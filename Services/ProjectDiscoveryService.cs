using System.IO;

namespace RAM.Services;

public sealed class ProjectDiscoveryService
{
    public ProjectDiscoveryResult DiscoverBuildTarget(string workspaceRoot, string requestedProject)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace not found: {workspaceRoot}");

        if (!string.IsNullOrWhiteSpace(requestedProject))
        {
            var fullPath = Path.IsPathRooted(requestedProject)
                ? Path.GetFullPath(requestedProject)
                : Path.GetFullPath(Path.Combine(workspaceRoot, requestedProject));

            EnsureInsideWorkspace(workspaceRoot, fullPath);

            if (!File.Exists(fullPath))
            {
                return ProjectDiscoveryResult.Failure(
                    $"dotnet_build failed: requested target was not found: {NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, fullPath))}.");
            }

            return ProjectDiscoveryResult.Success(
                NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, fullPath)),
                $"Using requested build target: {NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, fullPath))}");
        }

        var solutionCandidates = FindCandidates(workspaceRoot, ".sln");
        if (solutionCandidates.Count == 1)
            return ProjectDiscoveryResult.Success(solutionCandidates[0], $"Discovered build target: {solutionCandidates[0]}");

        if (solutionCandidates.Count > 1)
            return ProjectDiscoveryResult.Failure(BuildMultiCandidateMessage(solutionCandidates));

        var projectCandidates = FindCandidates(workspaceRoot, ".csproj");
        if (projectCandidates.Count == 1)
            return ProjectDiscoveryResult.Success(projectCandidates[0], $"Discovered build target: {projectCandidates[0]}");

        if (projectCandidates.Count > 1)
            return ProjectDiscoveryResult.Failure(BuildMultiCandidateMessage(projectCandidates));

        return ProjectDiscoveryResult.Failure(
            "dotnet_build failed: no .sln or .csproj files were found in the current workspace.");
    }

    private static List<string> FindCandidates(string workspaceRoot, string extension)
    {
        var matches = new List<string>();
        var pending = new Stack<string>();
        pending.Push(workspaceRoot);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (IsIgnoredDirectory(directory))
                    continue;

                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current))
            {
                if (!string.Equals(Path.GetExtension(file), extension, StringComparison.OrdinalIgnoreCase))
                    continue;

                matches.Add(NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, file)));
            }
        }

        matches.Sort(StringComparer.OrdinalIgnoreCase);
        return matches;
    }

    private static string BuildMultiCandidateMessage(IReadOnlyList<string> candidates)
    {
        const int maxCandidates = 5;
        var bounded = candidates.Take(maxCandidates).ToList();
        var lines = new List<string>
        {
            "dotnet_build failed: multiple build targets were found.",
            "Candidates:"
        };

        foreach (var candidate in bounded)
            lines.Add($"- {candidate}");

        if (candidates.Count > maxCandidates)
            lines.Add($"- [+{candidates.Count - maxCandidates} more]");

        lines.Add("Say which one to build using project=<relative path>.");
        return string.Join(Environment.NewLine, lines);
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

    private static void EnsureInsideWorkspace(string workspaceRoot, string path)
    {
        var fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        var fullPath = Path.GetFullPath(path);
        var workspacePrefix = fullWorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var isWorkspaceRoot = string.Equals(fullPath, fullWorkspaceRoot, StringComparison.OrdinalIgnoreCase);
        var isInsideWorkspace = fullPath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase);

        if (!isWorkspaceRoot && !isInsideWorkspace)
            throw new InvalidOperationException("Build target must stay inside the active workspace.");
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/');
    }
}

public sealed class ProjectDiscoveryResult
{
    public bool IsSuccess { get; private set; }
    public string TargetPath { get; private set; } = "";
    public string Message { get; private set; } = "";

    public static ProjectDiscoveryResult Success(string targetPath, string message)
    {
        return new ProjectDiscoveryResult
        {
            IsSuccess = true,
            TargetPath = targetPath,
            Message = message
        };
    }

    public static ProjectDiscoveryResult Failure(string message)
    {
        return new ProjectDiscoveryResult
        {
            IsSuccess = false,
            Message = message
        };
    }
}
