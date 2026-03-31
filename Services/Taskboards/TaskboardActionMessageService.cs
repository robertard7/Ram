using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardActionMessageService
{
    private readonly Dictionary<string, DateTime> _recentMessages = new(StringComparer.Ordinal);

    public bool ShouldSuppress(string actionName, string message, int dedupeWindowSeconds)
    {
        if (dedupeWindowSeconds <= 0 || string.IsNullOrWhiteSpace(actionName) || string.IsNullOrWhiteSpace(message))
            return false;

        var now = DateTime.UtcNow;
        var cutoff = now.AddSeconds(-dedupeWindowSeconds);
        foreach (var stale in _recentMessages.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToList())
            _recentMessages.Remove(stale);

        var key = $"{actionName}|{message}";
        if (_recentMessages.TryGetValue(key, out var lastSeen) && lastSeen >= cutoff)
            return true;

        _recentMessages[key] = now;
        return false;
    }

    public string BuildSelectionBanner(TaskboardProjection projection)
    {
        if (projection.SelectedImport is null)
            return "No taskboard import is selected.";

        return projection.SelectedImport.State switch
        {
            TaskboardImportState.ActivePlan => $"Selected import `{projection.SelectedImport.Title}` is already active.",
            TaskboardImportState.ReadyForPromotion => $"Selected import `{projection.SelectedImport.Title}` is ready for activation.",
            TaskboardImportState.Rejected => $"Selected import `{projection.SelectedImport.Title}` is rejected and cannot be activated.",
            TaskboardImportState.Archived => $"Selected import `{projection.SelectedImport.Title}` is archived.",
            TaskboardImportState.Deleted => $"Selected import `{projection.SelectedImport.Title}` was deleted and is hidden from normal workflow.",
            _ => $"Selected import `{projection.SelectedImport.Title}` is in state {projection.SelectedImport.State.ToString().ToLowerInvariant()}."
        };
    }

    public string BuildActionAvailabilityBanner(TaskboardProjection projection)
    {
        var parts = new List<string>();
        if (projection.CanPromoteSelected)
            parts.Add("activate selected");
        if (projection.CanRunActivePlan)
            parts.Add("run active plan");
        if (projection.CanRunSelectedBatch)
            parts.Add("run selected batch");
        if (projection.CanArchiveSelected)
            parts.Add("archive selected");
        if (projection.CanDeleteSelected)
            parts.Add("delete selected");
        if (projection.CanClearRejected)
            parts.Add("clear rejected");
        if (projection.CanClearInactive)
            parts.Add("clear inactive");

        return parts.Count == 0
            ? "No taskboard actions are currently ready."
            : "Ready actions: " + string.Join(", ", parts) + ".";
    }
}
