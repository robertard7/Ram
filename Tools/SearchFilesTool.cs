using System.IO;

namespace RAM.Tools;

public sealed class SearchFilesTool
{
    public string Search(string workspaceRoot, string pattern, int maxResults = 50)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace not found: {workspaceRoot}");

        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Search pattern is required.", nameof(pattern));

        maxResults = Math.Clamp(maxResults, 1, 100);

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
                if (!Path.GetFileName(file).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    continue;

                matches.Add(NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, file)));
                if (matches.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }
            }

            if (truncated)
                break;
        }

        if (matches.Count == 0)
            return $"No files matched '{pattern}'.";

        var lines = new List<string>
        {
            $"File search results for '{pattern}':"
        };

        foreach (var match in matches)
        {
            lines.Add($"- {match}");
        }

        if (truncated)
        {
            lines.Add($"[TRUNCATED after {matches.Count} matches]");
        }

        return string.Join(Environment.NewLine, lines);
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
