using System.IO;

namespace RAM.Tools;

public sealed class AppendFileTool
{
    public string Append(string workspaceRoot, string fullPath, string content)
    {
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("File not found.", fullPath);

        File.AppendAllText(fullPath, content ?? "");

        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');
        return $"Appended file {relativePath}.";
    }
}
