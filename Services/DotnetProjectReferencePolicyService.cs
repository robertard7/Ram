using System.IO;
using System.Xml.Linq;

namespace RAM.Services;

public enum DotnetProjectRole
{
    Unknown,
    Application,
    Core,
    Storage,
    Services,
    Tests,
    Library
}

public enum DotnetProjectReferenceDecisionKind
{
    None,
    Allow,
    Corrected,
    Blocked
}

public enum DotnetProjectReferenceCompatibilityKind
{
    Unknown,
    Compatible,
    Incompatible
}

public sealed class DotnetProjectReferenceDecision
{
    public DotnetProjectReferenceDecisionKind DecisionKind { get; set; } = DotnetProjectReferenceDecisionKind.None;
    public string DecisionCode { get; set; } = "";
    public string AttemptedProjectPath { get; set; } = "";
    public string AttemptedReferencePath { get; set; } = "";
    public string EffectiveProjectPath { get; set; } = "";
    public string EffectiveReferencePath { get; set; } = "";
    public string DirectionRuleId { get; set; } = "";
    public string DirectionRuleSummary { get; set; } = "";
    public string AttemptedFrameworkSummary { get; set; } = "";
    public string EffectiveFrameworkSummary { get; set; } = "";
    public DotnetProjectReferenceCompatibilityKind CompatibilityKind { get; set; } = DotnetProjectReferenceCompatibilityKind.Unknown;
    public string CompatibilitySummary { get; set; } = "";
    public string DecisionSummary { get; set; } = "";
    public int RulePriority { get; set; } = int.MaxValue;
    public bool ShouldExecute => DecisionKind is DotnetProjectReferenceDecisionKind.Allow or DotnetProjectReferenceDecisionKind.Corrected;
}

public sealed class DotnetProjectReferencePolicyService
{
    public DotnetProjectReferenceDecision Evaluate(string workspaceRoot, string projectPath, string referencePath)
    {
        var attemptedProjectPath = NormalizeRelativePath(projectPath);
        var attemptedReferencePath = NormalizeRelativePath(referencePath);
        if (string.IsNullOrWhiteSpace(attemptedProjectPath) || string.IsNullOrWhiteSpace(attemptedReferencePath))
        {
            return new DotnetProjectReferenceDecision
            {
                DecisionKind = DotnetProjectReferenceDecisionKind.Blocked,
                DecisionCode = "missing_project_reference_target",
                AttemptedProjectPath = attemptedProjectPath,
                AttemptedReferencePath = attemptedReferencePath,
                EffectiveProjectPath = attemptedProjectPath,
                EffectiveReferencePath = attemptedReferencePath,
                DecisionSummary = $"blocked_project_reference_policy: attempted_source={attemptedProjectPath} attempted_reference={attemptedReferencePath} decision=missing_project_reference_target"
            };
        }

        if (PathsResolveToSameWorkspaceTarget(workspaceRoot, attemptedProjectPath, attemptedReferencePath))
        {
            return new DotnetProjectReferenceDecision
            {
                DecisionKind = DotnetProjectReferenceDecisionKind.Blocked,
                DecisionCode = "blocked_self_project_reference",
                AttemptedProjectPath = attemptedProjectPath,
                AttemptedReferencePath = attemptedReferencePath,
                EffectiveProjectPath = attemptedProjectPath,
                EffectiveReferencePath = attemptedReferencePath,
                DirectionRuleId = "self_reference_forbidden",
                DirectionRuleSummary = "A project cannot reference itself.",
                CompatibilityKind = DotnetProjectReferenceCompatibilityKind.Incompatible,
                CompatibilitySummary = "self reference is never compatible",
                AttemptedFrameworkSummary = "source(self)->reference(self):incompatible",
                EffectiveFrameworkSummary = "source(self)->reference(self):incompatible",
                DecisionSummary = $"blocked_self_project_reference: attempted_source={attemptedProjectPath} attempted_reference={attemptedReferencePath} direction_rule=self_reference_forbidden frameworks=source(self)->reference(self):incompatible decision=blocked"
            };
        }

        var attemptedSource = LoadProjectInfo(workspaceRoot, attemptedProjectPath);
        var attemptedReference = LoadProjectInfo(workspaceRoot, attemptedReferencePath);
        var attemptedRule = EvaluateDirection(attemptedSource.Role, attemptedReference.Role);
        var attemptedCompatibility = EvaluateCompatibility(attemptedSource.Frameworks, attemptedReference.Frameworks);
        var attemptedFrameworkSummary = BuildFrameworkSummary(attemptedSource.Frameworks, attemptedReference.Frameworks, attemptedCompatibility);

        if (attemptedRule.Allowed && attemptedCompatibility != DotnetProjectReferenceCompatibilityKind.Incompatible)
        {
            var compatibilitySummary = attemptedCompatibility switch
            {
                DotnetProjectReferenceCompatibilityKind.Compatible => "frameworks are compatible",
                DotnetProjectReferenceCompatibilityKind.Unknown => "framework compatibility could not be fully resolved, but no incompatibility was detected",
                _ => "frameworks are incompatible"
            };
            return new DotnetProjectReferenceDecision
            {
                DecisionKind = DotnetProjectReferenceDecisionKind.Allow,
                DecisionCode = "execute_project_reference",
                AttemptedProjectPath = attemptedProjectPath,
                AttemptedReferencePath = attemptedReferencePath,
                EffectiveProjectPath = attemptedProjectPath,
                EffectiveReferencePath = attemptedReferencePath,
                DirectionRuleId = attemptedRule.RuleId,
                DirectionRuleSummary = attemptedRule.Summary,
                AttemptedFrameworkSummary = attemptedFrameworkSummary,
                EffectiveFrameworkSummary = attemptedFrameworkSummary,
                CompatibilityKind = attemptedCompatibility,
                CompatibilitySummary = compatibilitySummary,
                DecisionSummary = $"project_reference_allowed: source={attemptedProjectPath} reference={attemptedReferencePath} direction_rule={attemptedRule.RuleId} frameworks={attemptedFrameworkSummary} decision=execute",
                RulePriority = attemptedRule.Priority
            };
        }

        var reverseRule = EvaluateDirection(attemptedReference.Role, attemptedSource.Role);
        var reverseCompatibility = EvaluateCompatibility(attemptedReference.Frameworks, attemptedSource.Frameworks);
        var reverseFrameworkSummary = BuildFrameworkSummary(attemptedReference.Frameworks, attemptedSource.Frameworks, reverseCompatibility);
        if (reverseRule.Allowed && reverseCompatibility != DotnetProjectReferenceCompatibilityKind.Incompatible)
        {
            var compatibilitySummary = reverseCompatibility switch
            {
                DotnetProjectReferenceCompatibilityKind.Compatible => "frameworks are compatible after correcting the reference direction",
                DotnetProjectReferenceCompatibilityKind.Unknown => "framework compatibility could not be fully resolved, but the corrected direction avoids the known incompatible pairing",
                _ => "frameworks remain incompatible"
            };
            return new DotnetProjectReferenceDecision
            {
                DecisionKind = DotnetProjectReferenceDecisionKind.Corrected,
                DecisionCode = "corrected_reverse_project_reference",
                AttemptedProjectPath = attemptedProjectPath,
                AttemptedReferencePath = attemptedReferencePath,
                EffectiveProjectPath = attemptedReferencePath,
                EffectiveReferencePath = attemptedProjectPath,
                DirectionRuleId = reverseRule.RuleId,
                DirectionRuleSummary = reverseRule.Summary,
                AttemptedFrameworkSummary = attemptedFrameworkSummary,
                EffectiveFrameworkSummary = reverseFrameworkSummary,
                CompatibilityKind = reverseCompatibility,
                CompatibilitySummary = compatibilitySummary,
                DecisionSummary = $"project_reference_corrected: attempted_source={attemptedProjectPath} attempted_reference={attemptedReferencePath} effective_source={attemptedReferencePath} effective_reference={attemptedProjectPath} direction_rule={reverseRule.RuleId} attempted_frameworks={attemptedFrameworkSummary} effective_frameworks={reverseFrameworkSummary} decision=corrected_reverse_reference",
                RulePriority = reverseRule.Priority
            };
        }

        return new DotnetProjectReferenceDecision
        {
            DecisionKind = DotnetProjectReferenceDecisionKind.Blocked,
            DecisionCode = "blocked_incompatible_project_reference",
            AttemptedProjectPath = attemptedProjectPath,
            AttemptedReferencePath = attemptedReferencePath,
            EffectiveProjectPath = attemptedProjectPath,
            EffectiveReferencePath = attemptedReferencePath,
            DirectionRuleId = attemptedRule.RuleId,
            DirectionRuleSummary = attemptedRule.Summary,
            AttemptedFrameworkSummary = attemptedFrameworkSummary,
            EffectiveFrameworkSummary = reverseFrameworkSummary,
            CompatibilityKind = attemptedCompatibility == DotnetProjectReferenceCompatibilityKind.Incompatible
                ? DotnetProjectReferenceCompatibilityKind.Incompatible
                : reverseCompatibility,
            CompatibilitySummary = "no compatible architectural reference direction was found for the attempted project pair",
            DecisionSummary = $"blocked_project_reference_policy: attempted_source={attemptedProjectPath} attempted_reference={attemptedReferencePath} direction_rule={attemptedRule.RuleId} attempted_frameworks={attemptedFrameworkSummary} reverse_frameworks={reverseFrameworkSummary} decision=blocked"
        };
    }

    private static DirectionRule EvaluateDirection(DotnetProjectRole sourceRole, DotnetProjectRole referenceRole)
    {
        return sourceRole switch
        {
            DotnetProjectRole.Tests when referenceRole is not DotnetProjectRole.Tests
                => Allow("tests_reference_product_surface", "Test projects may depend on the product surface they validate.", 40),
            DotnetProjectRole.Application when referenceRole == DotnetProjectRole.Core
                => Allow("app_references_core", "The desktop app may depend on the Core/contracts library.", 10),
            DotnetProjectRole.Application when referenceRole == DotnetProjectRole.Storage
                => Allow("app_references_storage", "The desktop app may depend on the Storage library.", 15),
            DotnetProjectRole.Application when referenceRole == DotnetProjectRole.Services
                => Allow("app_references_services", "The desktop app may depend on shared service libraries.", 20),
            DotnetProjectRole.Application when referenceRole == DotnetProjectRole.Library
                => Allow("app_references_library", "The desktop app may depend on shared library projects.", 35),
            DotnetProjectRole.Storage when referenceRole is DotnetProjectRole.Core or DotnetProjectRole.Library
                => Allow("storage_references_shared_library", "Storage implementations may depend on Core/contracts or shared library projects.", 25),
            DotnetProjectRole.Services when referenceRole is DotnetProjectRole.Core or DotnetProjectRole.Library
                => Allow("services_references_shared_library", "Service libraries may depend on Core/contracts or shared library projects.", 30),
            DotnetProjectRole.Core when referenceRole == DotnetProjectRole.Library
                => Allow("core_references_shared_library", "Core libraries may depend on neutral shared libraries.", 50),
            DotnetProjectRole.Library when referenceRole is DotnetProjectRole.Core or DotnetProjectRole.Library
                => Allow("library_references_shared_library", "Neutral shared libraries may depend on other neutral/Core libraries.", 55),
            _ => Blocked($"{DescribeRole(sourceRole)}_must_not_reference_{DescribeRole(referenceRole)}", $"A {DescribeRoleText(sourceRole)} project must not reference a {DescribeRoleText(referenceRole)} project.")
        };
    }

    private static DotnetProjectReferenceCompatibilityKind EvaluateCompatibility(IReadOnlyList<string> sourceFrameworks, IReadOnlyList<string> referenceFrameworks)
    {
        if (sourceFrameworks.Count == 0 || referenceFrameworks.Count == 0)
            return DotnetProjectReferenceCompatibilityKind.Unknown;

        var sawKnownPair = false;
        foreach (var source in sourceFrameworks)
        {
            foreach (var reference in referenceFrameworks)
            {
                var result = IsCompatibleFrameworkPair(source, reference, out var knownPair);
                sawKnownPair |= knownPair;
                if (result)
                    return DotnetProjectReferenceCompatibilityKind.Compatible;
            }
        }

        return sawKnownPair
            ? DotnetProjectReferenceCompatibilityKind.Incompatible
            : DotnetProjectReferenceCompatibilityKind.Unknown;
    }

    private static bool IsCompatibleFrameworkPair(string sourceFramework, string referenceFramework, out bool knownPair)
    {
        knownPair = false;
        var source = ParseFramework(sourceFramework);
        var reference = ParseFramework(referenceFramework);
        if (!source.IsKnown || !reference.IsKnown)
            return string.Equals(sourceFramework, referenceFramework, StringComparison.OrdinalIgnoreCase);

        knownPair = true;
        if (string.Equals(source.Framework, reference.Framework, StringComparison.OrdinalIgnoreCase)
            && source.Version.CompareTo(reference.Version) >= 0)
        {
            if (string.IsNullOrWhiteSpace(reference.Platform))
                return true;

            return string.Equals(source.Platform, reference.Platform, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(reference.Framework, "netstandard", StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.Framework, "net", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static ProjectInfo LoadProjectInfo(string workspaceRoot, string relativePath)
    {
        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        var fullPath = Path.Combine(workspaceRoot, normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var document = LoadProjectDocument(fullPath);
        var frameworks = LoadFrameworks(document);
        var sdk = LoadSdk(document);
        var outputType = LoadOutputType(document);
        return new ProjectInfo
        {
            RelativePath = normalizedRelativePath,
            Name = Path.GetFileNameWithoutExtension(normalizedRelativePath),
            Frameworks = frameworks,
            Sdk = sdk,
            OutputType = outputType,
            Role = InferRole(normalizedRelativePath, frameworks, sdk, outputType)
        };
    }

    private static XDocument? LoadProjectDocument(string fullPath)
    {
        if (!File.Exists(fullPath))
            return null;

        try
        {
            return XDocument.Load(fullPath);
        }
        catch
        {
            return null;
        }
    }

    private static List<string> LoadFrameworks(XDocument? document)
    {
        var frameworks = new List<string>();
        if (document is null)
            return frameworks;

        foreach (var value in document.Descendants()
                     .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                     .Select(element => (element.Value ?? "").Trim())
                     .Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            frameworks.AddRange(value
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item)));
        }

        return frameworks
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string LoadSdk(XDocument? document)
    {
        return document?.Root?.Attribute("Sdk")?.Value?.Trim() ?? "";
    }

    private static string LoadOutputType(XDocument? document)
    {
        return document?.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "OutputType")
            ?.Value?
            .Trim() ?? "";
    }

    private static DotnetProjectRole InferRole(string relativePath, IReadOnlyList<string> frameworks, string sdk, string outputType)
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        var fileName = Path.GetFileNameWithoutExtension(normalizedPath);
        if (LooksLikeTestProject(normalizedPath, fileName))
            return DotnetProjectRole.Tests;
        if (ContainsAny(normalizedPath, fileName, ".core", "/core/", " core"))
            return DotnetProjectRole.Core;
        if (ContainsAny(normalizedPath, fileName, ".contracts", "/contracts/"))
            return DotnetProjectRole.Core;
        if (ContainsAny(normalizedPath, fileName, ".storage", "/storage/"))
            return DotnetProjectRole.Storage;
        if (ContainsAny(normalizedPath, fileName, ".services", "/services/"))
            return DotnetProjectRole.Services;
        if (string.Equals(sdk, "Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sdk, "Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase)
            || frameworks.Any(value => value.Contains("-windows", StringComparison.OrdinalIgnoreCase))
            || ContainsAny(normalizedPath, fileName, ".ui", "/ui/", "wpf", "shell"))
            return DotnetProjectRole.Application;
        if (frameworks.Count > 0)
            return DotnetProjectRole.Library;

        return DotnetProjectRole.Unknown;
    }

    private static string BuildFrameworkSummary(IReadOnlyList<string> sourceFrameworks, IReadOnlyList<string> referenceFrameworks, DotnetProjectReferenceCompatibilityKind compatibility)
    {
        var source = sourceFrameworks.Count == 0 ? "(unknown)" : string.Join(",", sourceFrameworks);
        var reference = referenceFrameworks.Count == 0 ? "(unknown)" : string.Join(",", referenceFrameworks);
        var state = compatibility switch
        {
            DotnetProjectReferenceCompatibilityKind.Compatible => "compatible",
            DotnetProjectReferenceCompatibilityKind.Incompatible => "incompatible",
            _ => "unknown"
        };
        return $"source({source})->reference({reference}):{state}";
    }

    private static ParsedFramework ParseFramework(string value)
    {
        var normalized = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return ParsedFramework.Unknown;

        var platformSeparator = normalized.IndexOf('-', StringComparison.Ordinal);
        var frameworkPortion = platformSeparator >= 0 ? normalized[..platformSeparator] : normalized;
        var platform = platformSeparator >= 0 ? normalized[(platformSeparator + 1)..] : "";
        if (frameworkPortion.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            return TryCreateFramework("netstandard", frameworkPortion["netstandard".Length..], platform);
        }

        if (frameworkPortion.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return TryCreateFramework("net", frameworkPortion["net".Length..], platform);
        }

        return ParsedFramework.Unknown;
    }

    private static ParsedFramework TryCreateFramework(string framework, string versionPortion, string platform)
    {
        if (Version.TryParse(versionPortion, out var version))
        {
            return new ParsedFramework(true, framework, version, platform);
        }

        return ParsedFramework.Unknown;
    }

    private static string NormalizeRelativePath(string? value)
    {
        return (value ?? "").Replace('\\', '/').Trim();
    }

    private static bool PathsResolveToSameWorkspaceTarget(string workspaceRoot, string left, string right)
    {
        try
        {
            var leftFull = Path.GetFullPath(Path.Combine(workspaceRoot, NormalizeRelativePath(left).Replace('/', Path.DirectorySeparatorChar)));
            var rightFull = Path.GetFullPath(Path.Combine(workspaceRoot, NormalizeRelativePath(right).Replace('/', Path.DirectorySeparatorChar)));
            return string.Equals(leftFull, rightFull, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(NormalizeRelativePath(left), NormalizeRelativePath(right), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool ContainsAny(string path, string fileName, params string[] fragments)
    {
        return fragments.Any(fragment =>
            (!string.IsNullOrWhiteSpace(path) && path.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(fileName) && fileName.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool LooksLikeTestProject(string normalizedPath, string fileName)
    {
        if (normalizedPath.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Contains("/test/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("Test", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeRole(DotnetProjectRole role)
    {
        return role switch
        {
            DotnetProjectRole.Application => "application",
            DotnetProjectRole.Core => "core",
            DotnetProjectRole.Storage => "storage",
            DotnetProjectRole.Services => "services",
            DotnetProjectRole.Tests => "tests",
            DotnetProjectRole.Library => "library",
            _ => "unknown"
        };
    }

    private static string DescribeRoleText(DotnetProjectRole role)
    {
        return role switch
        {
            DotnetProjectRole.Application => "desktop app/UI",
            DotnetProjectRole.Core => "Core/contracts",
            DotnetProjectRole.Storage => "Storage",
            DotnetProjectRole.Services => "Services",
            DotnetProjectRole.Tests => "test",
            DotnetProjectRole.Library => "shared library",
            _ => "unknown"
        };
    }

    private static DirectionRule Allow(string ruleId, string summary, int priority)
        => new(true, ruleId, summary, priority);

    private static DirectionRule Blocked(string ruleId, string summary)
        => new(false, ruleId, summary, int.MaxValue);

    private readonly record struct DirectionRule(bool Allowed, string RuleId, string Summary, int Priority);
    private readonly record struct ParsedFramework(bool IsKnown, string Framework, Version Version, string Platform)
    {
        public static ParsedFramework Unknown => new(false, "", new Version(0, 0), "");
    }

    private sealed class ProjectInfo
    {
        public string RelativePath { get; init; } = "";
        public string Name { get; init; } = "";
        public string Sdk { get; init; } = "";
        public string OutputType { get; init; } = "";
        public List<string> Frameworks { get; init; } = [];
        public DotnetProjectRole Role { get; init; }
    }
}
