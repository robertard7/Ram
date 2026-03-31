using System.IO;

namespace RAM.Tools;

public sealed class ReplaceInFileTool
{
    public string Replace(string workspaceRoot, string fullPath, string oldText, string newText)
    {
        if (string.IsNullOrEmpty(oldText))
            throw new ArgumentException("Old text is required.", nameof(oldText));

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("File not found.", fullPath);

        var content = File.ReadAllText(fullPath);
        var matchCount = CountOccurrences(content, oldText);

        if (matchCount == 0)
        {
            var unchangedPath = Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');
            return $"Text not found in {unchangedPath}. No replacement made.";
        }

        var updated = content.Replace(oldText, newText ?? "", StringComparison.Ordinal);
        File.WriteAllText(fullPath, updated);

        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');
        return $"Replaced {matchCount} occurrence(s) in {relativePath}.";
    }

    private static int CountOccurrences(string content, string oldText)
    {
        var count = 0;
        var index = 0;

        while (true)
        {
            index = content.IndexOf(oldText, index, StringComparison.Ordinal);
            if (index < 0)
                return count;

            count++;
            index += oldText.Length;
        }
    }
}
