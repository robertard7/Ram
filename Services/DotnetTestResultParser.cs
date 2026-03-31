using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class DotnetTestResultParser
{
    private static readonly Regex CompactSummaryPattern = new(
        @"(?:Passed!|Failed!)\s*-?\s*Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LegacySummaryPattern = new(
        @"Total tests:\s*(?<total>\d+)\s*\.?\s*Passed:\s*(?<passed>\d+)\s*\.?\s*Failed:\s*(?<failed>\d+)\s*\.?\s*Skipped:\s*(?<skipped>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex FailedTestPattern = new(
        @"^\s*Failed\s+(?<name>.+?)\s+\[[^\]]+\]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex XunitFailedTestPattern = new(
        @"^\s*\[xUnit\.net[^\]]*\]\s+(?<name>.+?)\s+\[FAIL\]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex StackFilePattern = new(
        @"\s+in\s+(?<file>(?:[A-Za-z]:)?[^:]+?):line\s+(?<line>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public DotnetTestParseResult Parse(string stdout, string stderr)
    {
        var combined = CombineOutput(stdout, stderr);
        var result = new DotnetTestParseResult();

        ParseSummary(combined, result);
        result.FailingTests = ParseFailingTests(combined, result.FailedCount);
        result.Success = result.FailedCount == 0;
        result.Summary = BuildSummary(result);
        return result;
    }

    private static void ParseSummary(string combined, DotnetTestParseResult result)
    {
        var summaryMatch = CompactSummaryPattern.Match(combined);
        if (!summaryMatch.Success)
            summaryMatch = LegacySummaryPattern.Match(combined);

        if (!summaryMatch.Success)
            return;

        result.FailedCount = ParseInt(summaryMatch, "failed");
        result.PassedCount = ParseInt(summaryMatch, "passed");
        result.SkippedCount = ParseInt(summaryMatch, "skipped");
        result.TotalTests = ParseInt(summaryMatch, "total");
    }

    private static List<DotnetTestFailureRecord> ParseFailingTests(string combined, int expectedFailureCount)
    {
        var failures = new List<DotnetTestFailureRecord>();
        DotnetTestFailureRecord? current = null;
        var messageLines = new List<string>();
        var stackLines = new List<string>();
        var mode = "";

        foreach (var rawLine in SplitLines(combined))
        {
            if (TryParseFailedTestName(rawLine, out var testName))
            {
                FinalizeFailure(current, messageLines, stackLines, failures);
                current = new DotnetTestFailureRecord { TestName = testName };
                messageLines.Clear();
                stackLines.Clear();
                mode = "";
                continue;
            }

            if (current is null)
                continue;

            var trimmed = rawLine.Trim();
            if (string.Equals(trimmed, "Error Message:", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "Message:", StringComparison.OrdinalIgnoreCase))
            {
                mode = "message";
                continue;
            }

            if (string.Equals(trimmed, "Stack Trace:", StringComparison.OrdinalIgnoreCase))
            {
                mode = "stack";
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (mode == "message")
            {
                messageLines.Add(trimmed);
                continue;
            }

            if (mode == "stack")
            {
                stackLines.Add(trimmed);
                TryApplySourceLocation(current, trimmed);
            }
        }

        FinalizeFailure(current, messageLines, stackLines, failures);

        if (failures.Count == 0 && expectedFailureCount > 0)
        {
            foreach (var inferredName in InferFailureNames(combined).Take(expectedFailureCount))
            {
                failures.Add(new DotnetTestFailureRecord { TestName = inferredName });
            }
        }

        return failures;
    }

    private static void FinalizeFailure(
        DotnetTestFailureRecord? current,
        List<string> messageLines,
        List<string> stackLines,
        List<DotnetTestFailureRecord> failures)
    {
        if (current is null)
            return;

        current.Message = BoundLines(messageLines, 4, 400);
        current.StackTraceExcerpt = BoundLines(stackLines, 6, 800);
        failures.Add(current);
    }

    private static bool TryParseFailedTestName(string line, out string testName)
    {
        var match = FailedTestPattern.Match(line);
        if (!match.Success)
            match = XunitFailedTestPattern.Match(line);

        if (!match.Success)
        {
            testName = "";
            return false;
        }

        testName = match.Groups["name"].Value.Trim();
        return !string.IsNullOrWhiteSpace(testName);
    }

    private static IEnumerable<string> InferFailureNames(string combined)
    {
        var names = new List<string>();
        foreach (var line in SplitLines(combined))
        {
            if (!TryParseFailedTestName(line, out var testName))
                continue;

            if (!names.Contains(testName, StringComparer.OrdinalIgnoreCase))
                names.Add(testName);
        }

        return names;
    }

    private static void TryApplySourceLocation(DotnetTestFailureRecord failure, string stackLine)
    {
        if (!string.IsNullOrWhiteSpace(failure.SourceFilePath))
            return;

        var match = StackFilePattern.Match(stackLine);
        if (!match.Success)
            return;

        failure.SourceFilePath = match.Groups["file"].Value.Trim();
        failure.SourceLine = int.TryParse(match.Groups["line"].Value, out var lineNumber)
            ? lineNumber
            : 0;
    }

    private static string BuildSummary(DotnetTestParseResult result)
    {
        if (result.TotalTests > 0)
        {
            return $"{result.FailedCount} failed, {result.PassedCount} passed, {result.SkippedCount} skipped, {result.TotalTests} total.";
        }

        if (result.FailedCount > 0)
            return $"{result.FailedCount} test(s) failed.";

        return "dotnet test completed.";
    }

    private static int ParseInt(Match match, string groupName)
    {
        return int.TryParse(match.Groups[groupName].Value, out var value) ? value : 0;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static string BoundLines(IEnumerable<string> lines, int maxLines, int maxChars)
    {
        var boundedLines = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(maxLines)
            .ToList();

        if (boundedLines.Count == 0)
            return "";

        var combined = string.Join(Environment.NewLine, boundedLines);
        return combined.Length <= maxChars
            ? combined
            : combined[..maxChars].TrimEnd() + "...";
    }

    private static string CombineOutput(string stdout, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return stdout ?? "";

        if (string.IsNullOrWhiteSpace(stdout))
            return stderr ?? "";

        return stdout + Environment.NewLine + stderr;
    }
}
