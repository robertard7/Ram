using System.IO;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class CSharpGenerationArgumentResolverService
{
    public CSharpGenerationArgumentContractRecord Resolve(
        ToolRequest request,
        string relativePath,
        string namespaceName = "",
        string projectName = "",
        string projectPath = "",
        string retrievalReadinessStatus = "",
        string workspaceTruthFingerprint = "")
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        var modificationIntent = NormalizeModificationIntent(GetFirstArgument(request, "modification_intent"));
        var pattern = NormalizePattern(GetFirstArgument(request, "pattern", "declared_pattern"));
        var fileRole = GetFirstArgument(request, "file_role", "role");
        var depth = NormalizeImplementationDepth(GetFirstArgument(request, "implementation_depth", "depth"));
        var targetProject = GetFirstArgument(request, "target_project", "project");
        var namespaceValue = FirstNonEmpty(GetFirstArgument(request, "namespace", "declared_namespace"), namespaceName);
        var className = FirstNonEmpty(GetFirstArgument(request, "class_name"), InferClassName(normalizedPath));
        var domainEntity = FirstNonEmpty(GetFirstArgument(request, "domain_entity"), InferDomainEntity(className, pattern));
        var serviceName = FirstNonEmpty(GetFirstArgument(request, "service_name"), InferServiceName(className, pattern));
        var baseTypes = ParseList(GetFirstArgument(request, "base_types"));
        var interfaces = ParseList(GetFirstArgument(request, "interfaces"));
        var constructorDependencies = ParseList(GetFirstArgument(request, "constructor_dependencies"));
        var requiredUsings = ParseList(GetFirstArgument(request, "required_usings"));
        var supportingSurfaces = ParseList(GetFirstArgument(request, "supporting_surfaces"));
        var completionContract = ParseList(GetFirstArgument(request, "completion_contract"));
        var followThroughRequirements = ParseList(GetFirstArgument(request, "followthrough", "follow_through"));
        var explicitFollowThroughMode = NormalizeSlug(GetFirstArgument(request, "followthrough_mode"));
        var storageContext = GetFirstArgument(request, "storage_context");
        var testSubject = GetFirstArgument(request, "test_subject");
        var uiSurface = GetFirstArgument(request, "ui_surface");
        var featureName = NormalizeSlug(GetFirstArgument(request, "feature_name"));

        if (requiredUsings.Count == 0)
            requiredUsings = ResolveDefaultUsings(pattern, interfaces, constructorDependencies);
        if (baseTypes.Count == 0)
            baseTypes = ResolveDefaultBaseTypes(pattern);
        if (interfaces.Count == 0)
            interfaces = ResolveDefaultInterfaces(pattern, className, modificationIntent, featureName);
        if (supportingSurfaces.Count == 0)
            supportingSurfaces = ResolveDefaultSupportingSurfaces(pattern, className, interfaces, domainEntity, modificationIntent, featureName);

        var followThroughMode = !string.IsNullOrWhiteSpace(explicitFollowThroughMode)
            ? explicitFollowThroughMode
            : supportingSurfaces.Count > 0 || followThroughRequirements.Count > 0
                ? "planned_supporting_surfaces"
                : "single_file";

        return new CSharpGenerationArgumentContractRecord
        {
            ModificationIntent = modificationIntent,
            FileRole = fileRole,
            Pattern = pattern,
            ImplementationDepth = depth,
            FollowThroughMode = followThroughMode,
            TargetProject = FirstNonEmpty(targetProject, projectName),
            TargetProjectPath = NormalizeRelativePath(projectPath),
            TargetPath = normalizedPath,
            NamespaceName = namespaceValue,
            ClassName = className,
            BaseTypes = baseTypes,
            Interfaces = interfaces,
            ConstructorDependencies = constructorDependencies,
            RequiredUsings = requiredUsings,
            SupportingSurfaces = supportingSurfaces,
            CompletionContract = completionContract,
            DomainEntity = domainEntity,
            ServiceName = serviceName,
            StorageContext = storageContext,
            TestSubject = testSubject,
            UiSurface = uiSurface,
            FeatureName = featureName,
            RetrievalReadinessStatus = retrievalReadinessStatus,
            WorkspaceTruthFingerprint = workspaceTruthFingerprint
        };
    }

    public string NormalizeModificationIntent(string value)
    {
        var normalized = NormalizeSlug(value);
        return normalized switch
        {
            "" => "",
            "repair" => "repair",
            "patch" => "patch",
            "feature" => "feature_update",
            "featureupdate" => "feature_update",
            "feature_update" => "feature_update",
            _ => normalized
        };
    }

    public string NormalizeImplementationDepth(string value)
    {
        var normalized = NormalizeSlug(value);
        return normalized switch
        {
            "" => "standard",
            "scaffold" => "scaffold",
            "structural" => "scaffold",
            "basic" => "scaffold",
            "behavioral" => "standard",
            "standard" => "standard",
            "integrated" => "standard",
            "strong" => "strong",
            _ => normalized
        };
    }

    public string NormalizePattern(string value)
    {
        var normalized = NormalizeSlug(value);
        return normalized switch
        {
            "test_fixture" => "test_harness",
            "unit_test" => "test_harness",
            "dto_request" => "dto",
            "dto_response" => "dto",
            "request_response_contract" => "dto",
            "worker_service" => "worker_support",
            "worker_service_support" => "worker_support",
            _ => normalized
        };
    }

    private static List<string> ResolveDefaultUsings(string pattern, IReadOnlyList<string> interfaces, IReadOnlyList<string> constructorDependencies)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (pattern)
        {
            case "interface":
                values.Add("System.Collections.Generic");
                values.Add("System.Threading");
                values.Add("System.Threading.Tasks");
                break;
            case "service":
            case "repository":
                values.Add("System");
                values.Add("System.Collections.Generic");
                values.Add("System.Linq");
                values.Add("System.Threading");
                values.Add("System.Threading.Tasks");
                break;
            case "controller":
                values.Add("Microsoft.AspNetCore.Mvc");
                values.Add("System.Collections.Generic");
                values.Add("System.Linq");
                values.Add("System.Threading");
                values.Add("System.Threading.Tasks");
                break;
            case "viewmodel":
                values.Add("System");
                values.Add("System.Collections.ObjectModel");
                values.Add("System.ComponentModel");
                values.Add("System.Runtime.CompilerServices");
                values.Add("System.Windows.Input");
                break;
            case "test_harness":
                values.Add("System.Threading");
                values.Add("System.Threading.Tasks");
                values.Add("Xunit");
                break;
            case "worker_support":
                values.Add("System");
                values.Add("System.Threading");
                values.Add("System.Threading.Tasks");
                values.Add("Microsoft.Extensions.Hosting");
                values.Add("Microsoft.Extensions.Logging");
                break;
            case "dto":
                values.Add("System");
                break;
        }

        if (constructorDependencies.Any(value => value.Contains("ILogger", StringComparison.OrdinalIgnoreCase)))
            values.Add("Microsoft.Extensions.Logging");
        if (interfaces.Any(value => value.Contains("INotifyPropertyChanged", StringComparison.OrdinalIgnoreCase)))
        {
            values.Add("System.ComponentModel");
            values.Add("System.Runtime.CompilerServices");
        }

        return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ResolveDefaultBaseTypes(string pattern)
    {
        return pattern switch
        {
            "controller" => ["ControllerBase"],
            "worker_support" => ["BackgroundService"],
            _ => []
        };
    }

    private static List<string> ResolveDefaultInterfaces(string pattern, string className, string modificationIntent, string featureName)
    {
        if (string.Equals(pattern, "viewmodel", StringComparison.OrdinalIgnoreCase))
            return ["INotifyPropertyChanged"];

        if (string.Equals(pattern, "service", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(className)
            && !className.StartsWith("I", StringComparison.Ordinal))
        {
            return [$"I{className}"];
        }

        if (string.Equals(pattern, "repository", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(className)
            && !className.StartsWith("I", StringComparison.Ordinal))
        {
            return [$"I{className}"];
        }

        if (string.Equals(pattern, "controller", StringComparison.OrdinalIgnoreCase)
            && string.Equals(modificationIntent, "feature_update", StringComparison.OrdinalIgnoreCase)
            && string.Equals(featureName, "search", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(className))
        {
            return [$"I{TrimSuffixes(className, "Controller")}Service"];
        }

        return [];
    }

    private static List<string> ResolveDefaultSupportingSurfaces(string pattern, string className, IReadOnlyList<string> interfaces, string domainEntity, string modificationIntent, string featureName)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if ((string.Equals(pattern, "service", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pattern, "repository", StringComparison.OrdinalIgnoreCase))
            && interfaces.Count > 0)
        {
            foreach (var @interface in interfaces)
                values.Add($"interface:{@interface}.cs");
        }

        if (string.Equals(pattern, "service", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(domainEntity))
            values.Add($"model:{SanitizeIdentifier(domainEntity)}Record.cs");

        if (string.Equals(pattern, "repository", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(domainEntity))
            values.Add($"model:{SanitizeIdentifier(domainEntity)}Record.cs");

        if (string.Equals(pattern, "controller", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(domainEntity))
        {
            var entity = SanitizeIdentifier(domainEntity);
            values.Add($"request:Create{entity}Request.cs");
            values.Add($"response:{entity}Response.cs");
            if (string.Equals(modificationIntent, "feature_update", StringComparison.OrdinalIgnoreCase)
                && string.Equals(featureName, "search", StringComparison.OrdinalIgnoreCase))
            {
                values.Add($"request:Search{entity}Request.cs");
            }
        }

        if (string.Equals(pattern, "viewmodel", StringComparison.OrdinalIgnoreCase))
            values.Add("helper:DelegateCommand.cs");

        if (string.Equals(pattern, "worker_support", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(className))
        {
            var optionsStem = TrimSuffixes(className, "Service", "Worker");
            values.Add($"options:{SanitizeIdentifier(optionsStem)}Options.cs");
        }

        return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string InferClassName(string relativePath)
    {
        var stem = Path.GetFileNameWithoutExtension(relativePath);
        return string.IsNullOrWhiteSpace(stem) ? "" : stem.Trim();
    }

    private static string InferDomainEntity(string className, string pattern)
    {
        if (string.IsNullOrWhiteSpace(className))
            return "";

        if (string.Equals(pattern, "repository", StringComparison.OrdinalIgnoreCase))
            return TrimSuffixes(className, "Repository", "Store");
        if (string.Equals(pattern, "service", StringComparison.OrdinalIgnoreCase))
            return TrimSuffixes(className, "Service");
        if (string.Equals(pattern, "controller", StringComparison.OrdinalIgnoreCase))
            return TrimSuffixes(className, "Controller");

        return "";
    }

    private static string InferServiceName(string className, string pattern)
    {
        if (!string.Equals(pattern, "service", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(pattern, "controller", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return TrimSuffixes(className, "Service", "Controller");
    }

    private static string TrimSuffixes(string value, params string[] suffixes)
    {
        var result = value ?? "";
        foreach (var suffix in suffixes)
        {
            if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                result = result[..^suffix.Length];
                break;
            }
        }

        return string.IsNullOrWhiteSpace(result) ? (value ?? "") : result;
    }

    private static List<string> ParseList(string value)
    {
        return (value ?? "")
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
                return value.Trim();
        }

        return "";
    }

    private static string NormalizeSlug(string value)
    {
        var normalized = Regex.Replace((value ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_");
        return normalized.Trim('_');
    }

    private static string NormalizeRelativePath(string value)
    {
        return (value ?? "").Replace('\\', '/').Trim().Trim('/');
    }

    private static string SanitizeIdentifier(string value)
    {
        var tokens = Regex.Matches(value ?? "", @"[A-Za-z0-9]+")
            .Select(match => match.Value)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToList();
        if (tokens.Count == 0)
            return "";

        return string.Concat(tokens.Select(token => char.ToUpperInvariant(token[0]) + token[1..]));
    }
}
