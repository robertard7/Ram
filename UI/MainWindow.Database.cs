namespace RAM;

public partial class MainWindow
{
    private void AppendPendingDatabaseMessages()
    {
        if (!_workspaceService.HasWorkspace())
            return;

        foreach (var message in _ramDbService.DrainMigrationMessages(_workspaceService.WorkspaceRoot))
            AppendOutput(message);
    }
}
