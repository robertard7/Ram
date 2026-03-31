using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardExecutionGoalMappingService
{
    private readonly TaskboardExecutionLaneResolutionService _laneResolutionService = new();

    public TaskboardExecutionGoalResolution Resolve(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle,
        string activeTargetRelativePath)
    {
        var laneResolution = _laneResolutionService.Resolve(workspaceRoot, workItem, planTitle, activeTargetRelativePath);
        var goalKind = laneResolution.LaneKind switch
        {
            TaskboardExecutionLaneKind.ToolLane => TaskboardExecutionGoalKind.ToolGoal,
            TaskboardExecutionLaneKind.ChainLane => TaskboardExecutionGoalKind.ChainGoal,
            TaskboardExecutionLaneKind.ManualOnlyLane => TaskboardExecutionGoalKind.ManualOnlyGoal,
            TaskboardExecutionLaneKind.BlockedLane => TaskboardExecutionGoalKind.BlockedGoal,
            _ => TaskboardExecutionGoalKind.Unknown
        };

        return new TaskboardExecutionGoalResolution
        {
            ResolutionId = laneResolution.ResolutionId,
            WorkspaceRoot = laneResolution.WorkspaceRoot,
            SourceWorkItemId = laneResolution.SourceWorkItemId,
            SourceWorkItemTitle = laneResolution.SourceWorkItemTitle,
            OperationKind = laneResolution.OperationKind,
            TargetStack = laneResolution.TargetStack,
            WorkFamily = laneResolution.WorkFamily,
            WorkFamilySource = laneResolution.WorkFamilySource,
            GoalKind = goalKind,
            PromptText = laneResolution.PromptText,
            ResolutionReason = laneResolution.ResolutionReason,
            ResolvedTargetPath = laneResolution.ResolvedTargetPath,
            PhraseFamily = laneResolution.PhraseFamily,
            TemplateId = laneResolution.TemplateId,
            TemplateCandidateIds = [.. laneResolution.TemplateCandidateIds],
            SelectionPath = laneResolution.SelectionPath,
            Eligibility = laneResolution.Eligibility,
            RequestKind = laneResolution.RequestKind,
            ResponseMode = laneResolution.ResponseMode,
            CreatedUtc = laneResolution.CreatedUtc,
            Goal = BuildGoal(workItem, laneResolution, goalKind),
            Blocker = BuildBlocker(workItem, laneResolution),
            Evidence = BuildGoalEvidence(workItem, laneResolution),
            LaneResolution = laneResolution
        };
    }

    private static TaskboardExecutionGoal BuildGoal(
        TaskboardRunWorkItem workItem,
        TaskboardExecutionLaneResolution laneResolution,
        TaskboardExecutionGoalKind goalKind)
    {
        return new TaskboardExecutionGoal
        {
            SourceWorkItemId = laneResolution.SourceWorkItemId,
            OperationKind = FirstNonEmpty(laneResolution.OperationKind, workItem.OperationKind),
            TargetStack = FirstNonEmpty(laneResolution.TargetStack, workItem.TargetStack),
            WorkFamily = FirstNonEmpty(laneResolution.WorkFamily, workItem.WorkFamily),
            WorkFamilySource = laneResolution.WorkFamilySource,
            GoalKind = goalKind,
            SelectedToolId = laneResolution.SelectedToolId,
            SelectedChainTemplateId = laneResolution.SelectedChainTemplateId,
            PhraseFamily = FirstNonEmpty(laneResolution.PhraseFamily, workItem.PhraseFamily),
            TemplateId = FirstNonEmpty(laneResolution.TemplateId, workItem.TemplateId, laneResolution.SelectedChainTemplateId),
            TemplateCandidateIds = laneResolution.TemplateCandidateIds.Count > 0
                ? [.. laneResolution.TemplateCandidateIds]
                : [.. workItem.TemplateCandidateIds],
            SelectionPath = laneResolution.SelectionPath,
            BoundedArguments = new Dictionary<string, string>(laneResolution.BoundedArguments, StringComparer.OrdinalIgnoreCase),
            ResolutionReason = laneResolution.ResolutionReason,
            ResolvedTargetPath = laneResolution.ResolvedTargetPath,
            ExpectedValidationHint = workItem.ValidationHint,
            ExpectedEvidence = BuildGoalEvidence(workItem, laneResolution)
        };
    }

    private static TaskboardExecutionGoalBlocker BuildBlocker(TaskboardRunWorkItem workItem, TaskboardExecutionLaneResolution laneResolution)
    {
        var blockerCode = laneResolution.LaneKind == TaskboardExecutionLaneKind.ManualOnlyLane
            ? ResolveManualOnlyBlockerCode(laneResolution.Eligibility)
            : MapBlockerCode(workItem, laneResolution);

        return new TaskboardExecutionGoalBlocker
        {
            Code = blockerCode,
            Message = laneResolution.Blocker.Message,
            Detail = FirstNonEmpty(laneResolution.Blocker.Detail, laneResolution.Blocker.Message)
        };
    }

    private static List<TaskboardExecutionGoalEvidence> BuildGoalEvidence(
        TaskboardRunWorkItem workItem,
        TaskboardExecutionLaneResolution laneResolution)
    {
        var evidence = laneResolution.Evidence
            .Select(item => new TaskboardExecutionGoalEvidence
            {
                Code = item.Code,
                Value = item.Value,
                Detail = item.Detail
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(workItem.ExpectedArtifact)
            && !evidence.Any(item => string.Equals(item.Code, "expected_artifact", StringComparison.OrdinalIgnoreCase)))
        {
            evidence.Add(new TaskboardExecutionGoalEvidence
            {
                Code = "expected_artifact",
                Value = workItem.ExpectedArtifact,
                Detail = FirstNonEmpty(workItem.ValidationHint, "Expected artifact recorded for deterministic validation.")
            });
        }

        if (!string.IsNullOrWhiteSpace(workItem.ValidationHint)
            && !evidence.Any(item => string.Equals(item.Code, "validation_hint", StringComparison.OrdinalIgnoreCase)))
        {
            evidence.Add(new TaskboardExecutionGoalEvidence
            {
                Code = "validation_hint",
                Value = workItem.ValidationHint,
                Detail = "Recorded validation hint for deterministic post-step proof."
            });
        }

        return evidence;
    }

    private static TaskboardExecutionGoalBlockerCode ResolveManualOnlyBlockerCode(TaskboardExecutionEligibilityKind eligibility)
    {
        return eligibility switch
        {
            TaskboardExecutionEligibilityKind.ManualOnlyElevated => TaskboardExecutionGoalBlockerCode.RequiresElevation,
            TaskboardExecutionEligibilityKind.ManualOnlySystemMutation => TaskboardExecutionGoalBlockerCode.SystemMutationManualOnly,
            TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous => TaskboardExecutionGoalBlockerCode.AmbiguousManualOnly,
            _ => TaskboardExecutionGoalBlockerCode.AmbiguousManualOnly
        };
    }

    private static TaskboardExecutionGoalBlockerCode MapBlockerCode(TaskboardRunWorkItem workItem, TaskboardExecutionLaneResolution laneResolution)
    {
        if (laneResolution.Blocker.Code == TaskboardExecutionLaneBlockerCode.NoLaneCandidates
            && string.IsNullOrWhiteSpace(FirstNonEmpty(laneResolution.PhraseFamily, workItem.PhraseFamily))
            && (workItem.IsDecomposedItem
                || !string.IsNullOrWhiteSpace(workItem.OperationKind)
                || !string.IsNullOrWhiteSpace(workItem.TargetStack)
                || !string.IsNullOrWhiteSpace(workItem.TemplateId)
                || workItem.TemplateCandidateIds.Count > 0))
        {
            return TaskboardExecutionGoalBlockerCode.MissingPhraseFamily;
        }

        return laneResolution.Blocker.Code switch
        {
            TaskboardExecutionLaneBlockerCode.EmptyPrompt => TaskboardExecutionGoalBlockerCode.EmptyPrompt,
            TaskboardExecutionLaneBlockerCode.UnsupportedRuntimeCoverage => TaskboardExecutionGoalBlockerCode.UnsupportedExecutionCoverage,
            TaskboardExecutionLaneBlockerCode.MissingToolLaneForOperationKind => TaskboardExecutionGoalBlockerCode.MissingToolMapping,
            TaskboardExecutionLaneBlockerCode.MissingChainLaneForPhraseFamily => TaskboardExecutionGoalBlockerCode.MissingChainTemplate,
            TaskboardExecutionLaneBlockerCode.MissingTemplateSelection => TaskboardExecutionGoalBlockerCode.MissingTemplateResolution,
            TaskboardExecutionLaneBlockerCode.MissingRequiredArgumentForLane => TaskboardExecutionGoalBlockerCode.MissingRequiredArgument,
            TaskboardExecutionLaneBlockerCode.UnsupportedStackLaneMapping => TaskboardExecutionGoalBlockerCode.UnsupportedStackForOperation,
            TaskboardExecutionLaneBlockerCode.UnresolvedWorkspaceTargetForLane => TaskboardExecutionGoalBlockerCode.UnresolvedWorkspaceTarget,
            TaskboardExecutionLaneBlockerCode.UnknownToolLaneTarget => TaskboardExecutionGoalBlockerCode.UnknownTool,
            TaskboardExecutionLaneBlockerCode.InvalidResponseModeForLane => TaskboardExecutionGoalBlockerCode.InvalidResponseMode,
            TaskboardExecutionLaneBlockerCode.MissingGroupedShellLane => TaskboardExecutionGoalBlockerCode.NoDeterministicLane,
            TaskboardExecutionLaneBlockerCode.MissingUiWiringLane => TaskboardExecutionGoalBlockerCode.NoDeterministicLane,
            TaskboardExecutionLaneBlockerCode.MissingAppStateLane => TaskboardExecutionGoalBlockerCode.NoDeterministicLane,
            TaskboardExecutionLaneBlockerCode.MissingViewmodelScaffoldLane => TaskboardExecutionGoalBlockerCode.NoDeterministicLane,
            TaskboardExecutionLaneBlockerCode.MissingStorageBootstrapLane => TaskboardExecutionGoalBlockerCode.NoDeterministicLane,
            TaskboardExecutionLaneBlockerCode.MissingRepositoryScaffoldLane => TaskboardExecutionGoalBlockerCode.NoDeterministicLane,
            TaskboardExecutionLaneBlockerCode.MissingCheckRunnerLane => TaskboardExecutionGoalBlockerCode.NoDeterministicLane,
            TaskboardExecutionLaneBlockerCode.MissingBuildVerifyLane => TaskboardExecutionGoalBlockerCode.NoDeterministicLane,
            TaskboardExecutionLaneBlockerCode.MissingBuildRepairLane => TaskboardExecutionGoalBlockerCode.NoDeterministicLane,
            TaskboardExecutionLaneBlockerCode.MissingNativeLaneMapping => TaskboardExecutionGoalBlockerCode.NoDeterministicLane,
            TaskboardExecutionLaneBlockerCode.ManualOnlyBoundary => TaskboardExecutionGoalBlockerCode.AmbiguousManualOnly,
            TaskboardExecutionLaneBlockerCode.AmbiguousLaneCandidates => TaskboardExecutionGoalBlockerCode.NoDeterministicLane,
            TaskboardExecutionLaneBlockerCode.NoLaneCandidates => TaskboardExecutionGoalBlockerCode.NoDeterministicLane,
            TaskboardExecutionLaneBlockerCode.UnsafeBlocked => TaskboardExecutionGoalBlockerCode.DestructiveOutsideWorkspace,
            _ => TaskboardExecutionGoalBlockerCode.None
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
}
