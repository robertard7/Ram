using System.IO;

namespace RAM.Tools;

public sealed class CreateWorkspaceTextFileTool
{
    public string Write(string workspaceRoot, string fullPath, string content, string label)
    {
        if (Directory.Exists(fullPath))
            throw new InvalidOperationException($"A directory already exists at: {fullPath}");

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(fullPath, content ?? "");
        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');
        return $"{label} wrote {relativePath}.";
    }
}
