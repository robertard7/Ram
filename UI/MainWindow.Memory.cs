using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private void ShowRecentMemorySummaries()
    {
        ExecuteToolRequest(new ToolRequest
        {
            ToolName = "show_memory",
            Reason = "User requested recent workspace memory summaries."
        }, "Manual tool request");
    }
}
