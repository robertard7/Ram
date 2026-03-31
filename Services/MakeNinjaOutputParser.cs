using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class MakeNinjaOutputParser
{
    private static readonly Regex ToolFailurePattern = new(
        @"^(?<tool>make|ninja):.+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex CommandFailurePattern = new(
        @"^(?<message>(?:collect2|ld|clang|gcc|g\+\+).+error.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    public List<DotnetBuildErrorRecord> Parse(string combinedOutput)
    {
        var results = new List<DotnetBuildErrorRecord>();
        var order = 0;

        foreach (Match match in ToolFailurePattern.Matches(combinedOutput ?? ""))
        {
            results.Add(new DotnetBuildErrorRecord
            {
                FilePath = "",
                RawPath = "",
                Severity = "error",
                Code = match.Groups["tool"].Value.Trim().ToLowerInvariant(),
                Message = BoundText(match.Value.Trim(), 240),
                InsideWorkspace = false,
                Order = order++
            });
        }

        foreach (Match match in CommandFailurePattern.Matches(combinedOutput ?? ""))
        {
            results.Add(new DotnetBuildErrorRecord
            {
                FilePath = "",
                RawPath = "",
                Severity = "error",
                Code = "build",
                Message = BoundText(match.Groups["message"].Value.Trim(), 240),
                InsideWorkspace = false,
                Order = order++
            });
        }

        return results;
    }

    private static string BoundText(string text, int maxChars)
    {
        return string.IsNullOrWhiteSpace(text) || text.Length <= maxChars
            ? text
            : text[..maxChars].TrimEnd() + "...";
    }
}
