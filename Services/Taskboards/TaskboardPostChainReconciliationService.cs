using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardPostChainReconciliationService
{
    private readonly TaskboardExecutionLaneResolutionService _laneResolutionService = new();
    private readonly TaskboardFollowUpWorkItemSelectionService _followUpSelectionService = new();
    private readonly TaskboardStateSatisfactionService _stateSatisfactionService = new();
    private readonly TaskboardWorkFamilyResolutionService _workFamilyResolutionService = new();
    private readonly TaskboardWorkItemStateRefreshService _workItemStateRefreshService = new();

    public TaskboardPostChainReconciliationResult Reconcile(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord successfulBatch,
        TaskboardWorkItemRunStateRecord successfulWorkItem,
        RamDbService ramDbService)
    {
        _workItemStateRefreshService.Refresh(runState, successfulBatch, successfulWorkItem);
        EnsureWorkFamily(successfulWorkItem);

        var successfulGoal = successfulWorkItem.LastExecutionGoalResolution ?? new TaskboardExecutionGoalResolution();
        var successfulLane = successfulGoal.LaneResolution ?? new TaskboardExecutionLaneResolution();
        var autoSatisfiedSiblingCount = PromoteSatisfiedPendingSiblingWorkItems(
            workspaceRoot,
            runState,
            successfulBatch,
            successfulWorkItem,
            ramDbService);
        var followUpSelection = _followUpSelectionService.SelectNext(runState, selectedAfterPostChainReconciliation: true);
        if (followUpSelection.WorkItem is not null)
        {
            _workItemStateRefreshService.Refresh(
                runState,
                followUpSelection.Batch ?? successfulBatch,
                followUpSelection.WorkItem);
            EnsureWorkFamily(followUpSelection.WorkItem);
        }

        var followUpLane = followUpSelection.WorkItem is null
            ? new TaskboardExecutionLaneResolution()
            : _laneResolutionService.Resolve(
                workspaceRoot,
                _workItemStateRefreshService.ToRunWorkItem(followUpSelection.WorkItem),
                runState.PlanTitle,
                "");
        var followUpResolution = BuildFollowUpResolution(
            runState,
            followUpSelection.Record,
            followUpSelection.WorkItem,
            followUpLane);

        PromoteSuccessfulState(runState, successfulWorkItem, successfulGoal, successfulLane);
        PromoteFollowupState(runState, followUpSelection.Record, followUpResolution);

        var reconciliation = new TaskboardPostChainReconciliationRecord
        {
            ReconciliationId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            PlanImportId = activeImport.ImportId,
            PlanTitle = activeImport.Title,
            SuccessfulBatchId = successfulBatch.BatchId,
            SuccessfulBatchTitle = successfulBatch.Title,
            SuccessfulWorkItemId = successfulWorkItem.WorkItemId,
            SuccessfulWorkItemTitle = successfulWorkItem.Title,
            SuccessfulWorkFamily = FirstMeaningful(successfulLane.WorkFamily, successfulGoal.WorkFamily, successfulWorkItem.WorkFamily),
            SuccessfulPhraseFamily = FirstMeaningful(successfulLane.PhraseFamily, successfulGoal.PhraseFamily, successfulWorkItem.PhraseFamily),
            SuccessfulOperationKind = FirstMeaningful(successfulLane.OperationKind, successfulGoal.OperationKind, successfulWorkItem.OperationKind),
            SuccessfulStackFamily = FirstMeaningful(successfulLane.TargetStack, successfulGoal.TargetStack, successfulWorkItem.TargetStack),
            SuccessfulLaneKind = successfulLane.LaneKind.ToString().ToLowerInvariant(),
            SuccessfulLaneTarget = FirstMeaningful(successfulLane.SelectedChainTemplateId, successfulLane.SelectedToolId),
            FollowupBatchId = followUpSelection.Record.BatchId,
            FollowupBatchTitle = followUpSelection.Record.BatchTitle,
            FollowupWorkItemId = followUpSelection.Record.WorkItemId,
            FollowupWorkItemTitle = followUpSelection.Record.WorkItemTitle,
            FollowupSelectionReason = followUpSelection.Record.SelectionReason,
            FollowupWorkFamily = followUpResolution.WorkFamily,
            FollowupPhraseFamily = followUpResolution.PhraseFamily,
            FollowupPhraseFamilyReasonCode = followUpResolution.PhraseFamilyReasonCode,
            FollowupOperationKind = followUpResolution.OperationKind,
            FollowupOperationKindReasonCode = followUpResolution.OperationKindReasonCode,
            FollowupStackFamily = followUpResolution.StackFamily,
            FollowupStackFamilyReasonCode = followUpResolution.StackFamilyReasonCode,
            FollowupLaneKind = followUpResolution.LaneKind,
            FollowupLaneTarget = followUpResolution.LaneTarget,
            FollowupBlockerCode = followUpResolution.LaneBlockerCode,
            FollowupBlockerMessage = followUpResolution.LaneBlockerMessage,
            Summary = BuildSummary(successfulWorkItem, successfulLane, followUpResolution, autoSatisfiedSiblingCount),
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };

        return new TaskboardPostChainReconciliationResult
        {
            Reconciliation = reconciliation,
            FollowUpSelection = followUpSelection.Record,
            FollowUpResolution = followUpResolution
        };
    }

    private int PromoteSatisfiedPendingSiblingWorkItems(
        string workspaceRoot,
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord successfulWorkItem,
        RamDbService ramDbService)
    {
        if (string.IsNullOrWhiteSpace(successfulWorkItem.SourceWorkItemId))
            return 0;

        var promotedCount = 0;
        foreach (var item in batch.WorkItems
                     .Where(current =>
                         current.Status == TaskboardWorkItemRuntimeStatus.Pending
                         && current.IsDecomposedItem
                         && !string.Equals(current.WorkItemId, successfulWorkItem.WorkItemId, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(current.SourceWorkItemId, successfulWorkItem.SourceWorkItemId, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(current => current.Ordinal <= 0 ? int.MaxValue : current.Ordinal))
        {
            if (item.DirectToolRequest is null)
                continue;

            _workItemStateRefreshService.Refresh(runState, batch, item);
            EnsureWorkFamily(item);

            var satisfaction = _stateSatisfactionService.EvaluatePlannedStep(
                workspaceRoot,
                runState,
                item.WorkItemId,
                item.Title,
                item.WorkFamily,
                item.DirectToolRequest.Clone(),
                FirstMeaningful(item.LastExecutionGoalResolution?.ResolvedTargetPath, item.ExpectedArtifact),
                ramDbService);
            if (!satisfaction.Satisfied || !satisfaction.SkipAllowed)
                continue;

            item.Status = TaskboardWorkItemRuntimeStatus.Passed;
            item.LastResultKind = "state_already_satisfied";
            item.LastResultSummary = BuildAutoSatisfiedSummary(item, satisfaction);
            item.UpdatedUtc = DateTime.UtcNow.ToString("O");
            promotedCount += 1;
        }

        if (promotedCount == 0)
            return 0;

        batch.CompletedWorkItemCount = batch.WorkItems.Count(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed);
        batch.Status = batch.WorkItems.All(item => item.Status is TaskboardWorkItemRuntimeStatus.Passed or TaskboardWorkItemRuntimeStatus.Skipped)
            ? TaskboardBatchRuntimeStatus.Completed
            : TaskboardBatchRuntimeStatus.Pending;
        runState.CompletedWorkItemCount = runState.Batches.Sum(current => current.WorkItems.Count(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed));
        if (runState.Batches.All(current => current.Status is TaskboardBatchRuntimeStatus.Completed or TaskboardBatchRuntimeStatus.Skipped))
        {
            runState.PlanStatus = TaskboardPlanRuntimeStatus.Completed;
        }

        return promotedCount;
    }

    private void EnsureWorkFamily(TaskboardWorkItemRunStateRecord workItem)
    {
        if (!IsMissingOrUnknown(workItem.WorkFamily))
            return;

        var family = _workFamilyResolutionService.Resolve(_workItemStateRefreshService.ToRunWorkItem(workItem));
        workItem.WorkFamily = family.FamilyId;
    }

    private static void PromoteSuccessfulState(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord successfulWorkItem,
        TaskboardExecutionGoalResolution successfulGoal,
        TaskboardExecutionLaneResolution successfulLane)
    {
        runState.LastBlockerReason = "";
        runState.LastBlockerOrigin = "";
        runState.LastBlockerWorkItemId = "";
        runState.LastBlockerWorkItemTitle = "";
        runState.LastBlockerPhase = "";
        runState.LastBlockerWorkFamily = "";
        runState.LastBlockerPhraseFamily = "";
        runState.LastBlockerOperationKind = "";
        runState.LastBlockerStackFamily = "";
        runState.LastForensicsSummary = "";
        runState.LastCompletedWorkItemId = successfulWorkItem.WorkItemId;
        runState.LastCompletedWorkItemTitle = successfulWorkItem.Title;
        runState.LastCompletedWorkFamily = FirstMeaningful(successfulLane.WorkFamily, successfulGoal.WorkFamily, successfulWorkItem.WorkFamily);
        runState.LastCompletedPhraseFamily = FirstMeaningful(successfulLane.PhraseFamily, successfulGoal.PhraseFamily, successfulWorkItem.PhraseFamily);
        runState.LastCompletedOperationKind = FirstMeaningful(successfulLane.OperationKind, successfulGoal.OperationKind, successfulWorkItem.OperationKind);
        runState.LastCompletedStackFamily = FirstMeaningful(successfulLane.TargetStack, successfulGoal.TargetStack, successfulWorkItem.TargetStack);
        runState.LastPhraseFamily = FirstMeaningful(successfulLane.PhraseFamily, successfulGoal.PhraseFamily, successfulWorkItem.PhraseFamily, runState.LastPhraseFamily);
        runState.LastPhraseFamilySource = FirstMeaningful(successfulWorkItem.PhraseFamilySource, runState.LastPhraseFamilySource);
        runState.LastTemplateId = FirstMeaningful(successfulLane.TemplateId, successfulGoal.TemplateId, successfulWorkItem.TemplateId, runState.LastTemplateId);
        runState.LastWorkFamily = FirstMeaningful(successfulLane.WorkFamily, successfulGoal.WorkFamily, successfulWorkItem.WorkFamily, runState.LastWorkFamily);
        runState.LastWorkFamilySource = FirstMeaningful(successfulLane.WorkFamilySource, successfulGoal.WorkFamilySource, runState.LastWorkFamilySource);
        runState.LastExecutionGoalResolution = successfulGoal;
        runState.LastExecutionGoalSummary = BuildExecutionGoalSummary(successfulGoal);
        runState.LastExecutionGoalBlockerCode = "";
    }

    private static void PromoteFollowupState(
        TaskboardPlanRunStateRecord runState,
        TaskboardFollowUpWorkItemSelectionRecord selection,
        TaskboardFollowUpWorkItemResolutionRecord followUpResolution)
    {
        runState.LastFollowupBatchId = selection.BatchId;
        runState.LastFollowupBatchTitle = selection.BatchTitle;
        runState.LastFollowupWorkItemId = selection.WorkItemId;
        runState.LastFollowupWorkItemTitle = selection.WorkItemTitle;
        runState.LastFollowupSelectionReason = selection.SelectionReason;
        runState.LastFollowupWorkFamily = followUpResolution.WorkFamily;
        runState.LastFollowupPhraseFamily = followUpResolution.PhraseFamily;
        runState.LastFollowupOperationKind = followUpResolution.OperationKind;
        runState.LastFollowupStackFamily = followUpResolution.StackFamily;
        runState.LastFollowupPhraseFamilyReasonCode = followUpResolution.PhraseFamilyReasonCode;
        runState.LastFollowupOperationKindReasonCode = followUpResolution.OperationKindReasonCode;
        runState.LastFollowupStackFamilyReasonCode = followUpResolution.StackFamilyReasonCode;
        runState.LastNextWorkFamily = string.IsNullOrWhiteSpace(selection.WorkItemId)
            ? ""
            : FirstMeaningful(followUpResolution.WorkFamily, runState.LastNextWorkFamily);
        runState.LastFollowupResolutionSummary = followUpResolution.Summary;
        runState.LastPostChainReconciliationSummary = string.IsNullOrWhiteSpace(selection.WorkItemId)
            ? "Post-chain reconciliation cleared grouped-shell residue; no unresolved follow-up work item remains."
            : $"Post-chain reconciliation promoted completed work and refreshed follow-up work item `{selection.WorkItemTitle}`.";
    }

    private static TaskboardFollowUpWorkItemResolutionRecord BuildFollowUpResolution(
        TaskboardPlanRunStateRecord runState,
        TaskboardFollowUpWorkItemSelectionRecord selection,
        TaskboardWorkItemRunStateRecord? followUpWorkItem,
        TaskboardExecutionLaneResolution followUpLane)
    {
        var phraseFamily = FirstMeaningful(followUpLane.PhraseFamily, followUpWorkItem?.PhraseFamily);
        var operationKind = FirstMeaningful(followUpLane.OperationKind, followUpWorkItem?.OperationKind);
        var stackFamily = FirstMeaningful(followUpLane.TargetStack, followUpWorkItem?.TargetStack);
        var workFamily = FirstMeaningful(followUpLane.WorkFamily, followUpWorkItem?.WorkFamily);
        var advisoryAttempted = !string.IsNullOrWhiteSpace(followUpWorkItem?.PhraseFamilySource)
            && followUpWorkItem.PhraseFamilySource.Contains("advisory", StringComparison.OrdinalIgnoreCase);
        var hasFollowUpItem = !string.IsNullOrWhiteSpace(selection.WorkItemId);

        return new TaskboardFollowUpWorkItemResolutionRecord
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = runState.WorkspaceRoot,
            PlanImportId = runState.PlanImportId,
            PlanTitle = runState.PlanTitle,
            BatchId = selection.BatchId,
            BatchTitle = selection.BatchTitle,
            WorkItemId = selection.WorkItemId,
            WorkItemTitle = selection.WorkItemTitle,
            SelectionReason = selection.SelectionReason,
            WorkFamily = workFamily,
            PhraseFamily = phraseFamily,
            PhraseFamilyReasonCode = hasFollowUpItem && IsMissingOrUnknown(phraseFamily)
                ? "phrase_family_unresolved_after_refresh"
                : "",
            OperationKind = operationKind,
            OperationKindReasonCode = hasFollowUpItem && IsMissingOrUnknown(operationKind)
                ? "operation_kind_unresolved_after_refresh"
                : "",
            StackFamily = stackFamily,
            StackFamilyReasonCode = hasFollowUpItem && IsMissingOrUnknown(stackFamily)
                ? "stack_family_unresolved_after_refresh"
                : "",
            LaneKind = followUpLane.LaneKind.ToString().ToLowerInvariant(),
            LaneTarget = FirstMeaningful(followUpLane.SelectedChainTemplateId, followUpLane.SelectedToolId, followUpWorkItem?.TemplateId),
            LaneBlockerCode = followUpLane.Blocker.Code == TaskboardExecutionLaneBlockerCode.None
                ? ""
                : followUpLane.Blocker.Code.ToString().ToLowerInvariant(),
            LaneBlockerMessage = followUpLane.Blocker.Message,
            ResolutionOrigin = string.IsNullOrWhiteSpace(selection.WorkItemId)
                ? "post_chain_no_followup"
                : "post_chain_followup_refresh",
            DeterministicRefreshAttempted = !string.IsNullOrWhiteSpace(selection.SelectionReason),
            AdvisoryAssistAttempted = advisoryAttempted,
            Summary = BuildFollowupSummary(runState, selection, workFamily, phraseFamily, operationKind, stackFamily, followUpLane),
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private static string BuildSummary(
        TaskboardWorkItemRunStateRecord successfulWorkItem,
        TaskboardExecutionLaneResolution successfulLane,
        TaskboardFollowUpWorkItemResolutionRecord followUpResolution,
        int autoSatisfiedSiblingCount)
    {
        var autoSatisfiedSummary = autoSatisfiedSiblingCount > 0
            ? $" and auto-satisfied {autoSatisfiedSiblingCount} already-satisfied sibling setup step(s)"
            : "";

        if (string.IsNullOrWhiteSpace(followUpResolution.WorkItemId))
        {
            return $"Promoted successful work item `{successfulWorkItem.Title}` family={DisplayValue(FirstMeaningful(successfulLane.WorkFamily, successfulWorkItem.WorkFamily), "unknown")}{autoSatisfiedSummary} and cleared stale grouped-shell blocker residue; no follow-up work item remains.";
        }

        return $"Promoted successful work item `{successfulWorkItem.Title}`{autoSatisfiedSummary} and refreshed follow-up work item `{followUpResolution.WorkItemTitle}` family={DisplayValue(followUpResolution.WorkFamily, "unknown")} phrase_family={DisplayFollowupValue(followUpResolution.PhraseFamily, followUpResolution.PhraseFamilyReasonCode)} operation_kind={DisplayFollowupValue(followUpResolution.OperationKind, followUpResolution.OperationKindReasonCode)} stack={DisplayFollowupValue(followUpResolution.StackFamily, followUpResolution.StackFamilyReasonCode)}.";
    }

    private static string BuildFollowupSummary(
        TaskboardPlanRunStateRecord runState,
        TaskboardFollowUpWorkItemSelectionRecord selection,
        string workFamily,
        string phraseFamily,
        string operationKind,
        string stackFamily,
        TaskboardExecutionLaneResolution followUpLane)
    {
        if (string.IsNullOrWhiteSpace(selection.WorkItemId))
        {
            return string.Equals(selection.SelectionReason, "behavior_depth_followup_missing", StringComparison.OrdinalIgnoreCase)
                ? $"Latest local step succeeded, but behavior-depth evidence still requires bounded follow-up: {FirstMeaningful(runState.LastBehaviorDepthFollowUpRecommendation, runState.LastBehaviorDepthCompletionRecommendation, "follow-up required")}."
                : "No follow-up work item remains after the latest successful completion.";
        }

        var laneKind = followUpLane.LaneKind == TaskboardExecutionLaneKind.Unknown
            ? "(none)"
            : followUpLane.LaneKind.ToString().ToLowerInvariant();
        var blocker = followUpLane.Blocker.Code == TaskboardExecutionLaneBlockerCode.None
            ? ""
            : $" blocker={followUpLane.Blocker.Code.ToString().ToLowerInvariant()}";
        var continuationWhy = string.Equals(selection.SelectionReason, "behavior_depth_followup_required", StringComparison.OrdinalIgnoreCase)
            ? $" continuation=required_by_behavior_depth({FirstMeaningful(runState.LastBehaviorDepthFollowUpRecommendation, runState.LastBehaviorDepthCompletionRecommendation, "follow-up required")})"
            : string.Equals(selection.SelectionReason, "next_unmet_adjacent_integration_surface", StringComparison.OrdinalIgnoreCase)
                ? $" continuation=next_unmet_adjacent_integration_surface({BuildBehaviorDepthFollowThroughDescriptor(runState)})"
            : "";
        return $"Follow-up work item `{selection.WorkItemTitle}` family={DisplayValue(workFamily, "unknown")} phrase_family={DisplayFollowupValue(phraseFamily, "phrase_family_unresolved_after_refresh")} operation_kind={DisplayFollowupValue(operationKind, "operation_kind_unresolved_after_refresh")} stack={DisplayFollowupValue(stackFamily, "stack_family_unresolved_after_refresh")} lane={laneKind}{blocker}{continuationWhy}.";
    }

    private static string BuildBehaviorDepthFollowThroughDescriptor(TaskboardPlanRunStateRecord runState)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(runState.LastBehaviorDepthNextFollowThroughHint))
            parts.Add($"next_followthrough={runState.LastBehaviorDepthNextFollowThroughHint}");
        if (!string.IsNullOrWhiteSpace(runState.LastBehaviorDepthIntegrationGapKind))
            parts.Add($"gap={runState.LastBehaviorDepthIntegrationGapKind}");
        if (!string.IsNullOrWhiteSpace(runState.LastBehaviorDepthTargetPath))
            parts.Add($"source={runState.LastBehaviorDepthTargetPath}");

        return parts.Count == 0
            ? FirstMeaningful(runState.LastBehaviorDepthFollowUpRecommendation, runState.LastBehaviorDepthCompletionRecommendation, "follow-up required")
            : string.Join(" ", parts);
    }

    private static string BuildExecutionGoalSummary(TaskboardExecutionGoalResolution? goalResolution)
    {
        if (goalResolution is null || goalResolution.GoalKind == TaskboardExecutionGoalKind.Unknown)
            return "";

        var selected = FirstMeaningful(
            goalResolution.Goal.SelectedChainTemplateId,
            goalResolution.Goal.SelectedToolId,
            goalResolution.Blocker.Code == TaskboardExecutionGoalBlockerCode.None
                ? ""
                : goalResolution.Blocker.Code.ToString().ToLowerInvariant());
        var summary = goalResolution.GoalKind.ToString().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(selected))
            summary += $" -> {selected}";

        return summary;
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

    private static string BuildAutoSatisfiedSummary(
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardStateSatisfactionResultRecord satisfaction)
    {
        return $"Marked `{workItem.Title}` already satisfied during post-chain reconciliation ({FirstMeaningful(satisfaction.ReasonCode, "state_already_satisfied")}). {FirstMeaningful(satisfaction.EvidenceSummary, "The required state was already present.")}";
    }
}
