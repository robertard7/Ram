using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public static class TaskboardGrammarService
{
    public const string GrammarVersion = "taskboard_grammar.v1";

    private static readonly Regex BatchHeadingRegex = new(
        @"^Batch\s+(?<number>\d+)\s*[—-]\s*(?<title>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryParseBatchHeading(string headingText, out int batchNumber, out string batchTitle)
    {
        batchNumber = 0;
        batchTitle = "";

        var match = BatchHeadingRegex.Match((headingText ?? "").Trim());
        if (!match.Success)
            return false;

        batchNumber = int.TryParse(match.Groups["number"].Value, out var parsedNumber)
            ? parsedNumber
            : 0;
        batchTitle = match.Groups["title"].Value.Trim();
        return batchNumber > 0 && !string.IsNullOrWhiteSpace(batchTitle);
    }

    public static bool IsSupportedMetadataHeading(string headingText)
    {
        return !string.IsNullOrWhiteSpace(headingText)
            && !TryParseBatchHeading(headingText, out _, out _);
    }

    public static string NormalizeHeadingKey(string headingText)
    {
        return Regex.Replace((headingText ?? "").Trim(), @"\s+", " ").ToLowerInvariant();
    }

    public static string BuildCanonicalFormHint()
    {
        return "Use one H1 title, optional metadata H2 sections before batches, then `## Batch N — Name` headings with `- command` bullet steps only.";
    }

    public static List<string> BuildCanonicalExamples()
    {
        return
        [
            "# CODEX TASKBOARD — ServiceDispatchApp Grammar Proof",
            "## Batch 1 — Scaffold",
            "- create dotnet solution ServiceDispatchApp",
            "- create dotnet project wpf ServiceDispatchApp",
            "- add project to solution",
            "- run dotnet build ServiceDispatchApp.sln",
            "## Batch 2 — Verification",
            "- run dotnet test ServiceDispatchApp.sln"
        ];
    }

    public static string BuildExpectedHeadingGrammar(bool batchStarted)
    {
        return batchStarted
            ? "Expected another `## Batch N — Name` heading or `- command` bullet steps inside the current batch."
            : "Expected `## Batch N — Name` or a metadata H2 heading before batches start.";
    }

    public static string BuildExpectedBatchContentGrammar()
    {
        return "Inside a batch, use `- command` bullet steps only. Do not mix prose paragraphs, numbered lists, or nested headings into executable batch content.";
    }
}
