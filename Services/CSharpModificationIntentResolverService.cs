using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class CSharpModificationIntentResolverService
{
    public const string ResolverVersion = "csharp_modification_intent.v1";

    public CSharpModificationIntentRecord ResolveForWrite(
        ToolRequest request,
        bool targetExists,
        string targetFilePath,
        string targetProject = "",
        string targetProjectPath = "")
    {
        var explicitIntent = NormalizeModificationIntent(GetFirstArgument(request, "modification_intent"));
        var requestedPattern = FirstNonEmpty(
            NormalizeToken(GetFirstArgument(request, "pattern")),
            NormalizeToken(GetFirstArgument(request, "file_role")),
            NormalizeToken(GetFirstArgument(request, "role")));
        var requestedRoleHint = NormalizeToken(GetFirstArgument(request, "role"));
        var resolvedOperationKind = FirstNonEmpty(NormalizeToken(request.ToolName), NormalizeToken(request.PreferredChainTemplateName));
        var targetSurfaceType = ResolveTargetSurfaceType(targetFilePath, requestedPattern, requestedRoleHint);

        if (!targetExists)
        {
            return new CSharpModificationIntentRecord
            {
                ResolverVersion = ResolverVersion,
                ModificationIntent = "",
                OperationKind = resolvedOperationKind,
                RequestedPattern = requestedPattern,
                RequestedRoleHint = requestedRoleHint,
                TargetSurfaceType = targetSurfaceType,
                TargetProject = FirstNonEmpty(targetProject, targetProjectPath),
                ClassificationReasons = ["target_surface_missing_requires_create_first"],
                Summary = $"Target `{DisplayValue(targetFilePath)}` is not present in workspace truth; resolve create-first scaffolding before entering the deterministic modification lane."
            };
        }

        var featureName = NormalizeToken(GetFirstArgument(request, "feature_name"));
        var repairCause = NormalizeToken(GetFirstArgument(request, "repair_cause"));
        var sourceText = string.Join(
            " ",
            new[]
            {
                request.Reason,
                GetFirstArgument(request, "task"),
                GetFirstArgument(request, "summary"),
                GetFirstArgument(request, "prompt"),
                GetFirstArgument(request, "notes"),
                requestedPattern,
                featureName,
                repairCause,
                targetProject,
                targetProjectPath,
                targetFilePath
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var resolved = ResolveCore(
            sourceText,
            explicitIntent,
            resolvedOperationKind,
            targetFilePath,
            requestedPattern,
            requestedRoleHint,
            featureName,
            repairCause,
            targetExists);
        resolved.TargetProject = FirstNonEmpty(targetProject, targetProjectPath);
        return resolved;
    }

    public CSharpModificationIntentRecord ResolveForExplicitIntent(
        string modificationIntent,
        string targetFilePath,
        string operationKind,
        string roleHint = "",
        string targetProject = "",
        string featureName = "",
        string repairCause = "")
    {
        var resolved = ResolveCore(
            string.Join(
                " ",
                new[]
                {
                    operationKind,
                    roleHint,
                    featureName,
                    repairCause,
                    targetProject,
                    targetFilePath
                }.Where(value => !string.IsNullOrWhiteSpace(value))),
            NormalizeModificationIntent(modificationIntent),
            NormalizeToken(operationKind),
            targetFilePath,
            NormalizeToken(operationKind),
            NormalizeToken(roleHint),
            NormalizeToken(featureName),
            NormalizeToken(repairCause),
            targetExists: !string.IsNullOrWhiteSpace(targetFilePath));
        resolved.TargetProject = targetProject;
        return resolved;
    }

    private CSharpModificationIntentRecord ResolveCore(
        string rawText,
        string explicitIntent,
        string normalizedOperationKind,
        string targetFilePath,
        string requestedPattern,
        string roleHint,
        string featureName,
        string repairCause,
        bool targetExists)
    {
        var normalizedText = NormalizeText(rawText);
        var reasons = new List<string>();
        var targetSurfaceType = ResolveTargetSurfaceType(targetFilePath, requestedPattern, roleHint);
        var modificationIntent = explicitIntent;
        if (!string.IsNullOrWhiteSpace(explicitIntent))
            reasons.Add($"explicit_modification_intent={explicitIntent}");

        if (string.IsNullOrWhiteSpace(modificationIntent) && IndicatesRepair(normalizedText, requestedPattern, normalizedOperationKind))
        {
            modificationIntent = "repair";
            reasons.Add("repair_phrase_family");
        }

        if (string.IsNullOrWhiteSpace(modificationIntent) && IndicatesFeatureUpdate(normalizedText, requestedPattern, normalizedOperationKind, featureName))
        {
            modificationIntent = "feature_update";
            reasons.Add("feature_update_phrase_family");
        }

        if (string.IsNullOrWhiteSpace(modificationIntent) && (targetExists || IndicatesPatch(normalizedText, requestedPattern, normalizedOperationKind)))
        {
            modificationIntent = "patch";
            reasons.Add(targetExists ? "existing_target_defaults_patch" : "patch_phrase_family");
        }

        if (string.IsNullOrWhiteSpace(featureName))
            featureName = InferFeatureName(normalizedText, requestedPattern, targetSurfaceType, modificationIntent);
        if (string.IsNullOrWhiteSpace(repairCause) && string.Equals(modificationIntent, "repair", StringComparison.OrdinalIgnoreCase))
            repairCause = InferRepairCause(normalizedText, requestedPattern);

        var followThroughMode = ResolveFollowThroughMode(modificationIntent, targetSurfaceType, requestedPattern, normalizedText);
        reasons.Add($"target_surface_type={DisplayValue(targetSurfaceType)}");
        reasons.Add($"followthrough_mode={DisplayValue(followThroughMode)}");
        if (!string.IsNullOrWhiteSpace(featureName))
            reasons.Add($"feature_name={featureName}");
        if (!string.IsNullOrWhiteSpace(repairCause))
            reasons.Add($"repair_cause={repairCause}");
        if (string.IsNullOrWhiteSpace(modificationIntent))
            reasons.Add("no_deterministic_modification_intent");

        return new CSharpModificationIntentRecord
        {
            ResolverVersion = ResolverVersion,
            ModificationIntent = modificationIntent,
            OperationKind = normalizedOperationKind,
            RequestedPattern = requestedPattern,
            RequestedRoleHint = roleHint,
            TargetSurfaceType = targetSurfaceType,
            FollowThroughMode = followThroughMode,
            RepairCause = repairCause,
            FeatureName = featureName,
            SuggestedCompletionContract = BuildSuggestedCompletionContract(modificationIntent, targetSurfaceType, normalizedText),
            SuggestedPreserveConstraints = BuildSuggestedPreserveConstraints(modificationIntent, targetSurfaceType),
            ClassificationReasons = reasons,
            Summary = string.IsNullOrWhiteSpace(modificationIntent)
                ? $"No deterministic modification intent resolved for `{DisplayValue(targetFilePath)}`."
                : $"Resolved `{modificationIntent}` for `{DisplayValue(targetFilePath)}` as `{DisplayValue(targetSurfaceType)}` with `{DisplayValue(followThroughMode)}`."
        };
    }

    private static List<string> BuildSuggestedCompletionContract(string modificationIntent, string targetSurfaceType, string normalizedText)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (modificationIntent)
        {
            case "repair":
                values.Add("bounded_mutation");
                values.Add("repair_root_cause_followthrough");
                values.Add("verification_followthrough");
                break;
            case "feature_update":
                values.Add("bounded_feature_extension");
                values.Add("supporting_surface_followthrough");
                values.Add("verification_followthrough");
                break;
            case "patch":
                values.Add("bounded_patch_completion");
                values.Add("preserve_public_interface_identity");
                break;
        }

        if (RequiresSupportingSurfaceFollowThrough(targetSurfaceType, normalizedText))
            values.Add("supporting_surface_followthrough");
        if (normalizedText.Contains("registration", StringComparison.OrdinalIgnoreCase)
            || normalizedText.Contains("bootstrap", StringComparison.OrdinalIgnoreCase))
        {
            values.Add("registration_followthrough");
        }

        if (normalizedText.Contains("test", StringComparison.OrdinalIgnoreCase))
            values.Add("test_update_followthrough");

        return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> BuildSuggestedPreserveConstraints(string modificationIntent, string targetSurfaceType)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "path_identity",
            "project_identity",
            "namespace_identity"
        };

        if (targetSurfaceType is "source" or "service" or "repository" or "controller" or "viewmodel" or "worker_support" or "dto" or "interface")
            values.Add("type_identity");
        if (modificationIntent is "repair" or "patch" or "feature_update")
            values.Add("public_api_identity");

        return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ResolveFollowThroughMode(string modificationIntent, string targetSurfaceType, string requestedPattern, string normalizedText)
    {
        if (string.Equals(modificationIntent, "repair", StringComparison.OrdinalIgnoreCase))
            return "repair_followthrough";
        if (string.Equals(modificationIntent, "feature_update", StringComparison.OrdinalIgnoreCase))
            return "planned_supporting_surfaces";
        if (RequiresSupportingSurfaceFollowThrough(targetSurfaceType, normalizedText)
            || requestedPattern is "service" or "repository" or "controller" or "viewmodel" or "worker_support" or "dto")
        {
            return "planned_supporting_surfaces";
        }

        return "single_file";
    }

    private static bool RequiresSupportingSurfaceFollowThrough(string targetSurfaceType, string normalizedText)
    {
        return targetSurfaceType is "service" or "repository" or "controller" or "viewmodel" or "worker_support" or "dto"
            || normalizedText.Contains("registration", StringComparison.OrdinalIgnoreCase)
            || normalizedText.Contains("bootstrap", StringComparison.OrdinalIgnoreCase)
            || normalizedText.Contains("dto", StringComparison.OrdinalIgnoreCase)
            || normalizedText.Contains("viewmodel", StringComparison.OrdinalIgnoreCase)
            || normalizedText.Contains("worker", StringComparison.OrdinalIgnoreCase)
            || normalizedText.Contains("test", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IndicatesRepair(string normalizedText, string requestedPattern, string operationKind)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return false;

        return ContainsAny(
                normalizedText,
                "fix",
                "repair",
                "resolve",
                "recover",
                "restore",
                "unblock",
                "broken",
                "failing",
                "failure",
                "compile error",
                "build failure",
                "test failure",
                "drift",
                "regression",
                "missing using",
                "missing namespace",
                "missing registration",
                "dependency wiring")
            || operationKind.Contains("repair", StringComparison.OrdinalIgnoreCase)
            || requestedPattern.Contains("repair", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IndicatesFeatureUpdate(string normalizedText, string requestedPattern, string operationKind, string featureName)
    {
        if (!string.IsNullOrWhiteSpace(featureName))
            return true;

        return ContainsAny(
                normalizedText,
                "feature update",
                "feature",
                "extend",
                "extension",
                "support new",
                "add endpoint",
                "add command",
                "add property",
                "add option",
                "search",
                "filter",
                "export",
                "import",
                "paging",
                "pagination",
                "logging")
            || ((requestedPattern is "controller" or "service") && ContainsAny(normalizedText, "extend", "new behavior", "new field"))
            || operationKind.Contains("feature", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IndicatesPatch(string normalizedText, string requestedPattern, string operationKind)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return false;

        return ContainsAny(
                normalizedText,
                "patch",
                "complete",
                "finish",
                "wire",
                "strengthen",
                "augment",
                "update existing",
                "implement missing",
                "bounded update",
                "bounded patch",
                "incomplete")
            || operationKind.Contains("patch", StringComparison.OrdinalIgnoreCase)
            || requestedPattern is "service" or "repository" or "controller" or "viewmodel" or "worker_support";
    }

    private static string InferFeatureName(string normalizedText, string requestedPattern, string targetSurfaceType, string modificationIntent)
    {
        if (!string.Equals(modificationIntent, "feature_update", StringComparison.OrdinalIgnoreCase))
            return "";
        if (ContainsAny(normalizedText, "search"))
            return "search";
        if (ContainsAny(normalizedText, "filter"))
            return "filter";
        if (ContainsAny(normalizedText, "export"))
            return "export";
        if (ContainsAny(normalizedText, "import"))
            return "import";
        if (ContainsAny(normalizedText, "logging"))
            return "logging";
        if (ContainsAny(normalizedText, "options"))
            return "options";
        if (ContainsAny(normalizedText, "command"))
            return "command";
        if (ContainsAny(normalizedText, "property"))
            return "property";
        return FirstNonEmpty(requestedPattern, targetSurfaceType);
    }

    private static string InferRepairCause(string normalizedText, string requestedPattern)
    {
        if (ContainsAny(normalizedText, "test failure", "failing test"))
            return "test_failure";
        if (ContainsAny(normalizedText, "build failure", "compile error", "broken build"))
            return "build_failure";
        if (ContainsAny(normalizedText, "missing using"))
            return "missing_using";
        if (ContainsAny(normalizedText, "missing namespace"))
            return "namespace_identity_drift";
        if (ContainsAny(normalizedText, "missing registration", "dependency wiring", "bootstrap"))
            return "registration_drift";
        if (ContainsAny(normalizedText, "drift") || requestedPattern is "interface" or "service" or "repository")
            return "interface_implementation_drift";
        return "bounded_repair";
    }

    private static string ResolveTargetSurfaceType(string targetFilePath, string requestedPattern, string roleHint)
    {
        var normalizedPath = NormalizePath(targetFilePath);
        if (normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return "project";
        if (normalizedPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return "solution";
        if (normalizedPath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            return "xaml";
        if (requestedPattern is "service" or "repository" or "controller" or "viewmodel" or "worker_support" or "dto" or "interface")
            return requestedPattern;
        if (roleHint is "services" or "repository" or "ui" or "tests")
            return roleHint switch
            {
                "services" => "service",
                "ui" => "viewmodel",
                _ => roleHint
            };
        if (normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return "source";
        return "";
    }

    private static bool ContainsAny(string normalizedText, params string[] phrases)
    {
        return phrases.Any(phrase => normalizedText.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeModificationIntent(string value)
    {
        return NormalizeToken(value) switch
        {
            "" => "",
            "repair" or "fix" or "hotfix" or "bugfix" or "correct" => "repair",
            "patch" or "update" or "complete" or "finish" => "patch",
            "feature" or "extend" or "extension" or "add_feature" or "featureupdate" or "feature_update" => "feature_update",
            _ => NormalizeToken(value)
        };
    }

    private static string NormalizeText(string value)
    {
        var normalized = (value ?? "").ToLowerInvariant().Replace('\\', '/');
        normalized = Regex.Replace(normalized, @"[^a-z0-9./_]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.Trim();
    }

    private static string NormalizeToken(string value)
    {
        return NormalizeText(value).Replace(' ', '_');
    }

    private static string NormalizePath(string value)
    {
        return (value ?? "").Replace('\\', '/').Trim().Trim('/');
    }

    private static string GetFirstArgument(ToolRequest request, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (request.TryGetArgument(key, out var value))
                return value;
        }

        return "";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
