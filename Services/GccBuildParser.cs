using System.IO;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class GccBuildParser
{
    private static readonly Regex FileLocationPattern = new(
        @"^(?<file>.+?):(?<line>\d+)(?::(?<column>\d+))?:\s+(?<severity>fatal error|error|warning):\s+(?<message>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex LinkerPattern = new(
        @"^(?<file>.+?):(?<line>\d+):\s+(?<message>undefined reference to .+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    public List<DotnetBuildErrorRecord> Parse(string workspaceRoot, string combinedOutput)
    {
        var results = new List<DotnetBuildErrorRecord>();
        var order = 0;

        foreach (Match match in FileLocationPattern.Matches(combinedOutput ?? ""))
        {
            var rawPath = NormalizePath(match.Groups["file"].Value.Trim());
            var normalizedPath = NormalizeForWorkspace(workspaceRoot, rawPath, out var insideWorkspace);
            var severity = NormalizeSeverity(match.Groups["severity"].Value);
            results.Add(new DotnetBuildErrorRecord
            {
                FilePath = normalizedPath,
                RawPath = rawPath,
                LineNumber = ParseInt(match, "line"),
                ColumnNumber = ParseInt(match, "column"),
                Severity = severity,
                Code = "",
                Message = BoundText(match.Groups["message"].Value.Trim(), 240),
                InsideWorkspace = insideWorkspace,
                Order = order++
            });
        }

        foreach (Match match in LinkerPattern.Matches(combinedOutput ?? ""))
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
                Code = "linker",
                Message = BoundText(match.Groups["message"].Value.Trim(), 240),
                InsideWorkspace = insideWorkspace,
                Order = order++
            });
        }

        return results;
    }

    private static int ParseInt(Match match, string groupName)
    {
        return int.TryParse(match.Groups[groupName].Value, out var value) ? value : 0;
    }

    private static string NormalizeSeverity(string value)
    {
        return value.Contains("warning", StringComparison.OrdinalIgnoreCase) ? "warning" : "error";
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
