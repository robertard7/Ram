using System.IO;

namespace RAM.Tools;

public sealed class CreateFileTool
{
    public string Create(string workspaceRoot, string fullPath, string content)
    {
        if (File.Exists(fullPath))
            throw new InvalidOperationException($"File already exists: {fullPath}");

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Parent directory not found: {directory}");

        using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(content ?? "");

        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');
        return $"Created file {relativePath}.";
    }
}
