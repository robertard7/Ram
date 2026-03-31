using System.IO;

namespace RAM.Services;

public sealed class WorkspaceStructuralExclusionPolicyService
{
    public const string PolicyVersion = "workspace_exclusion_policy.v1";

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".ram",
        ".codex-temp",
        "bin",
        "obj",
        "node_modules",
        "testresults"
    };

    public IReadOnlyList<string> GetExcludedDirectoryNames()
    {
        return ExcludedDirectoryNames
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool ShouldExcludeDirectory(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        return !string.IsNullOrWhiteSpace(name)
            && ExcludedDirectoryNames.Contains(name);
    }
}
