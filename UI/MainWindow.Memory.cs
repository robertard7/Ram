namespace RAM;

public partial class MainWindow
{
    private void ShowRecentMemorySummaries()
    {
        if (!_workspaceService.HasWorkspace())
        {
            AppendOutput("No workspace set. Cannot load memory summaries.");
            return;
        }

        try
        {
            var rows = _ramDbService.LoadRecentMemorySummaries(_workspaceService.WorkspaceRoot, 10);

            if (rows.Count == 0)
            {
                AppendOutput("No memory summaries found.");
                return;
            }

            var lines = new List<string>();
            lines.Add("Recent memory summaries:");

            foreach (var row in rows)
            {
                lines.Add($"- [{row.CreatedUtc}] {row.SourceType}/{row.SourceId}: {row.SummaryText}");
            }

            AppendOutput(string.Join(Environment.NewLine, lines));
        }
        catch (Exception ex)
        {
            AppendOutput("Memory summary load error:" + Environment.NewLine + ex.Message);
        }
    }
}