using System.IO;
using RAM.Models;

namespace RAM.Services;

public sealed class BuildFailureLocationService
{
    public IReadOnlyList<DotnetBuildErrorRecord> Prioritize(
        IEnumerable<DotnetBuildErrorRecord> locations,
        string targetPath)
    {
        var prioritized = (locations ?? [])
            .Select(location => location)
            .OrderBy(location => IsError(location) ? 0 : 1)
            .ThenBy(location => IsInsideTargetSubtree(location, targetPath) ? 0 : 1)
            .ThenBy(location => location.Order)
            .ToList();

        return prioritized;
    }

    public DotnetBuildErrorRecord? GetBestLocation(
        IEnumerable<DotnetBuildErrorRecord> locations,
        string targetPath)
    {
        return Prioritize(locations, targetPath).FirstOrDefault();
    }

    private static bool IsError(DotnetBuildErrorRecord location)
    {
        return string.Equals(location.Severity, "error", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideTargetSubtree(DotnetBuildErrorRecord location, string targetPath)
    {
        if (!location.InsideWorkspace || string.IsNullOrWhiteSpace(location.FilePath) || string.IsNullOrWhiteSpace(targetPath))
            return false;

        var normalizedFilePath = NormalizeRelativePath(location.FilePath);
        var normalizedTarget = NormalizeRelativePath(targetPath);
        var subtree = normalizedTarget;

        if (subtree.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || subtree.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            subtree = Path.GetDirectoryName(subtree)?.Replace('\\', '/') ?? "";
        }

        if (string.IsNullOrWhiteSpace(subtree) || subtree == ".")
            return true;

        var prefix = subtree.TrimEnd('/') + "/";
        return normalizedFilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path)
    {
        return (path ?? "").Replace('\\', '/');
    }
}
