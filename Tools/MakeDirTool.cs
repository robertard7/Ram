using System.IO;

namespace RAM.Tools;

public sealed class MakeDirTool
{
    public string Create(string workspaceRoot, string fullPath)
    {
        if (File.Exists(fullPath))
            throw new InvalidOperationException($"A file already exists at: {fullPath}");

        var existed = Directory.Exists(fullPath);
        Directory.CreateDirectory(fullPath);

        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');
        return existed
            ? $"Directory already exists: {relativePath}."
            : $"Created directory {relativePath}.";
    }
}
