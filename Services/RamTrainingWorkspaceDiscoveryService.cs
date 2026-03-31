using System.IO;

namespace RAM.Services;

public sealed class RamTrainingWorkspaceDiscoveryService
{
    public List<string> DiscoverWorkspaceRoots(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return [];

        var normalizedRoot = NormalizePath(workspaceRoot);
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            normalizedRoot
        };

        if (!Directory.Exists(normalizedRoot))
            return results.OrderBy(current => current, StringComparer.OrdinalIgnoreCase).ToList();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var ramDbPath in Directory.EnumerateFiles(normalizedRoot, "ram.db", options))
        {
            var ramDirectory = Path.GetDirectoryName(ramDbPath);
            if (!string.Equals(Path.GetFileName(ramDirectory), ".ram", StringComparison.OrdinalIgnoreCase))
                continue;

            var candidateWorkspaceRoot = Path.GetDirectoryName(ramDirectory);
            if (!IsEligibleWorkspaceRoot(candidateWorkspaceRoot, normalizedRoot))
                continue;

            results.Add(NormalizePath(candidateWorkspaceRoot!));
        }

        return results
            .OrderBy(current => current, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsEligibleWorkspaceRoot(string? candidateWorkspaceRoot, string normalizedRoot)
    {
        if (string.IsNullOrWhiteSpace(candidateWorkspaceRoot))
            return false;

        var normalized = NormalizePath(candidateWorkspaceRoot);
        if (!normalized.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        if (normalized.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\training-data-pipeline-validator-workspace", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}
