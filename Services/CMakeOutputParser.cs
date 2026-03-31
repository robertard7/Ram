using System.IO;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class CMakeOutputParser
{
    private static readonly Regex LocationPattern = new(
        @"^CMake Error at (?<file>.+?)(?::(?<line>\d+))?(?: \(.+?\))?:\s*(?<message>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex GenericErrorPattern = new(
        @"^CMake Error:\s*(?<message>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    public List<DotnetBuildErrorRecord> Parse(string workspaceRoot, string combinedOutput)
    {
        var results = new List<DotnetBuildErrorRecord>();
        var order = 0;

        foreach (Match match in LocationPattern.Matches(combinedOutput ?? ""))
        {
            var rawPath = NormalizePath(match.Groups["file"].Value.Trim());
            var normalizedPath = NormalizeForWorkspace(workspaceRoot, rawPath, out var insideWorkspace);
            results.Add(new DotnetBuildErrorRecord
            {
                FilePath = normalizedPath,
                RawPath = rawPath,
                LineNumber = ParseInt(match, "line"),
                ColumnNumber = 0,
                Severity = "error",
                Code = "cmake",
                Message = BoundText(match.Groups["message"].Value.Trim(), 240),
                InsideWorkspace = insideWorkspace,
                Order = order++
            });
        }

        foreach (Match match in GenericErrorPattern.Matches(combinedOutput ?? ""))
        {
            results.Add(new DotnetBuildErrorRecord
            {
                FilePath = "",
                RawPath = "",
                LineNumber = 0,
                ColumnNumber = 0,
                Severity = "error",
                Code = "cmake",
                Message = BoundText(match.Groups["message"].Value.Trim(), 240),
                InsideWorkspace = false,
                Order = order++
            });
        }

        return results;
    }

    private static int ParseInt(Match match, string groupName)
    {
        return int.TryParse(match.Groups[groupName].Value, out var value) ? value : 0;
    }

    private static string NormalizeForWorkspace(string workspaceRoot, string rawPath, out bool insideWorkspace)
    {
        insideWorkspace = false;
        if (string.IsNullOrWhiteSpace(rawPath) || string.IsNullOrWhiteSpace(workspaceRoot))
            return rawPath;

        try
        {
            var fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
            var fullPath = Path.IsPathRooted(rawPath)
                ? Path.GetFullPath(rawPath)
                : Path.GetFullPath(Path.Combine(workspaceRoot, rawPath));
            var workspacePrefix = fullWorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            insideWorkspace = string.Equals(fullPath, fullWorkspaceRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase);

            return insideWorkspace
                ? NormalizePath(Path.GetRelativePath(fullWorkspaceRoot, fullPath))
                : rawPath;
        }
        catch
        {
            return rawPath;
        }
    }

    private static string NormalizePath(string path)
    {
        return (path ?? "").Replace('\\', '/');
    }

    private static string BoundText(string text, int maxChars)
    {
        return string.IsNullOrWhiteSpace(text) || text.Length <= maxChars
            ? text
            : text[..maxChars].TrimEnd() + "...";
    }
}
