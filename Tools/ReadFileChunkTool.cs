using System.IO;

namespace RAM.Tools;

public sealed class ReadFileChunkTool
{
    public string ReadLines(string workspaceRoot, string fullPath, int startLine = 1, int lineCount = 40)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (string.IsNullOrWhiteSpace(fullPath))
            throw new ArgumentException("Path is required.", nameof(fullPath));

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("File not found.", fullPath);

        startLine = Math.Max(1, startLine);
        lineCount = Math.Clamp(lineCount, 1, 200);

        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');
        var endLine = startLine + lineCount - 1;
        var lines = new List<string>
        {
            $"File chunk: {relativePath} (lines {startLine}-{endLine})"
        };

        var currentLine = 0;
        foreach (var line in File.ReadLines(fullPath))
        {
            currentLine++;

            if (currentLine < startLine)
                continue;

            if (currentLine > endLine)
                break;

            lines.Add($"{currentLine,4}: {line}");
        }

        if (lines.Count == 1)
            return $"Requested line range is outside the file: {relativePath}";

        return string.Join(Environment.NewLine, lines);
    }
}
