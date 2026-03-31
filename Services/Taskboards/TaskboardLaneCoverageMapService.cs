using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardLaneCoverageMapService
{
    private readonly TaskboardExecutionLaneResolutionService _laneResolutionService = new();
    private readonly TaskboardWorkFamilyResolutionService _workFamilyResolutionService = new();
    private readonly TaskboardWorkItemStateRefreshService _workItemStateRefreshService = new();

    public TaskboardLaneCoverageMap Build(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardPlanRunStateRecord runState,
        int maxEntries = 12)
    {
        var entries = new List<TaskboardLaneCoverageEntry>();
        var itemPairs = runState.Batches
            .OrderBy(batch => batch.BatchNumber)
            .SelectMany(batch => batch.WorkItems
                .Where(item => item.Status is not TaskboardWorkItemRuntimeStatus.Skipped)
                .Select(item => new { Batch = batch, Item = item }))
            .ToList();
        var incompleteItems = itemPairs
            .Where(pair => pair.Item.Status is not TaskboardWorkItemRuntimeStatus.Passed)
            .Take(maxEntries)
            .ToList();
        var visibleItems = incompleteItems.Count > 0
            ? incompleteItems
            : itemPairs
                .OrderByDescending(pair => pair.Item.UpdatedUtc, StringComparer.OrdinalIgnoreCase)
                .Take(maxEntries)
                .ToList();

        foreach (var pair in visibleItems)
        {
            _workItemStateRefreshService.Refresh(runState, pair.Batch, pair.Item);
            var previewWorkItem = _workItemStateRefreshService.ToRunWorkItem(pair.Item);
            var family = _workFamilyResolutionService.Resolve(previewWorkItem);
            var freshLane = _laneResolutionService.Resolve(workspaceRoot, previewWorkItem, runState.PlanTitle, "");
            var storedLane = pair.Item.LastExecutionGoalResolution?.LaneResolution;
            var lane = ShouldUseStoredLane(pair.Item, storedLane)
                ? storedLane!
                : freshLane;

            entries.Add(new TaskboardLaneCoverageEntry
            {
                WorkItemId = pair.Item.WorkItemId,
                WorkItemTitle = pair.Item.Title,
                BatchId = pair.Batch.BatchId,
                BatchTitle = pair.Batch.Title,
                WorkFamily = FirstMeaningful(lane.WorkFamily, family.FamilyId),
                WorkFamilySource = FirstMeaningful(lane.WorkFamilySource, family.Source.ToString()),
                PhraseFamily = FirstMeaningful(lane.PhraseFamily, pair.Item.PhraseFamily),
                OperationKind = FirstMeaningful(lane.OperationKind, pair.Item.OperationKind),
                StackFamily = FirstMeaningful(lane.TargetStack, pair.Item.TargetStack),
                TemplateId = FirstMeaningful(lane.TemplateId, pair.Item.TemplateId),
                ExpectedLaneKind = lane.LaneKind,
                SelectedToolId = lane.SelectedToolId,
                SelectedChainTemplateId = lane.SelectedChainTemplateId,
                CandidateLaneIds = lane.Candidates
                    .Select(candidate => FirstNonEmpty(candidate.ChainTemplateId, candidate.ToolId, candidate.CandidateId))
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Status = pair.Item.Status.ToString().ToLowerInvariant(),
                Summary = lane.LaneKind == TaskboardExecutionLaneKind.Unknown || lane.LaneKind == TaskboardExecutionLaneKind.BlockedLane
                    ? FirstNonEmpty(lane.Blocker.Message, family.Reason, "Coverage preview unavailable.")
                    : FirstNonEmpty(lane.ResolutionReason, family.Reason, "Deterministic lane resolved."),
                BlockerCode = lane.Blocker.Code == TaskboardExecutionLaneBlockerCode.None ? "" : lane.Blocker.Code.ToString().ToLowerInvariant(),
                BlockerMessage = lane.Blocker.Message,
                IsCurrent = string.Equals(pair.Item.WorkItemId, runState.CurrentWorkItemId, StringComparison.OrdinalIgnoreCase),
                IsNext = pair.Item.Status == TaskboardWorkItemRuntimeStatus.Pending
                    && string.IsNullOrWhiteSpace(runState.CurrentWorkItemId)
            });
        }

        if (!entries.Any(entry => entry.IsNext))
        {
            var nextEntry = entries.FirstOrDefault(entry => string.Equals(entry.Status, "pending", StringComparison.OrdinalIgnoreCase));
            if (nextEntry is not null)
                nextEntry.IsNext = true;
        }

        var currentFamily = FirstMeaningful(
            entries.FirstOrDefault(entry => entry.IsCurrent)?.WorkFamily,
            runState.CurrentWorkItemId.Length > 0 ? runState.LastWorkFamily : "");
        var nextFamily = FirstMeaningful(
            entries.FirstOrDefault(entry => entry.IsNext)?.WorkFamily,
            entries.FirstOrDefault(entry => string.Equals(entry.Status, "pending", StringComparison.OrdinalIgnoreCase))?.WorkFamily,
            runState.LastFollowupWorkFamily,
            runState.LastBlockerWorkFamily);

        return new TaskboardLaneCoverageMap
        {
            MapId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            PlanImportId = activeImport.ImportId,
            PlanTitle = activeImport.Title,
            RuntimeStateVersion = runState.RuntimeStateVersion,
            RuntimeStateFingerprint = runState.RuntimeStateFingerprint,
            RuntimeStateStatusCode = runState.RuntimeStateStatusCode,
            CurrentBatchId = runState.CurrentBatchId,
            CurrentWorkItemId = runState.CurrentWorkItemId,
            CurrentWorkFamily = currentFamily,
            NextWorkFamily = nextFamily,
            Summary = $"Coverage: current_family={FirstNonEmpty(currentFamily, "(none)")} next_family={FirstNonEmpty(nextFamily, "(none)")} visible_entries={entries.Count}.",
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            Entries = entries
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static string FirstMeaningful(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!IsMissingOrUnknown(value))
                return value!.Trim();
        }

        return "";
    }

    private static bool IsMissingOrUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), "unknown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "(none)", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUseStoredLane(TaskboardWorkItemRunStateRecord item, TaskboardExecutionLaneResolution? storedLane)
    {
        if (storedLane is null)
            return false;

        var hasStoredLane = storedLane.LaneKind != TaskboardExecutionLaneKind.Unknown
            || storedLane.Blocker.Code != TaskboardExecutionLaneBlockerCode.None;
        if (!hasStoredLane)
            return false;

        return item.Status is TaskboardWorkItemRuntimeStatus.Running or TaskboardWorkItemRuntimeStatus.Passed;
    }
}
