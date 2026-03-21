using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private void LoadIntentFromWorkspace()
    {
        if (!_workspaceService.HasWorkspace())
        {
            _currentIntent = new IntentRecord();
            IntentTitleTextBlock.Text = "Intent: (not set)";
            IntentUpdatedTextBlock.Text = "";
            return;
        }

        try
        {
            _currentIntent = _ramDbService.LoadCurrentIntent(_workspaceService.WorkspaceRoot);
            RefreshIntentUi();
        }
        catch (Exception ex)
        {
            _currentIntent = new IntentRecord();
            IntentTitleTextBlock.Text = "Intent: (load failed)";
            IntentUpdatedTextBlock.Text = "";
            AppendOutput("Intent load error:" + Environment.NewLine + ex.Message);
        }
    }

    private void SaveIntentToWorkspace(string objective)
    {
        if (!_workspaceService.HasWorkspace())
            throw new InvalidOperationException("Set a workspace before saving intent.");

        var cleaned = objective.Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            throw new ArgumentException("Intent text is required.", nameof(objective));

        _currentIntent.Title = BuildIntentTitle(cleaned);
        _currentIntent.Objective = cleaned;
        _currentIntent.Notes = "";

        _ramDbService.SaveCurrentIntent(_workspaceService.WorkspaceRoot, _currentIntent);

        _ramDbService.AddMemorySummary(_workspaceService.WorkspaceRoot, new MemorySummaryRecord
        {
            SourceType = "intent",
            SourceId = "current",
            SummaryText = cleaned
        });

        RefreshIntentUi();
    }

    private void RefreshIntentUi()
    {
        if (string.IsNullOrWhiteSpace(_currentIntent.Objective))
        {
            IntentTitleTextBlock.Text = "Intent: (not set)";
            IntentUpdatedTextBlock.Text = "";
            return;
        }

        IntentTitleTextBlock.Text = $"Intent: {_currentIntent.Title}";
        IntentUpdatedTextBlock.Text = string.IsNullOrWhiteSpace(_currentIntent.LastUpdatedUtc)
            ? ""
            : $"Updated: {_currentIntent.LastUpdatedUtc}";
    }

    private static string BuildIntentTitle(string text)
    {
        const int max = 48;

        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();

        if (singleLine.Length <= max)
            return singleLine;

        return singleLine[..max].TrimEnd() + "...";
    }
}