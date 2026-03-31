using System.IO;
using RAM.Models;
using RAM.Services;

namespace RAM;

public partial class MainWindow
{
    private readonly ArtifactClassificationService _artifactClassificationService = new();

    private string GetActiveTargetRelativePath()
    {
        if (_workspaceService.HasWorkspace()
            && !string.IsNullOrWhiteSpace(_activeTargetPath)
            && FileOrDirectoryExists(_activeTargetPath)
            && _workspaceService.IsInsideWorkspace(_activeTargetPath))
        {
            return NormalizeRelativePath(Path.GetRelativePath(_workspaceService.WorkspaceRoot, _activeTargetPath));
        }

        if (TryGetFileBackedArtifactRelativePath(out var artifactRelativePath))
            return artifactRelativePath;

        return "";
    }

    private void SetActiveTargetPath(string path)
    {
        if (!_workspaceService.HasWorkspace() || string.IsNullOrWhiteSpace(path))
            return;

        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_workspaceService.WorkspaceRoot, path));

        if (!_workspaceService.IsInsideWorkspace(fullPath))
            return;

        _activeTargetPath = fullPath;
    }

    private void ClearActiveTargetPath()
    {
        _activeTargetPath = "";
    }

    private void SyncActiveTargetFromArtifact()
    {
        if (!_workspaceService.HasWorkspace() || _activeArtifact is null)
        {
            ClearActiveTargetPath();
            return;
        }

        if (TryGetFileBackedArtifactRelativePath(out var artifactRelativePath))
        {
            SetActiveTargetPath(artifactRelativePath);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_activeTargetPath)
            && !_workspaceService.IsInsideWorkspace(_activeTargetPath))
        {
            ClearActiveTargetPath();
        }
    }

    private void RefreshToolStateAfterExecution(ToolRequest request, ToolResult result)
    {
        if (!result.Success)
        {
            if (ContainsArtifactSyncMessage(result.ErrorMessage))
                LoadActiveArtifactFromWorkspace();

            return;
        }

        RefreshActiveTargetFromRequest(request);

        if (string.Equals(request.ToolName, "show_artifacts", StringComparison.OrdinalIgnoreCase)
            || ContainsArtifactSyncMessage(result.Output))
        {
            LoadActiveArtifactFromWorkspace();
        }
    }

    private void RefreshActiveTargetFromRequest(ToolRequest request)
    {
        if (request.TryGetArgument("path", out var path)
            && ShouldTrackPathTool(request.ToolName))
        {
            SetActiveTargetPath(path);
            return;
        }

        if (request.TryGetArgument("project", out var project)
            && ShouldTrackProjectTool(request.ToolName))
        {
            SetActiveTargetPath(project);
        }
    }

    private static bool ShouldTrackPathTool(string toolName)
    {
        var normalized = (toolName ?? "").Trim().ToLowerInvariant();
        return normalized is "create_file"
            or "write_file"
            or "append_file"
            or "replace_in_file"
            or "read_file"
            or "read_file_chunk"
            or "file_info"
            or "inspect_project"
            or "open_failure_context"
            or "plan_repair"
            or "preview_patch_draft"
            or "apply_patch_draft"
            or "verify_patch_draft"
            or "save_output";
    }

    private static bool ShouldTrackProjectTool(string toolName)
    {
        var normalized = (toolName ?? "").Trim().ToLowerInvariant();
        return normalized is "dotnet_build"
            or "dotnet_test";
    }

    private static bool FileOrDirectoryExists(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }

    private static string NormalizeRelativePath(string path)
    {
        return (path ?? "").Replace('\\', '/');
    }

    private static bool ContainsArtifactSyncMessage(string output)
    {
        return !string.IsNullOrWhiteSpace(output)
            && output.Contains("Artifact synced:", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetFileBackedArtifactRelativePath(out string relativePath)
    {
        relativePath = "";

        if (!_workspaceService.HasWorkspace()
            || _activeArtifact is null
            || string.IsNullOrWhiteSpace(_activeArtifact.RelativePath)
            || !_artifactClassificationService.IsFileBackedArtifact(_activeArtifact))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(Path.Combine(
            _workspaceService.WorkspaceRoot,
            _activeArtifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!_workspaceService.IsInsideWorkspace(fullPath) || !FileOrDirectoryExists(fullPath))
            return false;

        relativePath = NormalizeRelativePath(Path.GetRelativePath(_workspaceService.WorkspaceRoot, fullPath));
        return true;
    }
}
