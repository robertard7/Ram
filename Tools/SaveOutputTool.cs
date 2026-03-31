using System.IO;
using Microsoft.Win32;

namespace RAM.Tools;

public sealed class SaveOutputTool
{
    public string PickSavePath(string workspaceRoot, string suggestedFileName)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace not found: {workspaceRoot}");

        var dialog = new SaveFileDialog
        {
            Title = "Save output inside workspace",
            InitialDirectory = workspaceRoot,
            FileName = string.IsNullOrWhiteSpace(suggestedFileName) ? "output.txt" : suggestedFileName,
            Filter = "Text files|*.txt;*.md;*.json;*.cs;*.xaml;*.xml;*.log|All files|*.*",
            AddExtension = true,
            OverwritePrompt = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : "";
    }

    public string SaveText(string workspaceRoot, string path, string content)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace not found: {workspaceRoot}");

        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Save path is required.", nameof(path));

        var fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        var fullPath = Path.GetFullPath(path);
        var workspacePrefix = fullWorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var isWorkspaceRoot = string.Equals(fullPath, fullWorkspaceRoot, StringComparison.OrdinalIgnoreCase);
        var isInsideWorkspace = fullPath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase);

        if (!isWorkspaceRoot && !isInsideWorkspace)
            throw new InvalidOperationException("Save path must stay inside the active workspace.");

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content ?? "");

        var relativePath = Path.GetRelativePath(fullWorkspaceRoot, fullPath);
        return $"Saved output to {relativePath}.";
    }
}
