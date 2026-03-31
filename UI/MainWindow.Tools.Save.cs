using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private void SaveOutputButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_workspaceService.HasWorkspace())
        {
            AppendOutput("No workspace set. Cannot save output.");
            return;
        }

        var outputText = OutputTextBox.Text;
        if (string.IsNullOrWhiteSpace(outputText))
        {
            AppendOutput("No output available to save.");
            return;
        }

        try
        {
            var savePath = _saveOutputTool.PickSavePath(
                _workspaceService.WorkspaceRoot,
                BuildSuggestedOutputFileName());

            if (string.IsNullOrWhiteSpace(savePath))
                return;

            var request = new ToolRequest
            {
                ToolName = "save_output",
                Reason = "User clicked Save Output."
            };

            request.Arguments["path"] = savePath;
            request.Arguments["content"] = outputText;
            request.Arguments["intent_title"] = _currentIntent.Title;

            ExecuteToolRequest(request, "Manual tool request");
        }
        catch (Exception ex)
        {
            AddMessage("error", ex.Message);
            AppendOutput("Save output error:" + Environment.NewLine + ex.Message);
        }
    }

    private static string BuildSuggestedOutputFileName()
    {
        return $"output-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
    }
}
