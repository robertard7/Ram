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
            LoadIntentFromWorkspace();

            AddMessage("system", $"Workspace set: {_workspaceService.WorkspaceRoot}");
            AppendOutput($"Workspace set to: {_workspaceService.WorkspaceRoot}");
        }
        catch (Exception ex)
        {
            AddMessage("error", ex.Message);
            AppendOutput("Workspace error:" + Environment.NewLine + ex.Message);
        }
    }
}