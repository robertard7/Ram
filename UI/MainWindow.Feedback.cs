using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private bool TryHandleExecutionFeedbackRequest(string prompt, BuilderRequestKind requestKind)
    {
        if (requestKind == BuilderRequestKind.BuildRequest || !_workspaceService.HasWorkspace())
            return false;

        var response = _executionFeedbackService.BuildResponse(
            prompt,
            _workspaceService.WorkspaceRoot,
            _ramDbService);
        AppendPendingDatabaseMessages();

        if (string.IsNullOrWhiteSpace(response))
            return false;

        AppendOutput("Deterministic build-feedback routing selected.");
        AddMessage("assistant", response);
        return true;
    }
}
