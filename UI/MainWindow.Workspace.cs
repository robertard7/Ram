using System.IO;
using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private void SetWorkspaceButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            var path = _toolService.PickFolderWithDialog();

            if (string.IsNullOrWhiteSpace(path))
                return;

            _workspaceService.SetWorkspace(path);
            WorkspaceTextBlock.Text = $"Workspace: {_workspaceService.WorkspaceRoot}";

            SaveSettings();
            PrepareActiveWorkspaceSurface("set workspace");
            LoadIntentFromWorkspace();
            LoadActiveArtifactFromWorkspace();
            RefreshTaskboardUi();

            AddMessage("system", $"Workspace set: {_workspaceService.WorkspaceRoot}");
            AppendOutput($"Workspace set to: {_workspaceService.WorkspaceRoot}");
        }
        catch (Exception ex)
        {
            AddMessage("error", ex.Message);
            AppendOutput("Workspace error:" + Environment.NewLine + ex.Message);
        }
    }

    private void PrepareActiveWorkspaceSurface(string source)
    {
        if (!_workspaceService.HasWorkspace())
            return;

        try
        {
            var settings = ResolveCurrentAppSettingsSnapshot(persist: false);
            var state = _workspacePreparationService.Prepare(_workspaceService.WorkspaceRoot, _ramDbService, settings);
            AppendOutput(FormatWorkspacePreparationOutput(source, state));
        }
        catch (Exception ex)
        {
            AppendOutput($"[prep] {GetWorkspacePreparationLabel()} | failed | source {source} | {ex.Message}");
        }
    }

    private string FormatWorkspacePreparationOutput(string source, WorkspacePreparationStateRecord state)
    {
        var fingerprint = string.IsNullOrWhiteSpace(state.TruthFingerprint)
            ? ""
            : state.TruthFingerprint[..Math.Min(8, state.TruthFingerprint.Length)];
        var parts = new List<string>
        {
            $"[prep] {GetWorkspacePreparationLabel()}",
            state.PreparationStatus,
            $"sync {state.SyncStatus}",
            $"persist {state.PersistenceStatus}",
            $"files {state.IndexedFileCount}",
            $"chunks {state.ChunkCount}",
            $"changed {state.ChangedFileCount}",
            $"removed {state.RemovedFileCount}"
        };

        if (state.PreparationDurationMs > 0)
            parts.Add($"ms {state.PreparationDurationMs}");
        if (!string.IsNullOrWhiteSpace(fingerprint))
            parts.Add($"fp {fingerprint}");
        if (!string.IsNullOrWhiteSpace(source))
            parts.Add($"source {source}");

        return string.Join(" | ", parts);
    }

    private string GetWorkspacePreparationLabel()
    {
        if (!_workspaceService.HasWorkspace())
            return "(no workspace)";

        var root = _workspaceService.WorkspaceRoot?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "";
        return string.IsNullOrWhiteSpace(root)
            ? "(workspace)"
            : Path.GetFileName(root);
    }
}
