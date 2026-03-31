using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class CommandCanonicalizationService
{
    public const string ResolverContractVersion = "command_canonicalization.v5";
    private static readonly DotnetScaffoldSurfaceService DotnetScaffoldSurfaceService = new();

    private static readonly Regex RelativePathPattern = new(
        @"(?<path>(?:src|tests|test|artifacts|docs|include)/[A-Za-z0-9_./\\-]+(?:\.[A-Za-z0-9]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FileNamePattern = new(
        @"(?<name>[A-Za-z0-9_./\\-]+)\.(?<ext>sln|csproj|cs|xaml)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedIdentifierPattern = new(
        @"[""'](?<value>[^""']+)[""']",
        RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitArgumentPattern = new(
        @"(?<!\S)(?<key>[A-Za-z_][A-Za-z0-9_\-]*)=(?<value>""[^""]*""|'[^']*'|[^\s]+)",
        RegexOptions.CultureInvariant);

    private static readonly string[] DirectoryPatterns =
    [
        "make dir",
        "make directory",
        "mkdir",
        "create directory",
        "create folder",
        "add folder",
        "scaffold folder",
        "ensure directory exists"
    ];

    private static readonly string[] CreateSolutionPatterns =
    [
        "create dotnet solution",
        "create solution",
        "make solution",
        "initialize solution",
        "scaffold solution",
        "setup solution",
        "create sln"
    ];

    private static readonly string[] AddProjectToSolutionPatterns =
    [
        "add project to solution",
        "attach project to solution",
        "include project in solution",
        "wire project into solution",
        "register project in solution",
        "add test project to solution",
        "add app project to solution"
    ];

    private static readonly string[] AddProjectReferencePatterns =
    [
        "add project reference",
        "add reference from",
        "add dependency reference",
        "wire project reference",
        "attach reference",
        "reference core library from app",
        "add app reference to core"
    ];

    private static readonly string[] BuildPatterns =
    [
        "run dotnet build",
        "build solution",
        "verify build",
        "validate build",
        "run workspace build verification",
        "rerun build",
        "check build",
        "ensure solution builds",
        "run build"
    ];

    private static readonly string[] TestPatterns =
    [
        "run dotnet test",
        "run test project",
        "test project",
        "verify tests",
        "validate tests",
        "rerun tests",
        "execute tests",
        "run solution tests",
        "run direct test target"
    ];

    private static readonly string[] WritePatterns =
    [
        "write file",
        "create file",
        "generate file",
        "scaffold file",
        "write contract",
        "write model",
        "write repository",
        "write store",
        "write page",
        "write shell",
        "write viewmodel",
        "create page",
        "create xaml page",
        "create viewmodel",
        "create contract",
        "create repository implementation"
    ];

    public CommandCanonicalizationRecord Canonicalize(
        string rawPhraseText,
        string workspaceRoot = "",
        string planImportId = "",
        string batchId = "",
        string workItemId = "",
        string workItemTitle = "")
    {
        var normalizedArguments = NormalizeArguments(ParseExplicitArguments(rawPhraseText));
        var normalizedPhrase = Normalize(RemoveExplicitArguments(rawPhraseText));
        var normalizedOperation = ResolveOperationKind(normalizedPhrase, normalizedArguments);
        var normalizedTemplate = ResolveTemplateHint(normalizedPhrase, normalizedOperation, normalizedArguments);
        var normalizedProjectName = ResolveExplicitName(rawPhraseText, normalizedPhrase, normalizedOperation, normalizedTemplate, normalizedArguments);
        var normalizedTargetPath = ResolveTargetPath(rawPhraseText, normalizedPhrase, normalizedOperation, normalizedProjectName, normalizedTemplate, normalizedArguments);
        var roleHint = ResolveRoleHint(normalizedTargetPath, normalizedPhrase, normalizedOperation, normalizedArguments);
        var argumentTrace = BuildArgumentTrace(normalizedArguments);

        var summary = string.IsNullOrWhiteSpace(normalizedOperation)
            ? $"Canonicalization did not find a deterministic operation. normalized_phrase={DisplayValue(normalizedPhrase)}"
            : $"Canonicalized `{DisplayValue(rawPhraseText)}` to operation `{normalizedOperation}` target={DisplayValue(normalizedTargetPath)} template={DisplayValue(normalizedTemplate)} args={DisplayValue(argumentTrace)}.";

        return new CommandCanonicalizationRecord
        {
            NormalizationId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            PlanImportId = planImportId,
            BatchId = batchId,
            WorkItemId = workItemId,
            WorkItemTitle = workItemTitle,
            RawPhraseText = rawPhraseText ?? "",
            NormalizedPhraseText = normalizedPhrase,
            NormalizedOperationKind = normalizedOperation,
            NormalizedTargetPath = normalizedTargetPath,
            NormalizedProjectName = normalizedProjectName,
            NormalizedTemplateHint = normalizedTemplate,
            TargetRoleHint = roleHint,
            NormalizedArguments = normalizedArguments,
            NormalizedArgumentTrace = argumentTrace,
            NormalizationTrace = $"raw={DisplayValue(rawPhraseText)} normalized={DisplayValue(normalizedPhrase)} operation={DisplayValue(normalizedOperation)} target={DisplayValue(normalizedTargetPath)} template={DisplayValue(normalizedTemplate)} role={DisplayValue(roleHint)} args={DisplayValue(argumentTrace)}",
            Summary = summary,
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private static string ResolveOperationKind(string normalizedPhrase, IReadOnlyDictionary<string, string> normalizedArguments)
    {
        if (MatchesAny(normalizedPhrase, DirectoryPatterns))
            return "filesystem.create_directory";
        if (MatchesAny(normalizedPhrase, AddProjectReferencePatterns))
            return "dotnet.add_project_reference";
        if (MatchesAny(normalizedPhrase, AddProjectToSolutionPatterns))
            return "dotnet.add_project_to_solution";
        if (MatchesAny(normalizedPhrase, CreateSolutionPatterns))
            return "dotnet.create_solution";
        if (LooksLikeCreateProject(normalizedPhrase))
            return ResolveProjectOperation(normalizedPhrase, normalizedArguments);
        if (MatchesAny(normalizedPhrase, BuildPatterns))
            return "dotnet.build";
        if (MatchesAny(normalizedPhrase, TestPatterns))
            return "dotnet.test";
        if (MatchesAny(normalizedPhrase, WritePatterns))
            return "file.write";

        return "";
    }

    private static string ResolveProjectOperation(string normalizedPhrase, IReadOnlyDictionary<string, string> normalizedArguments)
    {
        var templateArgument = DotnetScaffoldSurfaceService.NormalizeTemplate(GetArgument(normalizedArguments, "template"));
        if (string.Equals(templateArgument, "xunit", StringComparison.OrdinalIgnoreCase))
            return "dotnet.create_project.xunit";
        if (string.Equals(templateArgument, "wpf", StringComparison.OrdinalIgnoreCase))
            return "dotnet.create_project.wpf";
        if (string.Equals(templateArgument, "classlib", StringComparison.OrdinalIgnoreCase))
            return "dotnet.create_project.classlib";
        if (string.Equals(templateArgument, "console", StringComparison.OrdinalIgnoreCase))
            return "dotnet.create_project.console";
        if (string.Equals(templateArgument, "worker", StringComparison.OrdinalIgnoreCase))
            return "dotnet.create_project.worker";
        if (string.Equals(templateArgument, "webapi", StringComparison.OrdinalIgnoreCase))
            return "dotnet.create_project.webapi";
        if (MatchesAny(normalizedPhrase, "create xunit project", "create test project", "create dotnet test project", "create dotnet project xunit", "scaffold tests", "add test project"))
            return "dotnet.create_project.xunit";
        if (MatchesAny(normalizedPhrase, "create wpf project", "scaffold wpf app", "create desktop app project", "create window app", "make app project", "create client project"))
            return "dotnet.create_project.wpf";
        if (MatchesAny(normalizedPhrase, "create console project", "create console app", "create dotnet console app", "create console application"))
            return "dotnet.create_project.console";
        if (MatchesAny(normalizedPhrase, "create worker project", "create worker service", "create dotnet worker service", "create background worker"))
            return "dotnet.create_project.worker";
        if (MatchesAny(normalizedPhrase, "create web api", "create webapi", "create dotnet web api", "create asp.net core web api", "create api project"))
            return "dotnet.create_project.webapi";
        if (MatchesAny(normalizedPhrase, "create class library", "create classlib project", "create core project", "create contracts library", "create storage project", "create repository project"))
            return "dotnet.create_project.classlib";
        if (MatchesAny(normalizedPhrase, "create dotnet project", "create project"))
            return "dotnet.create_project";

        return "";
    }

    private static string ResolveTemplateHint(string normalizedPhrase, string normalizedOperation, IReadOnlyDictionary<string, string> normalizedArguments)
    {
        var explicitTemplate = DotnetScaffoldSurfaceService.NormalizeTemplate(GetArgument(normalizedArguments, "template"));
        if (!string.IsNullOrWhiteSpace(explicitTemplate))
            return explicitTemplate;

        if (normalizedOperation == "dotnet.create_project.wpf")
            return "wpf";
        if (normalizedOperation == "dotnet.create_project.classlib")
            return "classlib";
        if (normalizedOperation == "dotnet.create_project.xunit")
            return "xunit";
        if (normalizedOperation == "dotnet.create_project.console")
            return "console";
        if (normalizedOperation == "dotnet.create_project.worker")
            return "worker";
        if (normalizedOperation == "dotnet.create_project.webapi")
            return "webapi";
        if (normalizedOperation == "dotnet.create_project")
        {
            if (MatchesAny(normalizedPhrase, "wpf", "desktop app", "window app"))
                return "wpf";
            if (MatchesAny(normalizedPhrase, "web api", "webapi", "api project", "asp.net core web api"))
                return "webapi";
            if (MatchesAny(normalizedPhrase, "worker", "worker service", "background worker"))
                return "worker";
            if (MatchesAny(normalizedPhrase, "console", "console app", "console application"))
                return "console";
            if (MatchesAny(normalizedPhrase, "class library", "classlib", "core", "contracts", "storage", "repository"))
                return "classlib";
            if (MatchesAny(normalizedPhrase, "xunit", "test project", "tests"))
                return "xunit";
        }

        return "";
    }

    private static string ResolveExplicitName(string rawPhrase, string normalizedPhrase, string normalizedOperation, string normalizedTemplate, IReadOnlyDictionary<string, string> normalizedArguments)
    {
        var explicitName = FirstNonEmpty(
            GetArgument(normalizedArguments, "name"),
            normalizedOperation == "dotnet.create_solution"
                ? GetArgument(normalizedArguments, "solution")
                : GetArgument(normalizedArguments, "project"));
        if (!string.IsNullOrWhiteSpace(explicitName))
            return TrimSolutionExtension(explicitName);

        var fileMatch = FileNamePattern.Match(rawPhrase ?? "");
        if (fileMatch.Success)
            return SanitizeProjectName(Path.GetFileNameWithoutExtension(fileMatch.Groups["name"].Value.Replace('\\', '/')));

        var pathMatch = RelativePathPattern.Match(rawPhrase ?? "");
        if (pathMatch.Success)
        {
            var resolvedFromPath = Path.GetFileNameWithoutExtension(pathMatch.Groups["path"].Value.Replace('\\', '/'));
            if (!string.IsNullOrWhiteSpace(resolvedFromPath))
                return SanitizeProjectName(resolvedFromPath);
        }

        var quoted = QuotedIdentifierPattern.Match(rawPhrase ?? "");
        if (quoted.Success)
            return SanitizeProjectName(quoted.Groups["value"].Value);

        var candidateSource = NormalizeCandidateSource(rawPhrase);
        var tokens = candidateSource
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1)
            .ToList();
        if (tokens.Count == 0)
            return "";

        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "create", "dotnet", "solution", "project", "wpf", "classlib", "class", "library", "xunit",
            "console", "worker", "web", "api", "service", "application",
            "make", "initialize", "scaffold", "add", "to", "reference", "build", "test", "folder",
            "directory", "dir", "file", "run", "verify", "validate", "ensure", "exists"
        };

        var candidateTokens = tokens
            .Where(token => !stopWords.Contains(token))
            .Where(token => !token.Contains('/'))
            .Where(token => !token.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            .Where(token => !token.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidateTokens.Count == 0)
            return "";

        if (normalizedOperation == "dotnet.create_project.xunit" && candidateTokens.Count == 1)
        {
            var projectName = SanitizeProjectName(candidateTokens[0]);
            return projectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                ? projectName
                : $"{projectName}.Tests";
        }
        if (normalizedOperation == "dotnet.create_project.classlib"
            && MatchesAny(normalizedPhrase, "core", "contracts", "storage", "repository"))
        {
            var baseName = SanitizeProjectName(candidateTokens[^1]);
            if (MatchesAny(normalizedPhrase, "core"))
                return baseName.EndsWith(".Core", StringComparison.OrdinalIgnoreCase) ? baseName : $"{baseName}.Core";
            if (MatchesAny(normalizedPhrase, "contracts"))
                return baseName.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase) ? baseName : $"{baseName}.Contracts";
            if (MatchesAny(normalizedPhrase, "storage"))
                return baseName.EndsWith(".Storage", StringComparison.OrdinalIgnoreCase) ? baseName : $"{baseName}.Storage";
            if (MatchesAny(normalizedPhrase, "repository"))
                return baseName.EndsWith(".Repository", StringComparison.OrdinalIgnoreCase) ? baseName : $"{baseName}.Repository";
        }

        return SanitizeProjectName(candidateTokens[^1]);
    }

    private static string ResolveTargetPath(
        string rawPhrase,
        string normalizedPhrase,
        string normalizedOperation,
        string normalizedProjectName,
        string normalizedTemplate,
        IReadOnlyDictionary<string, string> normalizedArguments)
    {
        var explicitPath = FirstNonEmpty(GetArgument(normalizedArguments, "path"), ResolveExplicitPath(rawPhrase));
        var solutionArgument = GetArgument(normalizedArguments, "solution");
        var validationArgument = GetArgument(normalizedArguments, "validation");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (normalizedOperation == "filesystem.create_directory")
                return explicitPath;
            if (Path.HasExtension(explicitPath))
                return explicitPath;
        }

        return normalizedOperation switch
        {
            "filesystem.create_directory" => explicitPath,
            "dotnet.create_solution" when !string.IsNullOrWhiteSpace(solutionArgument) => EnsureSolutionPath(solutionArgument),
            "dotnet.create_solution" when !string.IsNullOrWhiteSpace(normalizedProjectName) => $"{normalizedProjectName}.sln",
            "dotnet.create_project.wpf" when !string.IsNullOrWhiteSpace(explicitPath) && !Path.HasExtension(explicitPath) && !string.IsNullOrWhiteSpace(normalizedProjectName) => NormalizeRelativePath(Path.Combine(explicitPath, $"{normalizedProjectName}.csproj")),
            "dotnet.create_project.classlib" when !string.IsNullOrWhiteSpace(explicitPath) && !Path.HasExtension(explicitPath) && !string.IsNullOrWhiteSpace(normalizedProjectName) => NormalizeRelativePath(Path.Combine(explicitPath, $"{normalizedProjectName}.csproj")),
            "dotnet.create_project.xunit" when !string.IsNullOrWhiteSpace(explicitPath) && !Path.HasExtension(explicitPath) && !string.IsNullOrWhiteSpace(normalizedProjectName) => NormalizeRelativePath(Path.Combine(explicitPath, $"{normalizedProjectName}.csproj")),
            "dotnet.create_project.console" when !string.IsNullOrWhiteSpace(explicitPath) && !Path.HasExtension(explicitPath) && !string.IsNullOrWhiteSpace(normalizedProjectName) => NormalizeRelativePath(Path.Combine(explicitPath, $"{normalizedProjectName}.csproj")),
            "dotnet.create_project.worker" when !string.IsNullOrWhiteSpace(explicitPath) && !Path.HasExtension(explicitPath) && !string.IsNullOrWhiteSpace(normalizedProjectName) => NormalizeRelativePath(Path.Combine(explicitPath, $"{normalizedProjectName}.csproj")),
            "dotnet.create_project.webapi" when !string.IsNullOrWhiteSpace(explicitPath) && !Path.HasExtension(explicitPath) && !string.IsNullOrWhiteSpace(normalizedProjectName) => NormalizeRelativePath(Path.Combine(explicitPath, $"{normalizedProjectName}.csproj")),
            "dotnet.create_project" when !string.IsNullOrWhiteSpace(explicitPath) && !Path.HasExtension(explicitPath) && !string.IsNullOrWhiteSpace(normalizedProjectName) => NormalizeRelativePath(Path.Combine(explicitPath, $"{normalizedProjectName}.csproj")),
            "dotnet.create_project.wpf" when !string.IsNullOrWhiteSpace(normalizedProjectName) => BuildProjectTargetPath(normalizedProjectName, "wpf"),
            "dotnet.create_project.classlib" when !string.IsNullOrWhiteSpace(normalizedProjectName) => BuildProjectTargetPath(normalizedProjectName, "classlib"),
            "dotnet.create_project.xunit" when !string.IsNullOrWhiteSpace(normalizedProjectName) => BuildProjectTargetPath(normalizedProjectName, "xunit"),
            "dotnet.create_project.console" when !string.IsNullOrWhiteSpace(normalizedProjectName) => BuildProjectTargetPath(normalizedProjectName, "console"),
            "dotnet.create_project.worker" when !string.IsNullOrWhiteSpace(normalizedProjectName) => BuildProjectTargetPath(normalizedProjectName, "worker"),
            "dotnet.create_project.webapi" when !string.IsNullOrWhiteSpace(normalizedProjectName) => BuildProjectTargetPath(normalizedProjectName, "webapi"),
            "dotnet.create_project" when !string.IsNullOrWhiteSpace(normalizedProjectName) => BuildProjectTargetPath(normalizedProjectName, normalizedTemplate),
            "dotnet.add_project_to_solution" when !string.IsNullOrWhiteSpace(solutionArgument) => EnsureSolutionPath(solutionArgument),
            "dotnet.build" or "dotnet.test" when !string.IsNullOrWhiteSpace(validationArgument) => NormalizeRelativePath(validationArgument),
            "dotnet.add_project_to_solution" or "dotnet.add_project_reference" or "dotnet.build" or "dotnet.test" => !string.IsNullOrWhiteSpace(explicitPath)
                ? explicitPath
                : ResolveExplicitBuildFilePath(rawPhrase),
            "file.write" => !string.IsNullOrWhiteSpace(explicitPath)
                ? explicitPath
                : ResolveExplicitBuildFilePath(rawPhrase),
            _ => ""
        };
    }

    private static string ResolveRoleHint(string normalizedTargetPath, string normalizedPhrase, string normalizedOperation, IReadOnlyDictionary<string, string> normalizedArguments)
    {
        var explicitRole = GetArgument(normalizedArguments, "role");
        if (!string.IsNullOrWhiteSpace(explicitRole))
            return explicitRole;

        if (normalizedOperation == "dotnet.test")
            return "tests";
        if (normalizedOperation == "dotnet.build" && MatchesAny(normalizedPhrase, "solution"))
            return "solution";
        if (normalizedOperation == "dotnet.create_project.wpf")
            return "ui";
        if (normalizedOperation == "dotnet.create_project.console")
            return "app";
        if (normalizedOperation == "dotnet.create_project.worker")
            return "worker";
        if (normalizedOperation == "dotnet.create_project.webapi")
            return "api";
        if (normalizedOperation == "dotnet.create_project.classlib")
        {
            if (MatchesAny(normalizedPhrase, "core"))
                return "core";
            if (MatchesAny(normalizedPhrase, "contracts"))
                return "contracts";
            if (MatchesAny(normalizedPhrase, "services", "service"))
                return "services";
            if (MatchesAny(normalizedPhrase, "storage"))
                return "storage";
            if (MatchesAny(normalizedPhrase, "repository"))
                return "repository";
        }

        var normalizedTarget = normalizedTargetPath.ToLowerInvariant();
        if (normalizedTarget.Contains("/state/"))
            return "state";
        if (normalizedTarget.Contains("/storage/"))
            return "storage";
        if (normalizedTarget.Contains("/contracts/"))
            return "contracts";
        if (normalizedTarget.Contains("/models/"))
            return "models";
        if (normalizedTarget.Contains("/views/"))
            return "views";
        if (normalizedTarget.Contains("/tests/") || normalizedTarget.Contains(".tests.", StringComparison.OrdinalIgnoreCase))
            return "tests";

        return "";
    }

    private static Dictionary<string, string> ParseExplicitArguments(string? rawPhrase)
    {
        var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rawPhrase))
            return arguments;

        foreach (Match match in ExplicitArgumentPattern.Matches(rawPhrase))
        {
            var key = NormalizeArgumentKey(match.Groups["key"].Value);
            var value = match.Groups["value"].Value.Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            arguments[key] = value;
        }

        return arguments;
    }

    private static Dictionary<string, string> NormalizeArguments(IReadOnlyDictionary<string, string> rawArguments)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in rawArguments)
        {
            var key = NormalizeArgumentKey(entry.Key);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var value = entry.Value?.Trim().Trim('"', '\'') ?? "";
            if (string.IsNullOrWhiteSpace(value))
                continue;

            normalized[key] = NormalizeArgumentValue(key, value);
        }

        return normalized;
    }

    private static string RemoveExplicitArguments(string? rawPhrase)
    {
        if (string.IsNullOrWhiteSpace(rawPhrase))
            return "";

        var stripped = ExplicitArgumentPattern.Replace(rawPhrase, " ");
        stripped = Regex.Replace(stripped, @"\s+", " ");
        return stripped.Trim();
    }

    private static string NormalizeArgumentKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "";

        var normalized = key.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            "project_name" => "name",
            "solution_name" => "solution",
            "solution_path" => "solution",
            "output_path" => "path",
            "namespace_name" => "namespace",
            "validation_target" => "validation",
            "validation_path" => "validation",
            "follow_through" => "followthrough",
            "referencefrom" => "reference_from",
            "referenceto" => "reference_to",
            _ => normalized
        };
    }

    private static string NormalizeArgumentValue(string key, string value)
    {
        return key switch
        {
            "template" => NormalizeTemplateArgument(value),
            "path" => NormalizeRelativePath(value),
            "solution" => EnsureSolutionPath(value),
            "namespace" => SanitizeProjectName(value),
            "name" => TrimSolutionExtension(SanitizeProjectName(value)),
            "project" => SanitizeProjectName(value),
            "reference_from" => SanitizeProjectName(value),
            "reference_to" => SanitizeProjectName(value),
            "role" => NormalizeSlug(value),
            "pattern" => NormalizeSlug(value),
            "depth" => NormalizeDepth(value),
            "followthrough" => NormalizeFollowThrough(value),
            "validation" => NormalizeValidationTarget(value),
            "attach" => NormalizeBoolean(value),
            _ => value.Trim()
        };
    }

    private static string NormalizeTemplateArgument(string value)
    {
        var normalized = NormalizeSlug(value);
        normalized = normalized switch
        {
            "class_library" => "classlib",
            "classlib" => "classlib",
            "test" or "test_project" or "xunit_project" => "xunit",
            "xunit" => "xunit",
            "desktop_app" or "window_app" or "windows_app" => "wpf",
            "wpf" => "wpf",
            "console_app" => "console",
            "console" => "console",
            "worker_service" => "worker",
            "worker" => "worker",
            "web_api" or "api_project" or "asp_net_core_web_api" or "aspnet_core_web_api" => "webapi",
            "webapi" => "webapi",
            _ => normalized
        };
        return DotnetScaffoldSurfaceService.NormalizeTemplate(normalized);
    }

    private static string NormalizeValidationTarget(string value)
    {
        var normalized = NormalizeRelativePath(value);
        if (normalized.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains('/'))
        {
            return normalized;
        }

        return value.Trim();
    }

    private static string NormalizeFollowThrough(string value)
    {
        var items = value
            .Split([',', '|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSlug)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return items.Count == 0 ? "" : string.Join(",", items);
    }

    private static string NormalizeBoolean(string value)
    {
        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("y", StringComparison.OrdinalIgnoreCase)
            ? "true"
            : "false";
    }

    private static string NormalizeDepth(string value)
    {
        var normalized = NormalizeSlug(value);
        return normalized switch
        {
            "scaffold" => "scaffold",
            "structural" => "scaffold",
            "behavioral" => "standard",
            "standard" => "standard",
            "integrated" => "standard",
            "strong" => "strong",
            _ => normalized
        };
    }

    private static string NormalizeSlug(string value)
    {
        var normalized = Normalize(value);
        normalized = normalized.Replace(' ', '_');
        return normalized;
    }

    private static string BuildArgumentTrace(IReadOnlyDictionary<string, string> normalizedArguments)
    {
        if (normalizedArguments.Count == 0)
            return "";

        return string.Join(", ", normalizedArguments
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Key}={entry.Value}"));
    }

    private static string GetArgument(IReadOnlyDictionary<string, string> normalizedArguments, string key)
    {
        return normalizedArguments.TryGetValue(key, out var value) ? value : "";
    }

    private static string EnsureSolutionPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = NormalizeRelativePath(value);
        return normalized.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"{TrimSolutionExtension(normalized)}.sln";
    }

    private static string TrimSolutionExtension(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value.Trim();
        return normalized.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            ? SanitizeProjectName(Path.GetFileNameWithoutExtension(normalized))
            : normalized;
    }

    private static string ResolveExplicitPath(string rawPhrase)
    {
        var relativePath = RelativePathPattern.Match(rawPhrase ?? "");
        if (relativePath.Success)
            return NormalizeRelativePath(relativePath.Groups["path"].Value);

        return "";
    }

    private static string ResolveExplicitBuildFilePath(string rawPhrase)
    {
        var fileMatch = FileNamePattern.Match(rawPhrase ?? "");
        if (!fileMatch.Success)
            return "";

        return NormalizeRelativePath($"{fileMatch.Groups["name"].Value.Replace('\\', '/')}.{fileMatch.Groups["ext"].Value}");
    }

    private static bool LooksLikeCreateProject(string normalizedPhrase)
    {
        return MatchesAny(
            normalizedPhrase,
            "create project",
            "create dotnet project",
            "create wpf project",
            "create console project",
            "create console app",
            "create worker project",
            "create worker service",
            "create web api",
            "create webapi",
            "create classlib project",
            "create xunit project",
            "create class library",
            "create app project",
            "create desktop app project",
            "scaffold wpf app",
            "create window app",
            "create client project",
            "make app project",
            "create core project",
            "create contracts library",
            "create storage project",
            "create repository project",
            "create test project",
            "scaffold tests",
            "add test project");
    }

    private static string BuildProjectTargetPath(string projectName, string templateHint)
    {
        var normalizedProjectName = SanitizeProjectName(projectName);
        if (string.IsNullOrWhiteSpace(normalizedProjectName))
            return "";

        var root = DotnetScaffoldSurfaceService.ResolveDefaultProjectRoot(templateHint);
        return NormalizeRelativePath(Path.Combine(root, normalizedProjectName, $"{normalizedProjectName}.csproj"));
    }

    private static bool MatchesAny(string normalizedPhrase, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var tokens = Normalize(pattern).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ContainsOrderedTokens(normalizedPhrase, tokens))
                return true;
        }

        return false;
    }

    private static bool ContainsOrderedTokens(string normalizedPhrase, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(normalizedPhrase) || tokens.Count == 0)
            return false;

        var searchStart = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var match = Regex.Match(
                normalizedPhrase[searchStart..],
                $@"\b{Regex.Escape(token)}\b",
                RegexOptions.CultureInvariant);
            if (!match.Success)
                return false;

            searchStart += match.Index + match.Length;
        }

        return true;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value.ToLowerInvariant();
        normalized = normalized.Replace('\\', '/');
        normalized = normalized.Replace("c#", "csharp");
        normalized = Regex.Replace(normalized, @"[""']", " ");
        normalized = Regex.Replace(normalized, @"[,:;!?\(\)\[\]\{\}]", " ");
        normalized = Regex.Replace(normalized, @"\b(?:the|new|a|an|please|now)\b", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.Trim();
    }

    private static string NormalizeCandidateSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value.Replace('\\', '/');
        normalized = normalized.Replace("C#", "CSharp", StringComparison.Ordinal);
        normalized = Regex.Replace(normalized, @"[""']", " ");
        normalized = Regex.Replace(normalized, @"[,:;!?\(\)\[\]\{\}]", " ");
        normalized = Regex.Replace(normalized, @"\b(?:the|new|a|an|please|now)\b", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.Trim();
    }

    private static string SanitizeProjectName(string value)
    {
        var cleaned = (value ?? "")
            .Trim()
            .Trim('"', '\'')
            .Replace('\\', '.')
            .Replace('/', '.')
            .Replace(' ', '.');
        var parts = cleaned
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeIdentifier)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
        return parts.Count == 0 ? "" : string.Join(".", parts);
    }

    private static string SanitizeIdentifier(string value)
    {
        var parts = Regex.Matches(value ?? "", @"[A-Za-z0-9]+")
            .Select(match => match.Value)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
        if (parts.Count == 0)
            return "";

        return string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string NormalizeRelativePath(string? value)
    {
        return (value ?? "").Replace('\\', '/').Trim().Trim('/');
    }

    private static string DisplayValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }
}
