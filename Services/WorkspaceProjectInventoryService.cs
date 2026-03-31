using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RAM.Models;

namespace RAM.Services;

public sealed class WorkspaceProjectInventoryService
{
    public const string InventoryVersion = "workspace_project_inventory.v1";

    private sealed record ProjectDirectoryCandidate(string ProjectPath, string ProjectDirectory);

    private static readonly Regex SolutionProjectPattern = new(
        "^Project\\(\"\\{[^\\}]+\\}\"\\)\\s*=\\s*\"(?<name>[^\"]+)\",\\s*\"(?<path>[^\"]+)\",\\s*\"\\{[^\\}]+\\}\"",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public WorkspaceProjectGraphRecord Build(string workspaceRoot, WorkspaceSnapshotRecord snapshot)
    {
        var fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        var solutionMembership = BuildSolutionMembership(fullWorkspaceRoot, snapshot);
        ApplyOwningProjectPaths(snapshot);

        var projects = snapshot.ProjectPaths
            .Select(projectPath => BuildProjectRecord(fullWorkspaceRoot, snapshot, projectPath, solutionMembership))
            .OrderBy(project => project.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var solutions = solutionMembership
            .Select(entry => new WorkspaceSolutionRecord
            {
                SolutionKey = entry.Key,
                RelativePath = entry.Key,
                SolutionName = Path.GetFileNameWithoutExtension(entry.Key),
                MemberProjectPaths = entry.Value
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Evidence =
                [
                    "source=sln_membership",
                    $"member_count={entry.Value.Count}"
                ]
            })
            .OrderBy(solution => solution.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var references = solutions
            .SelectMany(solution => solution.MemberProjectPaths.Select(projectPath => new WorkspaceReferenceRecord
            {
                ReferenceKey = $"solution_member::{solution.RelativePath}::{projectPath}",
                ReferenceKind = "solution_membership",
                SourcePath = solution.RelativePath,
                TargetPath = projectPath,
                Include = projectPath,
                Evidence =
                [
                    "source=sln_membership",
                    $"solution={solution.RelativePath}"
                ]
            }))
            .Concat(projects
            .SelectMany(project => project.PackageReferences.Concat(project.ProjectReferences))
            )
            .OrderBy(reference => reference.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reference => reference.TargetPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reference => reference.ReferenceKind, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WorkspaceProjectGraphRecord
        {
            GraphId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = fullWorkspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            InventoryVersion = InventoryVersion,
            SnapshotId = snapshot.SnapshotId,
            SolutionCount = solutions.Count,
            ProjectCount = projects.Count,
            ReferenceCount = references.Count,
            Solutions = solutions,
            Projects = projects,
            References = references
        };
    }

    private static void ApplyOwningProjectPaths(WorkspaceSnapshotRecord snapshot)
    {
        var projectDirectories = snapshot.ProjectPaths
            .Select(path => new ProjectDirectoryCandidate(path, NormalizeRelativePath(Path.GetDirectoryName(path))))
            .OrderByDescending(entry => entry.ProjectDirectory.Length)
            .ThenBy(entry => entry.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in snapshot.Files)
        {
            file.OwningProjectPath = ResolveOwningProject(file.RelativePath, projectDirectories);
            if (!string.IsNullOrWhiteSpace(file.OwningProjectPath))
                file.Evidence.Add($"owning_project={file.OwningProjectPath}");
        }
    }

    private static Dictionary<string, List<string>> BuildSolutionMembership(string workspaceRoot, WorkspaceSnapshotRecord snapshot)
    {
        var memberships = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var solutionPath in snapshot.SolutionPaths)
        {
            var fullSolutionPath = Path.Combine(workspaceRoot, solutionPath.Replace('/', Path.DirectorySeparatorChar));
            var members = ParseSolutionMemberProjects(workspaceRoot, fullSolutionPath);
            memberships[solutionPath] = members
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return memberships;
    }

    private WorkspaceProjectRecord BuildProjectRecord(
        string workspaceRoot,
        WorkspaceSnapshotRecord snapshot,
        string projectPath,
        IReadOnlyDictionary<string, List<string>> solutionMembership)
    {
        var fullProjectPath = Path.Combine(workspaceRoot, projectPath.Replace('/', Path.DirectorySeparatorChar));
        var projectDirectory = NormalizeRelativePath(Path.GetDirectoryName(projectPath));
        var ownedFiles = snapshot.Files
            .Where(file => string.Equals(file.OwningProjectPath, projectPath, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(file.RelativePath, projectPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var references = ParseProjectReferences(workspaceRoot, fullProjectPath, projectPath);
        var solutionPaths = solutionMembership
            .Where(entry => entry.Value.Contains(projectPath, StringComparer.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var isTestProject = DetermineIsTestProject(projectPath, references);
        var projectReferences = references
            .Where(reference => string.Equals(reference.ReferenceKind, "project", StringComparison.OrdinalIgnoreCase))
            .OrderBy(reference => reference.TargetPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var packageReferences = references
            .Where(reference => string.Equals(reference.ReferenceKind, "package", StringComparison.OrdinalIgnoreCase))
            .OrderBy(reference => reference.Include, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var (sdk, targetFrameworks, outputType, parseEvidence) = ParseProjectMetadata(fullProjectPath);
        var evidence = new List<string>(parseEvidence)
        {
            $"owned_files={ownedFiles.Count}",
            $"solution_memberships={solutionPaths.Count}"
        };

        return new WorkspaceProjectRecord
        {
            ProjectKey = projectPath,
            RelativePath = projectPath,
            ProjectName = Path.GetFileNameWithoutExtension(projectPath),
            ProjectDirectory = string.IsNullOrWhiteSpace(projectDirectory) ? "." : projectDirectory,
            Sdk = sdk,
            TargetFrameworks = targetFrameworks,
            OutputType = outputType,
            IsTestProject = isTestProject,
            SolutionPaths = solutionPaths,
            OwnedFilePaths = ownedFiles.Select(file => file.RelativePath).ToList(),
            SourceFilePaths = ownedFiles
                .Where(file => string.Equals(file.FileKind, "source", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(file.FileKind, "test", StringComparison.OrdinalIgnoreCase))
                .Select(file => file.RelativePath)
                .ToList(),
            XamlFilePaths = ownedFiles
                .Where(file => string.Equals(file.FileKind, "xaml", StringComparison.OrdinalIgnoreCase))
                .Select(file => file.RelativePath)
                .ToList(),
            ConfigFilePaths = ownedFiles
                .Where(file => string.Equals(file.FileKind, "config", StringComparison.OrdinalIgnoreCase))
                .Select(file => file.RelativePath)
                .ToList(),
            TestedProjectPaths = isTestProject
                ? projectReferences.Select(reference => reference.TargetPath).ToList()
                : [],
            PackageReferences = packageReferences,
            ProjectReferences = projectReferences,
            Evidence = evidence
        };
    }

    private static (string Sdk, List<string> TargetFrameworks, string OutputType, List<string> Evidence) ParseProjectMetadata(string fullProjectPath)
    {
        var evidence = new List<string>();
        try
        {
            var document = XDocument.Load(fullProjectPath);
            var root = document.Root;
            if (root is null)
                return ("", [], "", ["parse_status=missing_root"]);

            var sdk = root.Attribute("Sdk")?.Value?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(sdk))
            {
                sdk = document.Descendants()
                    .FirstOrDefault(element => string.Equals(element.Name.LocalName, "Sdk", StringComparison.OrdinalIgnoreCase))
                    ?.Attribute("Name")?.Value?.Trim() ?? "";
            }

            var frameworks = document.Descendants()
                .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .SelectMany(element => (element.Value ?? "")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var outputType = document.Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "OutputType", StringComparison.OrdinalIgnoreCase))
                ?.Value?.Trim() ?? "";

            evidence.Add("parse_status=parsed");
            evidence.Add($"sdk={DisplayValue(sdk)}");
            evidence.Add($"frameworks={DisplayValue(string.Join(",", frameworks))}");
            evidence.Add($"output_type={DisplayValue(outputType)}");
            return (sdk, frameworks, outputType, evidence);
        }
        catch (Exception ex)
        {
            return ("", [], "", [$"parse_status=failed reason={ex.GetType().Name}"]);
        }
    }

    private static List<WorkspaceReferenceRecord> ParseProjectReferences(string workspaceRoot, string fullProjectPath, string projectPath)
    {
        try
        {
            var document = XDocument.Load(fullProjectPath);
            var references = new List<WorkspaceReferenceRecord>();
            foreach (var projectReference in document.Descendants().Where(element => string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.OrdinalIgnoreCase)))
            {
                var include = projectReference.Attribute("Include")?.Value?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(include))
                    continue;

                var resolvedPath = NormalizeRelativePath(Path.GetRelativePath(
                    workspaceRoot,
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullProjectPath) ?? workspaceRoot, include.Replace('/', Path.DirectorySeparatorChar)))));
                references.Add(new WorkspaceReferenceRecord
                {
                    ReferenceKey = $"project::{projectPath}::{resolvedPath}",
                    ReferenceKind = "project",
                    SourcePath = projectPath,
                    TargetPath = resolvedPath,
                    Include = include.Replace('\\', '/'),
                    Evidence =
                    [
                        "source=csproj",
                        $"include={include.Replace('\\', '/')}"
                    ]
                });
            }

            foreach (var packageReference in document.Descendants().Where(element => string.Equals(element.Name.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase)))
            {
                var include = packageReference.Attribute("Include")?.Value?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(include))
                    continue;

                var version = packageReference.Attribute("Version")?.Value?.Trim()
                    ?? packageReference.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, "Version", StringComparison.OrdinalIgnoreCase))?.Value?.Trim()
                    ?? "";
                references.Add(new WorkspaceReferenceRecord
                {
                    ReferenceKey = $"package::{projectPath}::{include}",
                    ReferenceKind = "package",
                    SourcePath = projectPath,
                    TargetPath = include,
                    Include = include,
                    Version = version,
                    Evidence =
                    [
                        "source=csproj",
                        $"package={include}",
                        $"version={DisplayValue(version)}"
                    ]
                });
            }

            return references;
        }
        catch
        {
            return [];
        }
    }

    private static List<string> ParseSolutionMemberProjects(string workspaceRoot, string fullSolutionPath)
    {
        var members = new List<string>();
        if (!File.Exists(fullSolutionPath))
            return members;

        var solutionDirectory = Path.GetDirectoryName(fullSolutionPath) ?? workspaceRoot;
        foreach (var line in File.ReadLines(fullSolutionPath))
        {
            var match = SolutionProjectPattern.Match(line);
            if (!match.Success)
                continue;

            var projectValue = match.Groups["path"].Value.Trim().Replace('\\', '/');
            if (!projectValue.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                continue;

            var resolvedPath = NormalizeRelativePath(Path.GetRelativePath(
                workspaceRoot,
                Path.GetFullPath(Path.Combine(solutionDirectory, projectValue.Replace('/', Path.DirectorySeparatorChar)))));
            members.Add(resolvedPath);
        }

        return members;
    }

    private static string ResolveOwningProject(
        string relativePath,
        IReadOnlyList<ProjectDirectoryCandidate> projectDirectories)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return "";

        var normalizedPath = NormalizeRelativePath(relativePath);
        foreach (var candidate in projectDirectories)
        {
            var projectPath = candidate.ProjectPath;
            var projectDirectory = candidate.ProjectDirectory;
            if (string.Equals(normalizedPath, projectPath, StringComparison.OrdinalIgnoreCase))
                return projectPath;

            if (string.IsNullOrWhiteSpace(projectDirectory))
                continue;

            var prefix = projectDirectory.TrimEnd('/') + "/";
            if (normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return projectPath;
        }

        return "";
    }

    private static bool DetermineIsTestProject(string projectPath, IReadOnlyList<WorkspaceReferenceRecord> references)
    {
        return projectPath.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
            || projectPath.Contains(".Tests.", StringComparison.OrdinalIgnoreCase)
            || references.Any(reference =>
                string.Equals(reference.ReferenceKind, "package", StringComparison.OrdinalIgnoreCase)
                && (reference.TargetPath.Contains("xunit", StringComparison.OrdinalIgnoreCase)
                    || reference.TargetPath.Contains("mstest", StringComparison.OrdinalIgnoreCase)
                    || reference.TargetPath.Contains("nunit", StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizeRelativePath(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Replace('\\', '/').Trim().Trim('/');
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
