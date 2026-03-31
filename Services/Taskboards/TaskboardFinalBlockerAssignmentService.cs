using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardFinalBlockerAssignmentService
{
    private readonly TaskboardExecutionLaneResolutionService _laneResolutionService = new();
    private readonly TaskboardWorkFamilyResolutionService _workFamilyResolutionService = new();
    private readonly TaskboardWorkItemStateRefreshService _workItemStateRefreshService = new();

    public TaskboardFinalBlockerAssignmentRecord Assign(
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord workItem,
        string phase,
        string summary)
    {
        _workItemStateRefreshService.Refresh(runState, batch, workItem);
        EnsureWorkFamily(workItem);

        var goal = workItem.LastExecutionGoalResolution ?? new TaskboardExecutionGoalResolution();
        var previewWorkItem = _workItemStateRefreshService.ToRunWorkItem(workItem);
        var freshLane = _laneResolutionService.Resolve(
            runState.WorkspaceRoot,
            previewWorkItem,
            runState.PlanTitle,
            "");
        var lane = SelectAuthoritativeLane(goal.LaneResolution, freshLane);
        PromoteFreshPreviewState(workItem, goal, lane);
        var workFamily = FirstMeaningful(lane.WorkFamily, goal.WorkFamily, workItem.WorkFamily);
        var phraseFamily = FirstMeaningful(lane.PhraseFamily, goal.PhraseFamily, workItem.PhraseFamily);
        var operationKind = FirstMeaningful(lane.OperationKind, goal.OperationKind, workItem.OperationKind);
        var stackFamily = FirstMeaningful(lane.TargetStack, goal.TargetStack, workItem.TargetStack);
        var origin = ResolveOrigin(runState.RuntimeStateStatusCode, phase);

        runState.LastBlockerReason = FirstMeaningful(lane.Blocker.Message, goal.Blocker.Message, summary);
        runState.LastBlockerOrigin = origin;
        runState.LastBlockerWorkItemId = workItem.WorkItemId;
        runState.LastBlockerWorkItemTitle = workItem.Title;
        runState.LastBlockerPhase = phase;
        runState.LastBlockerWorkFamily = workFamily;
        runState.LastBlockerPhraseFamily = phraseFamily;
        runState.LastBlockerOperationKind = operationKind;
        runState.LastBlockerStackFamily = stackFamily;
        runState.LastBlockerGeneration += 1;
        runState.LastWorkFamily = FirstMeaningful(workFamily, runState.LastWorkFamily);
        runState.LastWorkFamilySource = FirstMeaningful(lane.WorkFamilySource, goal.WorkFamilySource, runState.LastWorkFamilySource);
        runState.LastPhraseFamily = FirstMeaningful(phraseFamily, runState.LastPhraseFamily);
        runState.LastPhraseFamilySource = FirstMeaningful(workItem.PhraseFamilySource, runState.LastPhraseFamilySource);
        runState.LastTemplateId = FirstMeaningful(lane.TemplateId, goal.TemplateId, workItem.TemplateId, runState.LastTemplateId);
        runState.LastFollowupBatchId = batch.BatchId;
        runState.LastFollowupBatchTitle = batch.Title;
        runState.LastFollowupWorkItemId = workItem.WorkItemId;
        runState.LastFollowupWorkItemTitle = workItem.Title;
        runState.LastFollowupSelectionReason = "current_unresolved_work_item";
        runState.LastFollowupWorkFamily = workFamily;
        runState.LastFollowupPhraseFamily = phraseFamily;
        runState.LastFollowupOperationKind = operationKind;
        runState.LastFollowupStackFamily = stackFamily;
        runState.LastFollowupPhraseFamilyReasonCode = IsMissingOrUnknown(phraseFamily) ? "phrase_family_unresolved_after_refresh" : "";
        runState.LastFollowupOperationKindReasonCode = IsMissingOrUnknown(operationKind) ? "operation_kind_unresolved_after_refresh" : "";
        runState.LastFollowupStackFamilyReasonCode = IsMissingOrUnknown(stackFamily) ? "stack_family_unresolved_after_refresh" : "";
        runState.LastNextWorkFamily = FirstMeaningful(workFamily, runState.LastNextWorkFamily);
        runState.LastFollowupResolutionSummary = BuildFollowupResolutionSummary(workItem.Title, workFamily, phraseFamily, operationKind, stackFamily, lane);

        return new TaskboardFinalBlockerAssignmentRecord
        {
            AssignmentId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = runState.WorkspaceRoot,
            PlanImportId = runState.PlanImportId,
            PlanTitle = runState.PlanTitle,
            BatchId = batch.BatchId,
            WorkItemId = workItem.WorkItemId,
            WorkItemTitle = workItem.Title,
            WorkFamily = FirstMeaningful(lane.WorkFamily, goal.WorkFamily, workItem.WorkFamily),
            PhraseFamily = FirstMeaningful(lane.PhraseFamily, goal.PhraseFamily, workItem.PhraseFamily),
            OperationKind = FirstMeaningful(lane.OperationKind, goal.OperationKind, workItem.OperationKind),
            StackFamily = FirstMeaningful(lane.TargetStack, goal.TargetStack, workItem.TargetStack),
            GoalKind = goal.GoalKind.ToString().ToLowerInvariant(),
            LaneKind = lane.LaneKind.ToString().ToLowerInvariant(),
            LaneBlockerCode = lane.Blocker.Code == TaskboardExecutionLaneBlockerCode.None
                ? ""
                : lane.Blocker.Code.ToString().ToLowerInvariant(),
            GoalBlockerCode = goal.Blocker.Code == TaskboardExecutionGoalBlockerCode.None
                ? ""
                : goal.Blocker.Code.ToString().ToLowerInvariant(),
            BlockerOrigin = origin,
            BlockerPhase = phase,
            BlockerGeneration = runState.LastBlockerGeneration,
            Summary = runState.LastBlockerReason,
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private void EnsureWorkFamily(TaskboardWorkItemRunStateRecord workItem)
    {
        if (!IsMissingOrUnknown(workItem.WorkFamily))
            return;

        var family = _workFamilyResolutionService.Resolve(_workItemStateRefreshService.ToRunWorkItem(workItem));
        workItem.WorkFamily = family.FamilyId;
    }

    private static TaskboardExecutionLaneResolution SelectAuthoritativeLane(
        TaskboardExecutionLaneResolution? storedLane,
        TaskboardExecutionLaneResolution freshLane)
    {
        if (storedLane is null || !HasUsefulLaneIdentity(storedLane))
            return freshLane;

        return MeasureLaneIdentity(storedLane) >= MeasureLaneIdentity(freshLane)
            ? storedLane
            : freshLane;
    }

    private static bool HasUsefulLaneIdentity(TaskboardExecutionLaneResolution lane)
    {
        if (lane.LaneKind != TaskboardExecutionLaneKind.Unknown)
            return true;

        if (lane.Blocker.Code != TaskboardExecutionLaneBlockerCode.None)
        {
            return !IsMissingOrUnknown(lane.WorkFamily)
                || !IsMissingOrUnknown(lane.PhraseFamily)
                || !IsMissingOrUnknown(lane.OperationKind)
                || !IsMissingOrUnknown(lane.TargetStack);
        }

        return !IsMissingOrUnknown(lane.WorkFamily)
            || !IsMissingOrUnknown(lane.PhraseFamily)
            || !IsMissingOrUnknown(lane.OperationKind)
            || !IsMissingOrUnknown(lane.TargetStack);
    }

    private static int MeasureLaneIdentity(TaskboardExecutionLaneResolution lane)
    {
        var score = 0;
        if (lane.LaneKind != TaskboardExecutionLaneKind.Unknown)
            score += 5;
        if (!IsMissingOrUnknown(lane.WorkFamily))
            score += 4;
        if (!IsMissingOrUnknown(lane.PhraseFamily))
            score += 3;
        if (!IsMissingOrUnknown(lane.OperationKind))
            score += 3;
        if (!IsMissingOrUnknown(lane.TargetStack))
            score += 3;
        if (!IsMissingOrUnknown(lane.TemplateId))
            score += 2;
        if (!string.IsNullOrWhiteSpace(lane.SelectedChainTemplateId) || !string.IsNullOrWhiteSpace(lane.SelectedToolId))
            score += 2;
        if (lane.Blocker.Code != TaskboardExecutionLaneBlockerCode.None)
            score += 1;

        return score;
    }

    private static void PromoteFreshPreviewState(
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionGoalResolution goal,
        TaskboardExecutionLaneResolution lane)
    {
        workItem.WorkFamily = FirstMeaningful(lane.WorkFamily, goal.WorkFamily, workItem.WorkFamily);
        workItem.PhraseFamily = FirstMeaningful(lane.PhraseFamily, goal.PhraseFamily, workItem.PhraseFamily);
        workItem.OperationKind = FirstMeaningful(lane.OperationKind, goal.OperationKind, workItem.OperationKind);
        workItem.TargetStack = FirstMeaningful(lane.TargetStack, goal.TargetStack, workItem.TargetStack);
        workItem.TemplateId = FirstMeaningful(lane.TemplateId, goal.TemplateId, workItem.TemplateId);

        goal.SourceWorkItemId = FirstMeaningful(goal.SourceWorkItemId, workItem.WorkItemId);
        goal.SourceWorkItemTitle = FirstMeaningful(goal.SourceWorkItemTitle, workItem.Title);
        goal.WorkFamily = FirstMeaningful(lane.WorkFamily, goal.WorkFamily);
        goal.PhraseFamily = FirstMeaningful(lane.PhraseFamily, goal.PhraseFamily);
        goal.OperationKind = FirstMeaningful(lane.OperationKind, goal.OperationKind);
        goal.TargetStack = FirstMeaningful(lane.TargetStack, goal.TargetStack);
        goal.TemplateId = FirstMeaningful(lane.TemplateId, goal.TemplateId);

        if (!HasUsefulLaneIdentity(goal.LaneResolution))
            goal.LaneResolution = lane;
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

    private static string ResolveOrigin(string runtimeStateStatusCode, string phase)
    {
        var recomputedPrefix = string.Equals(runtimeStateStatusCode, "rebuilt_from_stale_snapshot", StringComparison.OrdinalIgnoreCase)
            ? "recomputed"
            : "fresh";

        return phase switch
        {
            "fresh_followup" => $"{recomputedPrefix}_followup_blocker",
            "build_failure_recovery" => $"{recomputedPrefix}_build_failure_recovery_blocker",
            "test_failure_recovery" => $"{recomputedPrefix}_test_failure_recovery_blocker",
            "manual_only_boundary" => $"{recomputedPrefix}_manual_only_blocker",
            "decomposition_blocked" => $"{recomputedPrefix}_decomposition_blocker",
            _ => $"{recomputedPrefix}_runtime_blocker"
        };
    }

    private static string BuildFollowupResolutionSummary(
        string workItemTitle,
        string workFamily,
        string phraseFamily,
        string operationKind,
        string stackFamily,
        TaskboardExecutionLaneResolution lane)
    {
        var laneKind = lane.LaneKind == TaskboardExecutionLaneKind.Unknown
            ? "(none)"
            : lane.LaneKind.ToString().ToLowerInvariant();
        var blocker = lane.Blocker.Code == TaskboardExecutionLaneBlockerCode.None
            ? ""
            : $" blocker={lane.Blocker.Code.ToString().ToLowerInvariant()}";
        return $"Follow-up work item `{workItemTitle}` family={DisplayValue(workFamily, "unknown")} phrase_family={DisplayFollowupValue(phraseFamily, "phrase_family_unresolved_after_refresh")} operation_kind={DisplayFollowupValue(operationKind, "operation_kind_unresolved_after_refresh")} stack={DisplayFollowupValue(stackFamily, "stack_family_unresolved_after_refresh")} lane={laneKind}{blocker}.";
    }

    private static string DisplayValue(string? value, string fallback)
    {
        return IsMissingOrUnknown(value) ? fallback : value!.Trim();
    }

    private static string DisplayFollowupValue(string? value, string reasonCode)
    {
        if (!IsMissingOrUnknown(value))
            return value!.Trim();

        return reasonCode switch
        {
            "phrase_family_unresolved_after_refresh" => "(phrase family unresolved after refresh)",
            "operation_kind_unresolved_after_refresh" => "(operation kind unresolved after refresh)",
            "stack_family_unresolved_after_refresh" => "(stack unresolved after refresh)",
            _ => "unknown"
        };
    }
}
