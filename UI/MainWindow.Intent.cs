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
            AppendPendingDatabaseMessages();
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

        SaveIntentRecordToWorkspace(BuildIntentTitle(cleaned), cleaned, "");
    }

    private void SaveIntentDraftToWorkspace(IntentDraft draft)
    {
        if (draft is null)
            throw new ArgumentNullException(nameof(draft));

        if (!_workspaceService.HasWorkspace())
            throw new InvalidOperationException("Set a workspace before saving intent.");

        SaveIntentRecordToWorkspace(
            string.IsNullOrWhiteSpace(draft.Title) ? BuildIntentTitle(draft.Objective) : draft.Title,
            draft.Objective,
            BuildIntentNotes(draft));
    }

    private void SaveIntentRecordToWorkspace(string title, string objective, string notes)
    {
        _currentIntent.Title = title;
        _currentIntent.Objective = objective;
        _currentIntent.Notes = notes;

        _ramDbService.SaveCurrentIntent(_workspaceService.WorkspaceRoot, _currentIntent);

        _ramDbService.AddMemorySummary(_workspaceService.WorkspaceRoot, new MemorySummaryRecord
        {
            SourceType = "intent",
            SourceId = "current",
            SummaryText = objective
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

    private static string BuildIntentNotes(IntentDraft draft)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(draft.TargetStack))
        {
            lines.Add($"Likely stack: {draft.TargetStack}");
        }

        if (!string.IsNullOrWhiteSpace(draft.ImplementationDirection))
        {
            lines.Add($"Implementation direction: {draft.ImplementationDirection}");
        }

        foreach (var question in draft.OpenQuestions.Take(2))
        {
            lines.Add($"Open question: {question}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
