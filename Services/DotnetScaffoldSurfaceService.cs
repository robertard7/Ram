using RAM.Models;

namespace RAM.Services;

public sealed class DotnetScaffoldSurfaceService
{
    public const string MatrixVersion = "dotnet_scaffold_surface.v1";

    private static readonly DotnetScaffoldSurfaceMatrixRecord Matrix = new()
    {
        MatrixVersion = MatrixVersion,
        Templates =
        [
            CreateSupported(
                "classlib",
                "classlib",
                "Class Library",
                "src",
                "library",
                "net8.0",
                [],
                ["sibling_library", "core_supporting_project"],
                "Deterministic class-library scaffold for core, contracts, storage, services, and shared library roles."),
            CreateSupported(
                "console",
                "console",
                "Console Application",
                "src",
                "app",
                "net8.0",
                [],
                ["app_project", "console_shape"],
                "Deterministic console application scaffold for command-line or batch-hosted app surfaces."),
            CreateSupported(
                "wpf",
                "wpf",
                "WPF Application",
                "src",
                "ui",
                "net8.0",
                [],
                ["app_project", "desktop_shell"],
                "Deterministic WPF application scaffold for desktop-shell solutions."),
            CreateSupported(
                "xunit",
                "xunit",
                "xUnit Test Project",
                "tests",
                "tests",
                "net8.0",
                [],
                ["test_project", "verification_surface"],
                "Deterministic xUnit scaffold for bounded .NET verification and harness flows."),
            CreateSupported(
                "webapi",
                "webapi",
                "ASP.NET Core Web API",
                "src",
                "api",
                "net8.0",
                ["--use-controllers", "--no-openapi"],
                ["app_project", "web_api_shape"],
                "Deterministic ASP.NET Core Web API scaffold with controllers enabled and OpenAPI disabled for bounded local create/build flows."),
            CreateSupported(
                "worker",
                "worker",
                "Worker Service",
                "src",
                "worker",
                "net8.0",
                [],
                ["app_project", "background_service_shape"],
                "Deterministic worker-service scaffold for hosted background process solutions."),
            CreateDeferred(
                "mstest",
                "mstest",
                "MSTest Project",
                "tests",
                "tests",
                "net8.0",
                "Explicitly deferred in Stage 1.0; xUnit remains the supported deterministic test template."),
            CreateDeferred(
                "nunit",
                "nunit",
                "NUnit Project",
                "tests",
                "tests",
                "net8.0",
                "Explicitly deferred in Stage 1.0; xUnit remains the supported deterministic test template."),
            CreateDeferred(
                "razorclasslib",
                "razorclasslib",
                "Razor Class Library",
                "src",
                "web",
                "net8.0",
                "Deferred until web-specific file-write and composition phases are in scope."),
            CreateDeferred(
                "blazor",
                "blazor",
                "Blazor Application",
                "src",
                "web",
                "net8.0",
                "Deferred until browser/web composition is explicitly in scope."),
            CreateDeferred(
                "winforms",
                "winforms",
                "Windows Forms Application",
                "src",
                "ui",
                "net8.0",
                "Deferred in Stage 1.0 to avoid half-supported desktop template drift.")
        ]
    };

    public DotnetScaffoldSurfaceMatrixRecord GetMatrix()
    {
        return new DotnetScaffoldSurfaceMatrixRecord
        {
            MatrixVersion = Matrix.MatrixVersion,
            Templates = Matrix.Templates
                .Select(Clone)
                .OrderBy(template => template.TemplateId, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    public bool TryResolve(string templateHint, out DotnetScaffoldTemplateRecord template)
    {
        var normalizedHint = NormalizeTemplateHint(templateHint);
        template = Matrix.Templates
            .FirstOrDefault(candidate =>
                string.Equals(candidate.TemplateId, normalizedHint, StringComparison.OrdinalIgnoreCase)
                || candidate.Aliases.Any(alias => string.Equals(alias, normalizedHint, StringComparison.OrdinalIgnoreCase)))
            ?? new DotnetScaffoldTemplateRecord();
        return !string.IsNullOrWhiteSpace(template.TemplateId);
    }

    public bool TryResolveSupported(string templateHint, out DotnetScaffoldTemplateRecord template)
    {
        if (TryResolve(templateHint, out template)
            && string.Equals(template.SupportStatus, "supported_complete", StringComparison.OrdinalIgnoreCase))
        {
            template = Clone(template);
            return true;
        }

        template = new DotnetScaffoldTemplateRecord();
        return false;
    }

    public string NormalizeTemplate(string templateHint)
    {
        return TryResolve(templateHint, out var template)
            ? template.TemplateId
            : NormalizeTemplateHint(templateHint);
    }

    public string ResolveDefaultProjectRoot(string templateHint)
    {
        return TryResolve(templateHint, out var template)
            ? template.DefaultProjectRoot
            : "src";
    }

    public string ResolveDefaultRole(string templateHint, string projectName = "", string explicitRole = "")
    {
        if (!string.IsNullOrWhiteSpace(explicitRole))
            return explicitRole.Trim();

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            if (projectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
                return "tests";
            if (projectName.EndsWith(".Api", StringComparison.OrdinalIgnoreCase))
                return "api";
            if (projectName.EndsWith(".Worker", StringComparison.OrdinalIgnoreCase))
                return "worker";
            if (projectName.EndsWith(".Console", StringComparison.OrdinalIgnoreCase))
                return "app";
            if (projectName.EndsWith(".Core", StringComparison.OrdinalIgnoreCase))
                return "core";
            if (projectName.EndsWith(".Storage", StringComparison.OrdinalIgnoreCase))
                return "storage";
            if (projectName.EndsWith(".Services", StringComparison.OrdinalIgnoreCase))
                return "services";
            if (projectName.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase))
                return "contracts";
            if (projectName.EndsWith(".Repository", StringComparison.OrdinalIgnoreCase))
                return "repository";
        }

        return TryResolve(templateHint, out var template)
            ? template.DefaultRole
            : "";
    }

    public string ResolveTargetFramework(string templateHint, string explicitTargetFramework = "")
    {
        var normalizedExplicit = (explicitTargetFramework ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(normalizedExplicit))
            return normalizedExplicit;

        return TryResolve(templateHint, out var template)
            ? template.DefaultTargetFramework
            : "";
    }

    public string ResolveDefaultSwitches(string templateHint, string explicitSwitches = "")
    {
        var normalizedExplicit = NormalizeSwitches(explicitSwitches);
        if (!TryResolve(templateHint, out var template))
            return normalizedExplicit;

        var switches = template.DefaultSwitches
            .Concat(SplitSwitches(normalizedExplicit))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return string.Join(" ", switches);
    }

    public string ResolveSupportStatus(string templateHint)
    {
        return TryResolve(templateHint, out var template)
            ? template.SupportStatus
            : "missing";
    }

    public string ResolveSummary(string templateHint)
    {
        return TryResolve(templateHint, out var template)
            ? template.Summary
            : "Template is outside the authoritative deterministic scaffold matrix.";
    }

    public string InferTemplateFromProjectIdentity(string projectName, string projectPath, string fallbackTemplate = "")
    {
        var normalizedProjectName = NormalizeProjectIdentity(projectName);
        var normalizedProjectPath = NormalizeProjectIdentity(projectPath);
        if (normalizedProjectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || normalizedProjectName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || normalizedProjectPath.Contains(".Tests.", StringComparison.OrdinalIgnoreCase))
        {
            return "xunit";
        }

        if (normalizedProjectName.EndsWith(".Api", StringComparison.OrdinalIgnoreCase)
            || normalizedProjectName.EndsWith(".Web", StringComparison.OrdinalIgnoreCase)
            || normalizedProjectPath.Contains(".Api.", StringComparison.OrdinalIgnoreCase)
            || normalizedProjectPath.Contains(".Web.", StringComparison.OrdinalIgnoreCase))
        {
            return "webapi";
        }

        if (normalizedProjectName.EndsWith(".Worker", StringComparison.OrdinalIgnoreCase)
            || normalizedProjectPath.Contains(".Worker.", StringComparison.OrdinalIgnoreCase))
        {
            return "worker";
        }

        if (normalizedProjectName.EndsWith(".Console", StringComparison.OrdinalIgnoreCase)
            || normalizedProjectPath.Contains(".Console.", StringComparison.OrdinalIgnoreCase))
        {
            return "console";
        }

        if (normalizedProjectName.EndsWith(".Core", StringComparison.OrdinalIgnoreCase)
            || normalizedProjectName.EndsWith(".Storage", StringComparison.OrdinalIgnoreCase)
            || normalizedProjectName.EndsWith(".Services", StringComparison.OrdinalIgnoreCase)
            || normalizedProjectName.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase)
            || normalizedProjectName.EndsWith(".Repository", StringComparison.OrdinalIgnoreCase))
        {
            return "classlib";
        }

        return NormalizeTemplate(FirstNonEmpty(fallbackTemplate, "wpf"));
    }

    private static DotnetScaffoldTemplateRecord CreateSupported(
        string templateId,
        string dotnetTemplateName,
        string displayName,
        string defaultProjectRoot,
        string defaultRole,
        string defaultTargetFramework,
        IReadOnlyList<string> defaultSwitches,
        IReadOnlyList<string> compositionTags,
        string summary)
    {
        return new DotnetScaffoldTemplateRecord
        {
            TemplateId = templateId,
            DotnetTemplateName = dotnetTemplateName,
            DisplayName = displayName,
            SupportStatus = "supported_complete",
            DefaultProjectRoot = defaultProjectRoot,
            DefaultRole = defaultRole,
            DefaultTargetFramework = defaultTargetFramework,
            Aliases = BuildAliases(templateId),
            DefaultSwitches = [.. defaultSwitches],
            CompositionTags = [.. compositionTags],
            Summary = summary
        };
    }

    private static DotnetScaffoldTemplateRecord CreateDeferred(
        string templateId,
        string dotnetTemplateName,
        string displayName,
        string defaultProjectRoot,
        string defaultRole,
        string defaultTargetFramework,
        string summary)
    {
        return new DotnetScaffoldTemplateRecord
        {
            TemplateId = templateId,
            DotnetTemplateName = dotnetTemplateName,
            DisplayName = displayName,
            SupportStatus = "deferred",
            DefaultProjectRoot = defaultProjectRoot,
            DefaultRole = defaultRole,
            DefaultTargetFramework = defaultTargetFramework,
            Aliases = BuildAliases(templateId),
            Summary = summary
        };
    }

    private static DotnetScaffoldTemplateRecord Clone(DotnetScaffoldTemplateRecord template)
    {
        return new DotnetScaffoldTemplateRecord
        {
            TemplateId = template.TemplateId,
            DotnetTemplateName = template.DotnetTemplateName,
            DisplayName = template.DisplayName,
            SupportStatus = template.SupportStatus,
            DefaultProjectRoot = template.DefaultProjectRoot,
            DefaultRole = template.DefaultRole,
            DefaultTargetFramework = template.DefaultTargetFramework,
            Aliases = [.. template.Aliases],
            DefaultSwitches = [.. template.DefaultSwitches],
            CompositionTags = [.. template.CompositionTags],
            Summary = template.Summary
        };
    }

    private static List<string> BuildAliases(string templateId)
    {
        return templateId switch
        {
            "classlib" => ["classlib", "class-library", "class library", "library"],
            "console" => ["console", "consoleapp", "console-app", "console app"],
            "wpf" => ["wpf", "desktop", "desktop-app", "desktop app", "windows app", "window app", "client project"],
            "xunit" => ["xunit", "test", "test-project", "test project", "tests"],
            "webapi" => ["webapi", "web-api", "web api", "api", "aspnet-webapi", "asp.net core web api", "aspnet core web api"],
            "worker" => ["worker", "worker-service", "worker service", "background-worker", "background worker"],
            "mstest" => ["mstest", "ms test"],
            "nunit" => ["nunit"],
            "razorclasslib" => ["razorclasslib", "razor-class-library", "razor class library"],
            "blazor" => ["blazor", "blazorwebapp", "blazor web app"],
            "winforms" => ["winforms", "windows forms", "windows-forms"],
            _ => [templateId]
        };
    }

    private static string NormalizeTemplateHint(string templateHint)
    {
        return (templateHint ?? "")
            .Trim()
            .Trim('"', '\'')
            .Replace("_", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal)
            .Replace(".", " ", StringComparison.Ordinal)
            .ToLowerInvariant()
            .Replace("asp net", "aspnet", StringComparison.Ordinal)
            .Replace("web api", "webapi", StringComparison.Ordinal)
            .Replace("worker service", "worker", StringComparison.Ordinal)
            .Replace("console app", "console", StringComparison.Ordinal)
            .Replace("class library", "classlib", StringComparison.Ordinal)
            .Replace("test project", "xunit", StringComparison.Ordinal)
            .Replace("desktop app", "wpf", StringComparison.Ordinal)
            .Replace("windows app", "wpf", StringComparison.Ordinal)
            .Replace("window app", "wpf", StringComparison.Ordinal)
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static string NormalizeSwitches(string rawSwitches)
    {
        return string.Join(" ", SplitSwitches(rawSwitches));
    }

    private static IEnumerable<string> SplitSwitches(string rawSwitches)
    {
        return (rawSwitches ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.StartsWith("--", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeProjectIdentity(string value)
    {
        return (value ?? "")
            .Trim()
            .Replace('\\', '.')
            .Replace('/', '.');
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
    }
}
