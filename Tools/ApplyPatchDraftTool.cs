using System.IO;
using RAM.Models;

namespace RAM.Tools;

public sealed class ApplyPatchDraftTool
{
    public string Apply(string workspaceRoot, string fullPath, PatchDraftRecord draft, WriteFileTool writeFileTool)
    {
        if (draft is null)
            throw new ArgumentNullException(nameof(draft));

        if (!draft.CanApplyLocally)
            throw new InvalidOperationException("apply_patch_draft failed: the selected patch draft is not eligible for deterministic local apply. Preview the draft and review the model brief instead.");

        if (draft.StartLine <= 0 || draft.EndLine < draft.StartLine)
            throw new InvalidOperationException("apply_patch_draft failed: the selected patch draft does not have a valid line range.");

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("File not found.", fullPath);

        var fileText = File.ReadAllText(fullPath);
        var newline = fileText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hasTrailingNewline = fileText.EndsWith("\r\n", StringComparison.Ordinal) || fileText.EndsWith("\n", StringComparison.Ordinal);
        var normalizedText = fileText.Replace("\r\n", "\n");
        var lines = normalizedText.Split('\n').ToList();

        if (hasTrailingNewline && lines.Count > 0 && lines[^1] == "")
            lines.RemoveAt(lines.Count - 1);

        if (draft.EndLine > lines.Count)
            throw new InvalidOperationException("apply_patch_draft failed: the target file is shorter than the draft expects. Regenerate the patch draft.");

        var currentExcerpt = string.Join("\n", lines.Skip(draft.StartLine - 1).Take(draft.EndLine - draft.StartLine + 1));
        var expectedExcerpt = NormalizeNewlines(draft.OriginalExcerpt);
        if (!string.Equals(currentExcerpt, expectedExcerpt, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "apply_patch_draft failed: target file no longer matches the saved patch draft." + Environment.NewLine
                + $"Expected excerpt: {BuildExcerptSummary(expectedExcerpt)}" + Environment.NewLine
                + $"Current excerpt: {BuildExcerptSummary(currentExcerpt)}" + Environment.NewLine
                + "Regenerate the patch draft before applying it again.");
        }

        var replacementLines = NormalizeNewlines(draft.ReplacementText).Split('\n').ToList();
        var updatedLines = new List<string>();
        updatedLines.AddRange(lines.Take(draft.StartLine - 1));
        updatedLines.AddRange(replacementLines);
        updatedLines.AddRange(lines.Skip(draft.EndLine));

        var updatedText = string.Join(newline, updatedLines);
        if (hasTrailingNewline)
            updatedText += newline;

        var writeMessage = writeFileTool.Write(workspaceRoot, fullPath, updatedText);
        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');

        return string.Join(
            Environment.NewLine,
            "Patch draft applied:",
            $"Target file: {relativePath}",
            $"Lines: {draft.StartLine}-{draft.EndLine}",
            $"Draft kind: {draft.DraftKind}",
            writeMessage);
    }

    private static string NormalizeNewlines(string value)
    {
        return (value ?? "").Replace("\r\n", "\n");
    }

    private static string BuildExcerptSummary(string value)
    {
        const int maxLength = 120;

        var normalized = (value ?? "")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        if (string.IsNullOrWhiteSpace(normalized))
            return "(empty)";

        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength].TrimEnd() + "...";
    }
}
