using System.IO;

namespace RAM.Tools;

public sealed class WriteFileTool
{
    public string Write(string workspaceRoot, string fullPath, string content)
    {
        if (Directory.Exists(fullPath))
            throw new InvalidOperationException($"A directory already exists at: {fullPath}");

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content ?? "");

        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');
        return $"Wrote file {relativePath}.";
    }
}
