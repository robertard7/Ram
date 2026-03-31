using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private void ShowArtifactsButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ShowRecentArtifacts();
    }

    private void LoadActiveArtifactFromWorkspace()
    {
        if (!_workspaceService.HasWorkspace())
        {
            _activeArtifact = null;
            ClearActiveTargetPath();
            return;
        }

        try
        {
            _activeArtifact = _ramDbService
                .LoadLatestArtifacts(_workspaceService.WorkspaceRoot, 20)
                .FirstOrDefault(artifact => !_artifactClassificationService.IsTaskboardArtifact(artifact));
            _activeArtifact ??= _ramDbService
                .LoadLatestArtifacts(_workspaceService.WorkspaceRoot, 1)
                .FirstOrDefault();
            AppendPendingDatabaseMessages();
            SyncActiveTargetFromArtifact();
        }
        catch (Exception ex)
        {
            _activeArtifact = null;
            ClearActiveTargetPath();
            AppendOutput("Artifact load error:" + Environment.NewLine + ex.Message);
        }
    }

    private void ShowRecentArtifacts()
    {
        ExecuteToolRequest(new ToolRequest
        {
            ToolName = "show_artifacts",
            Reason = "User requested recent workspace artifacts."
        }, "Manual tool request");
    }
}
