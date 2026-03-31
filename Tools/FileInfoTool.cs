using System.IO;

namespace RAM.Tools;

public sealed class FileInfoTool
{
    public string Describe(string workspaceRoot, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (string.IsNullOrWhiteSpace(fullPath))
            throw new ArgumentException("Path is required.", nameof(fullPath));

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("File not found.", fullPath);

        var info = new FileInfo(fullPath);
        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');

        var lines = new List<string>
        {
            "File info:",
            $"Name: {info.Name}",
            $"Extension: {info.Extension}",
            $"Relative path: {relativePath}",
            $"Full path: {info.FullName}",
            $"Size bytes: {info.Length}",
            $"Created UTC: {info.CreationTimeUtc:O}",
            $"Modified UTC: {info.LastWriteTimeUtc:O}"
        };

        return string.Join(Environment.NewLine, lines);
    }
}
