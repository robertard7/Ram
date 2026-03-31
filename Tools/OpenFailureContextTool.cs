using System.IO;
using RAM.Models;

namespace RAM.Tools;

public sealed class OpenFailureContextTool
{
    public string Open(
        string workspaceRoot,
        FailureContextResolutionResult resolution,
        ReadFileChunkTool readFileChunkTool)
    {
        if (resolution is null)
            throw new ArgumentNullException(nameof(resolution));

        var lines = new List<string>
        {
            "Failure context:"
        };

        lines.Add($"Source: {DisplayValue(resolution.Source)}");
        lines.Add($"Resolution: {DisplayValue(resolution.Message)}");

        var item = resolution.Item;
        if (item is not null)
        {
            lines.Add($"Title: {DisplayValue(item.Title)}");
            lines.Add($"Path: {DisplayValue(item.RelativePath)}");
            if (item.LineNumber > 0)
            {
                var columnSuffix = item.ColumnNumber > 0 ? $", column {item.ColumnNumber}" : "";
                lines.Add($"Location: line {item.LineNumber}{columnSuffix}");
            }

            lines.Add($"Confidence: {DisplayValue(item.Confidence)}");
            if (!string.IsNullOrWhiteSpace(item.Code))
                lines.Add($"Code: {item.Code}");
            if (!string.IsNullOrWhiteSpace(item.Message))
                lines.Add($"Message: {item.Message}");

            if (item.CandidatePaths.Count > 1)
            {
                lines.Add("Candidate files:");
                foreach (var candidate in item.CandidatePaths.Take(5))
                    lines.Add($"- {candidate}");
            }

            if (!resolution.HasOpenablePath && !string.IsNullOrWhiteSpace(item.RawPath))
                lines.Add($"Raw path: {item.RawPath}");
        }

        if (resolution.HasOpenablePath && item is not null && !string.IsNullOrWhiteSpace(item.RelativePath))
        {
            var fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, item.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            var startLine = item.LineNumber > 0 ? Math.Max(item.LineNumber - 10, 1) : 1;
            lines.Add(readFileChunkTool.ReadLines(workspaceRoot, fullPath, startLine, 40));
        }
        else
        {
            lines.Add("No workspace file was opened because RAM did not have a single justified in-workspace file target.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
