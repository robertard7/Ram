using RAM.Models;
using System.IO;

namespace RAM.Services;

public sealed class WorkspaceService
{
    private string _workspaceRoot = "";

    public string WorkspaceRoot => _workspaceRoot;

    public void SetWorkspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Workspace path is required.", nameof(path));

        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Workspace not found: {path}");

        _workspaceRoot = Path.GetFullPath(path);
    }

    public bool HasWorkspace()
    {
        return !string.IsNullOrWhiteSpace(_workspaceRoot);
    }

    public bool IsInsideWorkspace(string path)
    {
        if (!HasWorkspace())
            return false;

        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase);
    }
}