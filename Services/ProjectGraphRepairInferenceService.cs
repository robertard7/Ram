using System.IO;
using System.Text.RegularExpressions;

namespace RAM.Services;

public sealed class ProjectGraphRepairInferenceService
{
    private static readonly Regex ProjectReferencePattern = new(
        @"<ProjectReference\b[^>]*\bInclude\s*=\s*[""'](?<include>[^""']+)[""'][^>]*/?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public ProjectGraphRepairInferenceResult InferCircularDependencyTarget(string workspaceRoot, string preferredTargetPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return new ProjectGraphRepairInferenceResult();

        var projectFiles = ResolveProjectFiles(workspaceRoot, preferredTargetPath);
        if (projectFiles.Count == 0)
            return new ProjectGraphRepairInferenceResult();

        var selfReferenceIssues = new List<ProjectGraphRepairIssue>();
        var duplicateReferenceIssues = new List<ProjectGraphRepairIssue>();
        foreach (var projectFile in projectFiles)
        {
            var issue = InspectCircularDependencyTarget(workspaceRoot, projectFile);
            if (issue is null)
                continue;

            if (string.Equals(issue.IssueKind, "self_project_reference", StringComparison.OrdinalIgnoreCase))
                selfReferenceIssues.Add(issue);
            else if (string.Equals(issue.IssueKind, "duplicate_project_reference", StringComparison.OrdinalIgnoreCase))
                duplicateReferenceIssues.Add(issue);
        }

        var prioritized = selfReferenceIssues.Count > 0 ? selfReferenceIssues : duplicateReferenceIssues;
        if (prioritized.Count == 1)
        {
            var issue = prioritized[0];
            return new ProjectGraphRepairInferenceResult
            {
                Success = true,
                Issue = issue,
                Summary = $"Inferred circular dependency repair target `{issue.RelativeProjectPath}` via {issue.IssueKind}."
            };
        }

        if (prioritized.Count > 1)
        {
            return new ProjectGraphRepairInferenceResult
            {
                Ambiguous = true,
                Summary = "Multiple deterministic circular-dependency repair targets were found in the workspace.",
                Candidates = prioritized
                    .OrderBy(issue => issue.RelativeProjectPath, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        return new ProjectGraphRepairInferenceResult();
    }

    public ProjectGraphRepairIssue? InspectCircularDependencyTarget(string workspaceRoot, string relativeProjectPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(relativeProjectPath))
            return null;

        try
        {
            var fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
            var fullProjectPath = Path.GetFullPath(Path.Combine(
                fullWorkspaceRoot,
                relativeProjectPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(fullProjectPath))
                return null;

            var projectDirectory = Path.GetDirectoryName(fullProjectPath) ?? fullWorkspaceRoot;
            var lines = File.ReadAllLines(fullProjectPath);
            var seenReferences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var match = ProjectReferencePattern.Match(line);
                if (!match.Success)
                    continue;

                var includeValue = NormalizeRelativePath(match.Groups["include"].Value);
                if (string.IsNullOrWhiteSpace(includeValue))
                    continue;

                var resolvedReference = Path.GetFullPath(Path.Combine(
                    projectDirectory,
                    includeValue.Replace('/', Path.DirectorySeparatorChar)));
                if (string.Equals(resolvedReference, fullProjectPath, StringComparison.OrdinalIgnoreCase))
                {
                    return new ProjectGraphRepairIssue
                    {
                        IssueKind = "self_project_reference",
                        RelativeProjectPath = NormalizeRelativePath(Path.GetRelativePath(fullWorkspaceRoot, fullProjectPath)),
                        RelativeReferencePath = includeValue,
                        LineNumber = index + 1,
                        Confidence = "high",
                        Summary = $"Detected a self project reference in `{Path.GetFileName(fullProjectPath)}`."
                    };
                }

                var normalizedReference = NormalizeRelativePath(Path.GetRelativePath(projectDirectory, resolvedReference));
                if (seenReferences.TryGetValue(normalizedReference, out var previousLine))
                {
                    return new ProjectGraphRepairIssue
                    {
                        IssueKind = "duplicate_project_reference",
                        RelativeProjectPath = NormalizeRelativePath(Path.GetRelativePath(fullWorkspaceRoot, fullProjectPath)),
                        RelativeReferencePath = normalizedReference,
                        LineNumber = index + 1,
                        PreviousLineNumber = previousLine,
                        Confidence = "medium",
                        Summary = $"Detected a duplicate project reference to `{normalizedReference}`."
                    };
                }

                seenReferences[normalizedReference] = index + 1;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static List<string> ResolveProjectFiles(string workspaceRoot, string preferredTargetPath)
    {
        var normalizedPreferredTarget = NormalizeRelativePath(preferredTargetPath);
        var projectFiles = new List<string>();

        if (normalizedPreferredTarget.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            projectFiles.Add(normalizedPreferredTarget);
        }
        else if (Directory.Exists(workspaceRoot))
        {
            projectFiles.AddRange(Directory
                .EnumerateFiles(workspaceRoot, "*.csproj", SearchOption.AllDirectories)
                .Select(path => NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, path)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        }

        return projectFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeRelativePath(string value)
    {
        return (value ?? "").Replace('\\', '/').Trim();
    }
}

public sealed class ProjectGraphRepairInferenceResult
{
    public bool Success { get; set; }
    public bool Ambiguous { get; set; }
    public string Summary { get; set; } = "";
    public ProjectGraphRepairIssue? Issue { get; set; }
    public List<ProjectGraphRepairIssue> Candidates { get; set; } = [];
}

public sealed class ProjectGraphRepairIssue
{
    public string IssueKind { get; set; } = "";
    public string RelativeProjectPath { get; set; } = "";
    public string RelativeReferencePath { get; set; } = "";
    public int LineNumber { get; set; }
    public int PreviousLineNumber { get; set; }
    public string Confidence { get; set; } = "";
    public string Summary { get; set; } = "";
}
