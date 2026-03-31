using System.IO;
using RAM.Models;

namespace RAM.Services;

public sealed class TestFailureSourceInferenceService
{
    private readonly WorkspaceBuildIndexService _workspaceBuildIndexService;

    public TestFailureSourceInferenceService(WorkspaceBuildIndexService workspaceBuildIndexService)
    {
        _workspaceBuildIndexService = workspaceBuildIndexService;
    }

    public void Enrich(string workspaceRoot, IList<DotnetTestFailureRecord> failures, string activeTargetRelativePath = "")
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || failures is null || failures.Count == 0)
            return;

        var preferredDirectories = BuildPreferredDirectories(workspaceRoot, activeTargetRelativePath);
        var candidates = EnumerateCandidateFiles(workspaceRoot);

        foreach (var failure in failures)
        {
            if (TryResolveStackSource(workspaceRoot, failure))
                continue;

            var className = ExtractClassName(failure.TestName);
            if (string.IsNullOrWhiteSpace(className))
            {
                failure.SourceConfidence = "none";
                continue;
            }

            var exactMatches = FindCandidates(candidates, preferredDirectories, className, exactOnly: true);
            if (TryApplyCandidateResolution(failure, exactMatches, preferredDirectories))
                continue;

            var looseMatches = FindCandidates(candidates, preferredDirectories, className, exactOnly: false);
            if (TryApplyCandidateResolution(failure, looseMatches, preferredDirectories, allowLowConfidence: true))
                continue;

            failure.SourceConfidence = "none";
        }
    }

    private static bool TryResolveStackSource(string workspaceRoot, DotnetTestFailureRecord failure)
    {
        if (string.IsNullOrWhiteSpace(failure.SourceFilePath))
            return false;

        var fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        var fullPath = Path.IsPathRooted(failure.SourceFilePath)
            ? Path.GetFullPath(failure.SourceFilePath)
            : Path.GetFullPath(Path.Combine(workspaceRoot, failure.SourceFilePath));

        var workspacePrefix = fullWorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!string.Equals(fullPath, fullWorkspaceRoot, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            failure.SourceConfidence = "none";
            return false;
        }

        failure.ResolvedSourcePath = Path.GetRelativePath(fullWorkspaceRoot, fullPath).Replace('\\', '/');
        failure.SourceConfidence = "high";
        failure.CandidatePaths = [failure.ResolvedSourcePath];
        return true;
    }

    private List<string> BuildPreferredDirectories(string workspaceRoot, string activeTargetRelativePath)
    {
        var directories = new List<string>();
        if (!string.IsNullOrWhiteSpace(activeTargetRelativePath))
        {
            var activeDirectory = Path.GetDirectoryName(activeTargetRelativePath)?.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(activeDirectory))
                directories.Add(activeDirectory);
        }

        foreach (var item in _workspaceBuildIndexService.ListItems(workspaceRoot)
                     .Where(item => item.ItemType == "project" && item.LikelyTestProject)
                     .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!directories.Contains(item.ParentDirectory, StringComparer.OrdinalIgnoreCase))
                directories.Add(item.ParentDirectory);
        }

        return directories;
    }

    private static List<string> EnumerateCandidateFiles(string workspaceRoot)
    {
        var results = new List<string>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(workspaceRoot));

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (IsIgnoredDirectory(directory))
                    continue;

                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current, "*.cs"))
            {
                results.Add(Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/'));
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    private static List<string> FindCandidates(
        IEnumerable<string> candidates,
        IEnumerable<string> preferredDirectories,
        string className,
        bool exactOnly)
    {
        var expectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"{className}.cs",
            $"{className}Tests.cs",
            $"{className}Test.cs"
        };

        return candidates
            .Where(candidate =>
            {
                var fileName = Path.GetFileName(candidate);
                if (expectedNames.Contains(fileName))
                    return true;

                if (exactOnly)
                    return false;

                return fileName.Contains(className, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(candidate => IsPreferredDirectory(candidate, preferredDirectories) ? 0 : 1)
            .ThenBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryApplyCandidateResolution(
        DotnetTestFailureRecord failure,
        IReadOnlyList<string> candidates,
        IEnumerable<string> preferredDirectories,
        bool allowLowConfidence = false)
    {
        if (candidates.Count == 0)
            return false;

        failure.CandidatePaths = candidates.Take(5).ToList();
        if (candidates.Count == 1)
        {
            failure.ResolvedSourcePath = candidates[0];
            failure.SourceConfidence = IsPreferredDirectory(candidates[0], preferredDirectories)
                ? "high"
                : allowLowConfidence ? "low" : "medium";
            return true;
        }

        failure.SourceConfidence = "none";
        failure.AmbiguityDetails = $"Multiple candidate files matched {failure.TestName}.";
        return true;
    }

    private static string ExtractClassName(string testName)
    {
        if (string.IsNullOrWhiteSpace(testName))
            return "";

        var parts = testName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? parts[^2] : "";
    }

    private static bool IsPreferredDirectory(string candidatePath, IEnumerable<string> preferredDirectories)
    {
        foreach (var preferredDirectory in preferredDirectories)
        {
            if (string.IsNullOrWhiteSpace(preferredDirectory))
                continue;

            var prefix = preferredDirectory.TrimEnd('/') + "/";
            if (candidatePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsIgnoredDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, ".ram", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase);
    }
}
