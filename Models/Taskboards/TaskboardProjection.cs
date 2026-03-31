namespace RAM.Models;

public sealed class TaskboardProjection
{
    public string WorkspaceRoot { get; set; } = "";
    public int TotalImportCount { get; set; }
    public int HiddenArchivedCount { get; set; }
    public int RejectedCount { get; set; }
    public int InactiveCount { get; set; }
    public List<TaskboardImportRecord> Imports { get; set; } = [];
    public TaskboardImportRecord? ActiveImport { get; set; }
    public TaskboardDocument? ActiveDocument { get; set; }
    public TaskboardValidationReport? ActiveValidationReport { get; set; }
    public TaskboardImportRecord? SelectedImport { get; set; }
    public TaskboardValidationReport? SelectedValidationReport { get; set; }
    public TaskboardBatchProjection? SelectedBatch { get; set; }
    public TaskboardPlanRunStateRecord? RunState { get; set; }
    public bool IsRuntimeSnapshotStale { get; set; }
    public string ObjectiveSummary { get; set; } = "";
    public string ValidationBanner { get; set; } = "";
    public string SelectedStatusBanner { get; set; } = "";
    public string RunTargetBanner { get; set; } = "";
    public string ActionAvailabilityBanner { get; set; } = "";
    public string RuntimeStatusBanner { get; set; } = "";
    public string RuntimeFreshnessBanner { get; set; } = "";
    public string RuntimeEntryBanner { get; set; } = "";
    public string RuntimeActivationHandoffBanner { get; set; } = "";
    public string RuntimePhaseBanner { get; set; } = "";
    public string RuntimeCurrentStepBanner { get; set; } = "";
    public string RuntimeLatestStepBanner { get; set; } = "";
    public string RuntimeLastCompletedStepBanner { get; set; } = "";
    public string RuntimeNextStepBanner { get; set; } = "";
    public string RuntimeRecentActivityText { get; set; } = "";
    public string RuntimeProgressBanner { get; set; } = "";
    public string RuntimeLastResultBanner { get; set; } = "";
    public string RuntimeBlockerOriginBanner { get; set; } = "";
    public string RuntimeSummaryBanner { get; set; } = "";
    public string RuntimeSummaryModeBanner { get; set; } = "";
    public string RuntimeSummaryText { get; set; } = "";
    public string RuntimeRawSummaryText { get; set; } = "";
    public TaskboardRunTerminalSummaryRecord? VisibleTerminalSummary { get; set; }
    public TaskboardOperatorSummaryPacket? VisibleOperatorSummaryPacket { get; set; }
    public string RuntimeBaselineBanner { get; set; } = "";
    public string RuntimeBuildProfileBanner { get; set; } = "";
    public string RuntimeDecompositionBanner { get; set; } = "";
    public string RuntimeExecutionWiringBanner { get; set; } = "";
    public string RuntimeChainDepthBanner { get; set; } = "";
    public string RuntimeExecutionTraceText { get; set; } = "";
    public string RuntimeMutationProofBanner { get; set; } = "";
    public string RuntimeRepairBanner { get; set; } = "";
    public string RuntimeGenerationGuardrailBanner { get; set; } = "";
    public string RuntimeProjectAttachBanner { get; set; } = "";
    public string RuntimeProjectReferenceBanner { get; set; } = "";
    public string RuntimeHeadingPolicyBanner { get; set; } = "";
    public string RuntimeWorkFamilyBanner { get; set; } = "";
    public string RuntimeCoverageBanner { get; set; } = "";
    public string RuntimeLaneBanner { get; set; } = "";
    public string RuntimeLaneBlockerBanner { get; set; } = "";
    public string RuntimeGoalBanner { get; set; } = "";
    public string RuntimeGoalBlockerBanner { get; set; } = "";
    public List<TaskboardBatchProjection> Batches { get; set; } = [];
    public bool CanPromoteSelected { get; set; }
    public bool CanArchiveSelected { get; set; }
    public bool CanDeleteSelected { get; set; }
    public bool CanRunActivePlan { get; set; }
    public bool CanRunActivePlanViaActivationHandoff { get; set; }
    public bool CanRunSelectedBatch { get; set; }
    public bool CanClearRejected { get; set; }
    public bool CanClearInactive { get; set; }
}

public sealed class TaskboardBatchProjection
{
    public string BatchId { get; set; } = "";
    public int BatchNumber { get; set; }
    public string Title { get; set; } = "";
    public string DisplayLabel { get; set; } = "";
    public int StepCount { get; set; }
    public string RuntimeStatus { get; set; } = "";
    public string DetailsText { get; set; } = "";
}
