using System.IO;

namespace RAM.Tools;

public sealed class SearchTextTool
{
    public string Search(string workspaceRoot, string pattern, int maxMatches = 40)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace not found: {workspaceRoot}");

        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Search pattern is required.", nameof(pattern));

        maxMatches = Math.Clamp(maxMatches, 1, 80);

        var matches = new List<string>();
        var pending = new Stack<string>();
        pending.Push(workspaceRoot);
        var truncated = false;

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (IsIgnoredDirectory(directory))
                    continue;

                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current))
            {
                if (!IsSearchableFile(file))
                    continue;

                var lineNumber = 0;

                IEnumerable<string> lines;
                try
                {
                    lines = File.ReadLines(file);
                }
                catch
                {
                    continue;
                }

                foreach (var line in lines)
                {
                    lineNumber++;

                    if (!line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var relativePath = NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, file));
                    matches.Add($"{relativePath}:{lineNumber}: {TrimLine(line)}");

                    if (matches.Count >= maxMatches)
                    {
                        truncated = true;
                        break;
                    }
                }

                if (truncated)
                    break;
            }

            if (truncated)
                break;
        }

        if (matches.Count == 0)
            return $"No text matches found for '{pattern}'.";

        var outputLines = new List<string>
        {
            $"Text search results for '{pattern}':"
        };

        foreach (var match in matches)
        {
            outputLines.Add($"- {match}");
        }

        if (truncated)
        {
            outputLines.Add($"[TRUNCATED after {matches.Count} matches]");
        }

        return string.Join(Environment.NewLine, outputLines);
    }

    private static bool IsSearchableFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Length <= 1024 * 1024;
        }
        catch
        {
            return false;
        }
    }

    private static string TrimLine(string line)
    {
        const int maxChars = 160;
        var normalized = line.Trim();

        if (normalized.Length <= maxChars)
            return normalized;

        return normalized[..maxChars].TrimEnd() + "...";
    }

    private static bool IsIgnoredDirectory(string path)
    {
        var name = Path.GetFileName(path);

        return string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, ".ram", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/');
    }
}
