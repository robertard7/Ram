using System.IO;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class DotnetBuildParser
{
    private static readonly Regex ErrorCountPattern = new(
        @"^\s*(?<count>\d+)\s+Error\(s\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex WarningCountPattern = new(
        @"^\s*(?<count>\d+)\s+Warning\(s\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex LocationPattern = new(
        @"^(?<file>.+?)\((?<line>\d+)(?:,(?<column>\d+))?\):\s+(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+):\s+(?<message>.+?)(?:\s+\[[^\]]+\])?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex MsbuildErrorPattern = new(
        @"^(?:(?<file>.*?)(?:\((?<line>\d+)(?:,(?<column>\d+))?\))?:\s+)?(?<severity>error|warning)\s+(?<code>MSB\d+):\s+(?<message>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    public DotnetBuildParseResult Parse(string stdout, string stderr)
    {
        return Parse("", stdout, stderr);
    }

    public DotnetBuildParseResult Parse(string workspaceRoot, string stdout, string stderr)
    {
        var combined = CombineOutput(stdout, stderr);
        var buildLocations = ParseLocations(workspaceRoot, combined);
        var errorCount = ParseLastCount(ErrorCountPattern, combined);
        var warningCount = ParseLastCount(WarningCountPattern, combined);

        if (errorCount == 0)
            errorCount = buildLocations.Count(location => string.Equals(location.Severity, "error", StringComparison.OrdinalIgnoreCase));

        if (warningCount == 0)
            warningCount = buildLocations.Count(location => string.Equals(location.Severity, "warning", StringComparison.OrdinalIgnoreCase));

        var success = errorCount == 0
            && (combined.Contains("Build succeeded.", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("0 Error(s)", StringComparison.OrdinalIgnoreCase));

        var normalizedFailure = ParseNormalizedFailure(buildLocations);

        return new DotnetBuildParseResult
        {
            Success = success,
            ErrorCount = errorCount,
            WarningCount = warningCount,
            BuildLocations = buildLocations,
            TopErrors = buildLocations
                .Where(location => string.Equals(location.Severity, "error", StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .ToList(),
            Summary = success
                ? $"Build succeeded with {warningCount} warning(s) and 0 error(s)."
                : $"Build failed with {errorCount} error(s) and {warningCount} warning(s).",
            NormalizedFailureFamily = normalizedFailure.Family,
            NormalizedErrorCode = normalizedFailure.ErrorCode,
            NormalizedFailureSummary = normalizedFailure.Summary,
            NormalizedSourcePath = normalizedFailure.SourcePath
        };
    }

    private static List<DotnetBuildErrorRecord> ParseLocations(string workspaceRoot, string combined)
    {
        var locations = new List<DotnetBuildErrorRecord>();
        var order = 0;

        foreach (Match match in LocationPattern.Matches(combined))
        {
            var rawPath = NormalizePath(match.Groups["file"].Value.Trim());
            var normalizedPath = NormalizeForWorkspace(workspaceRoot, rawPath, out var insideWorkspace);
            locations.Add(new DotnetBuildErrorRecord
            {
                FilePath = normalizedPath,
                RawPath = rawPath,
                LineNumber = ParseInt(match, "line"),
                ColumnNumber = ParseInt(match, "column"),
                Severity = match.Groups["severity"].Value.Trim().ToLowerInvariant(),
                Code = match.Groups["code"].Value.Trim(),
                Message = BoundText(match.Groups["message"].Value.Trim(), 240),
                InsideWorkspace = insideWorkspace,
                Order = order++
            });
        }

        foreach (Match match in MsbuildErrorPattern.Matches(combined))
        {
            var rawPath = NormalizePath(match.Groups["file"].Value.Trim());
            var normalizedPath = NormalizeForWorkspace(workspaceRoot, rawPath, out var insideWorkspace);
            var severity = match.Groups["severity"].Value.Trim().ToLowerInvariant();
            var code = match.Groups["code"].Value.Trim();
            var message = BoundText(match.Groups["message"].Value.Trim(), 240);
            if (locations.Any(location =>
                    string.Equals(location.RawPath, rawPath, StringComparison.OrdinalIgnoreCase)
                    && location.LineNumber == ParseInt(match, "line")
                    && location.ColumnNumber == ParseInt(match, "column")
                    && string.Equals(location.Severity, severity, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(location.Code, code, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(location.Message, message, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            locations.Add(new DotnetBuildErrorRecord
            {
                FilePath = normalizedPath,
                RawPath = rawPath,
                LineNumber = ParseInt(match, "line"),
                ColumnNumber = ParseInt(match, "column"),
                Severity = severity,
                Code = code,
                Message = message,
                InsideWorkspace = insideWorkspace,
                Order = order++
            });
        }

        return locations;
    }

    private static (string Family, string ErrorCode, string Summary, string SourcePath) ParseNormalizedFailure(
        IReadOnlyList<DotnetBuildErrorRecord> buildLocations)
    {
        var circularDependency = buildLocations.FirstOrDefault(location =>
            string.Equals(location.Code, "MSB4006", StringComparison.OrdinalIgnoreCase)
            && location.Message.Contains("circular dependency", StringComparison.OrdinalIgnoreCase));
        if (circularDependency is not null)
        {
            var sourcePath = FirstNonEmpty(circularDependency.FilePath, circularDependency.RawPath);
            var sourceSummary = string.IsNullOrWhiteSpace(sourcePath)
                ? "target graph"
                : NormalizePath(Path.GetFileName(sourcePath));
            return (
                "solution_graph_circular_dependency",
                "MSB4006",
                $"MSB4006 circular dependency in target graph reported by {sourceSummary}.",
                sourcePath);
        }

        return ("", "", "", "");
    }

    private static int ParseLastCount(Regex regex, string combined)
    {
        var matches = regex.Matches(combined);
        if (matches.Count == 0)
            return 0;

        var lastMatch = matches[^1];
        return int.TryParse(lastMatch.Groups["count"].Value, out var value) ? value : 0;
    }

    private static int ParseInt(Match match, string groupName)
    {
        return int.TryParse(match.Groups[groupName].Value, out var value) ? value : 0;
    }

    private static string NormalizeForWorkspace(string workspaceRoot, string rawPath, out bool insideWorkspace)
    {
        insideWorkspace = false;
        if (string.IsNullOrWhiteSpace(rawPath))
            return "";

        if (string.IsNullOrWhiteSpace(workspaceRoot))
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

    private static string CombineOutput(string stdout, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return stdout ?? "";

        if (string.IsNullOrWhiteSpace(stdout))
            return stderr ?? "";

        return stdout + Environment.NewLine + stderr;
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
