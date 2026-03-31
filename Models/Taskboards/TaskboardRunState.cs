namespace RAM.Models;

public enum TaskboardPlanRuntimeStatus
{
    Active,
    Running,
    Blocked,
    Failed,
    Completed,
    PausedManualOnly
}

public enum TaskboardBatchRuntimeStatus
{
    Pending,
    Running,
    Blocked,
    Failed,
    Completed,
    ManualOnly,
    Skipped
}

public enum TaskboardWorkItemRuntimeStatus
{
    Pending,
    Running,
    Passed,
    Failed,
    Blocked,
    ManualOnly,
    Skipped
}

public enum TaskboardWorkItemResultKind
{
    Passed,
    Failed,
    Blocked,
    ManualOnly,
    NeedsFollowup,
    ValidationFailed
}

public enum TaskboardExecutionBridgeDisposition
{
    Executable,
    ManualOnly,
    Blocked
}

public sealed class TaskboardPlanRunStateRecord
{
    public string RunStateId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string PlanImportId { get; set; } = "";
    public string PlanTitle { get; set; } = "";
    public string RuntimeStateVersion { get; set; } = "";
    public string RuntimeStateFingerprint { get; set; } = "";
    public string RuntimeStateStatusCode { get; set; } = "";
    public string RuntimeStateSummary { get; set; } = "";
    public string RuntimeStateInvalidationReason { get; set; } = "";
    public string RuntimeStateComputedUtc { get; set; } = "";
    public TaskboardPlanRuntimeStatus PlanStatus { get; set; } = TaskboardPlanRuntimeStatus.Active;
    public string CurrentBatchId { get; set; } = "";
    public string CurrentWorkItemId { get; set; } = "";
    public string LastCompletedWorkItemId { get; set; } = "";
    public string LastCompletedWorkItemTitle { get; set; } = "";
    public string LastCompletedWorkFamily { get; set; } = "";
    public string LastCompletedPhraseFamily { get; set; } = "";
    public string LastCompletedOperationKind { get; set; } = "";
    public string LastCompletedStackFamily { get; set; } = "";
    public string LastBlockerReason { get; set; } = "";
    public string LastBlockerOrigin { get; set; } = "";
    public string LastBlockerWorkItemId { get; set; } = "";
    public string LastBlockerWorkItemTitle { get; set; } = "";
    public string LastBlockerPhase { get; set; } = "";
    public string LastBlockerWorkFamily { get; set; } = "";
    public string LastBlockerPhraseFamily { get; set; } = "";
    public string LastBlockerOperationKind { get; set; } = "";
    public string LastBlockerStackFamily { get; set; } = "";
    public int LastBlockerGeneration { get; set; }
    public string LastResultSummary { get; set; } = "";
    public string LastResultKind { get; set; } = "";
    public int CompletedWorkItemCount { get; set; }
    public int TotalWorkItemCount { get; set; }
    public bool AutoRunStarted { get; set; }
    public string StartedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
    public string LastRunEntryAction { get; set; } = "";
    public string LastRunEntryPath { get; set; } = "";
    public string LastRunEntrySelectedImportId { get; set; } = "";
    public string LastRunEntrySelectedImportTitle { get; set; } = "";
    public string LastRunEntrySelectedState { get; set; } = "";
    public string LastRunEntrySelectedBatchId { get; set; } = "";
    public string LastLiveRunEntrySummary { get; set; } = "";
    public bool LastRunUsedActivationHandoff { get; set; }
    public string LastActivationHandoffSummary { get; set; } = "";
    public string CurrentRunPhaseCode { get; set; } = "";
    public string CurrentRunPhaseText { get; set; } = "";
    public string LatestStepSummary { get; set; } = "";
    public string LastExecutionDecisionSummary { get; set; } = "";
    public string LastPlannedToolName { get; set; } = "";
    public string LastPlannedChainTemplateId { get; set; } = "";
    public string LastObservedToolName { get; set; } = "";
    public string LastObservedChainTemplateId { get; set; } = "";
    public string LastMutationToolName { get; set; } = "";
    public string LastMutationUtc { get; set; } = "";
    public List<string> LastMutationTouchedFilePaths { get; set; } = [];
    public string LastVerificationAfterMutationOutcome { get; set; } = "";
    public string LastVerificationAfterMutationUtc { get; set; } = "";
    public string LastRepairTargetPath { get; set; } = "";
    public string LastRepairDraftKind { get; set; } = "";
    public bool LastRepairLocalPatchAvailable { get; set; }
    public string LastRepairTargetingStrategy { get; set; } = "";
    public string LastRepairTargetingSummary { get; set; } = "";
    public string LastRepairReferencedSymbolName { get; set; } = "";
    public string LastRepairReferencedMemberName { get; set; } = "";
    public string LastRepairSymbolRecoveryStatus { get; set; } = "";
    public string LastRepairSymbolRecoverySummary { get; set; } = "";
    public string LastRepairSymbolRecoveryCandidatePath { get; set; } = "";
    public string LastRepairContinuationStatus { get; set; } = "";
    public string LastRepairContinuationSummary { get; set; } = "";
    public string LastBehaviorDepthArtifactPath { get; set; } = "";
    public string LastBehaviorDepthTier { get; set; } = "";
    public string LastBehaviorDepthCompletionRecommendation { get; set; } = "";
    public string LastBehaviorDepthFollowUpRecommendation { get; set; } = "";
    public string LastBehaviorDepthTargetPath { get; set; } = "";
    public string LastBehaviorDepthProfile { get; set; } = "";
    public string LastBehaviorDepthNamespace { get; set; } = "";
    public string LastBehaviorDepthFeatureFamily { get; set; } = "";
    public string LastBehaviorDepthIntegrationGapKind { get; set; } = "";
    public string LastBehaviorDepthNextFollowThroughHint { get; set; } = "";
    public List<string> LastBehaviorDepthCandidateSurfaceHints { get; set; } = [];
    public string LastProjectAttachTargetPath { get; set; } = "";
    public bool LastProjectAttachProjectExistedAtDecision { get; set; }
    public string LastProjectAttachContinuationStatus { get; set; } = "";
    public string LastProjectAttachInsertedStep { get; set; } = "";
    public string LastProjectAttachSummary { get; set; } = "";
    public List<string> RecentObservedToolNames { get; set; } = [];
    public List<string> RecentObservedChainTemplateIds { get; set; } = [];
    public List<TaskboardExecutedToolCallRecord> ExecutedToolCalls { get; set; } = [];
    public string LastCompletedStepSummary { get; set; } = "";
    public TaskboardBuildProfileResolutionRecord LastResolvedBuildProfile { get; set; } = new();
    public string LastDecompositionWorkItemId { get; set; } = "";
    public string LastDecompositionSummary { get; set; } = "";
    public string LastWorkFamily { get; set; } = "";
    public string LastWorkFamilySource { get; set; } = "";
    public string LastCoverageMapSummary { get; set; } = "";
    public string LastNextWorkFamily { get; set; } = "";
    public string LastFollowupBatchId { get; set; } = "";
    public string LastFollowupBatchTitle { get; set; } = "";
    public string LastFollowupWorkItemId { get; set; } = "";
    public string LastFollowupWorkItemTitle { get; set; } = "";
    public string LastFollowupSelectionReason { get; set; } = "";
    public string LastFollowupWorkFamily { get; set; } = "";
    public string LastFollowupPhraseFamily { get; set; } = "";
    public string LastFollowupOperationKind { get; set; } = "";
    public string LastFollowupStackFamily { get; set; } = "";
    public string LastFollowupPhraseFamilyReasonCode { get; set; } = "";
    public string LastFollowupOperationKindReasonCode { get; set; } = "";
    public string LastFollowupStackFamilyReasonCode { get; set; } = "";
    public string LastFollowupResolutionSummary { get; set; } = "";
    public string LastFailureOutcomeType { get; set; } = "";
    public string LastFailureFamily { get; set; } = "";
    public string LastFailureErrorCode { get; set; } = "";
    public string LastFailureNormalizedSummary { get; set; } = "";
    public string LastFailureTargetPath { get; set; } = "";
    public string LastFailureSourcePath { get; set; } = "";
    public string LastFailureRepairContextPath { get; set; } = "";
    public string LastCanonicalOperationKind { get; set; } = "";
    public string LastCanonicalTargetPath { get; set; } = "";
    public string LastCanonicalProjectName { get; set; } = "";
    public string LastCanonicalTemplateHint { get; set; } = "";
    public string LastCanonicalRoleHint { get; set; } = "";
    public string LastCanonicalizationTrace { get; set; } = "";
    public string LastPhraseFamilyRawPhraseText { get; set; } = "";
    public string LastPhraseFamilyNormalizedPhraseText { get; set; } = "";
    public string LastPhraseFamilyClosestKnownFamilyGroup { get; set; } = "";
    public string LastPhraseFamilyResolutionPathTrace { get; set; } = "";
    public string LastPhraseFamilyTerminalResolverStage { get; set; } = "";
    public string LastPhraseFamilyBuilderOperationStatus { get; set; } = "";
    public string LastPhraseFamilyLaneResolutionStatus { get; set; } = "";
    public string LastPhraseFamily { get; set; } = "";
    public string LastPhraseFamilySource { get; set; } = "";
    public string LastPhraseFamilyResolutionSummary { get; set; } = "";
    public List<string> LastPhraseFamilyCandidates { get; set; } = [];
    public string LastPhraseFamilyDeterministicCandidate { get; set; } = "";
    public string LastPhraseFamilyAdvisoryCandidate { get; set; } = "";
    public string LastPhraseFamilyBlockerCode { get; set; } = "";
    public string LastPhraseFamilyTieBreakRuleId { get; set; } = "";
    public string LastPhraseFamilyTieBreakSummary { get; set; } = "";
    public string LastTemplateId { get; set; } = "";
    public List<string> LastTemplateCandidateIds { get; set; } = [];
    public string LastResolvedTargetFileType { get; set; } = "";
    public string LastResolvedTargetRole { get; set; } = "";
    public string LastResolvedTargetProjectName { get; set; } = "";
    public string LastResolvedTargetNamespaceHint { get; set; } = "";
    public string LastResolvedTargetIdentityTrace { get; set; } = "";
    public string LastForensicsSummary { get; set; } = "";
    public TaskboardExecutionGoalResolution LastExecutionGoalResolution { get; set; } = new();
    public string LastExecutionGoalSummary { get; set; } = "";
    public string LastExecutionGoalBlockerCode { get; set; } = "";
    public string LastPostChainReconciliationSummary { get; set; } = "";
    public string LastMaintenanceBaselineSummary { get; set; } = "";
    public string LastMaintenanceBaselineSolutionPath { get; set; } = "";
    public List<string> LastMaintenanceAllowedRoots { get; set; } = [];
    public List<string> LastMaintenanceExcludedRoots { get; set; } = [];
    public string LastMaintenanceGuardReasonCode { get; set; } = "";
    public string LastMaintenanceGuardSummary { get; set; } = "";
    public string LastSupportCoverageWorkItemId { get; set; } = "";
    public string LastSupportCoverageWorkItemTitle { get; set; } = "";
    public string LastSupportCoverageSummary { get; set; } = "";
    public string LastHeadingPolicyWorkItemId { get; set; } = "";
    public string LastHeadingPolicyWorkItemTitle { get; set; } = "";
    public string LastHeadingPolicyNormalizedTitle { get; set; } = "";
    public string LastHeadingPolicyClass { get; set; } = "";
    public string LastHeadingPolicyTreatment { get; set; } = "";
    public string LastHeadingPolicyReasonCode { get; set; } = "";
    public string LastHeadingPolicySummary { get; set; } = "";
    public string LastContradictionGuardWorkItemId { get; set; } = "";
    public string LastContradictionGuardWorkItemTitle { get; set; } = "";
    public string LastContradictionGuardReasonCode { get; set; } = "";
    public string LastContradictionGuardSummary { get; set; } = "";
    public string LastTerminalStatusCode { get; set; } = "";
    public string LastTerminalSummaryFingerprint { get; set; } = "";
    public string LastTerminalSummaryArtifactPath { get; set; } = "";
    public TaskboardRunTerminalSummaryRecord LastTerminalSummary { get; set; } = new();
    public int SatisfactionSkipCount { get; set; }
    public int RepeatedFileTouchesAvoidedCount { get; set; }
    public string LastSatisfactionSkipWorkItemId { get; set; } = "";
    public string LastSatisfactionSkipWorkItemTitle { get; set; } = "";
    public string LastSatisfactionSkipReasonCode { get; set; } = "";
    public string LastSatisfactionSkipEvidenceSummary { get; set; } = "";
    public string LastSatisfactionCheckSummary { get; set; } = "";
    public List<TaskboardBatchRunStateRecord> Batches { get; set; } = [];
    public List<TaskboardRunEventRecord> Events { get; set; } = [];
}

public sealed class TaskboardLiveRunEntryContext
{
    public string ActionName { get; set; } = "";
    public string EntryPath { get; set; } = "";
    public string SelectedImportId { get; set; } = "";
    public string SelectedImportTitle { get; set; } = "";
    public string SelectedImportState { get; set; } = "";
    public string ActivePlanImportId { get; set; } = "";
    public string ActivePlanTitle { get; set; } = "";
    public string SelectedBatchId { get; set; } = "";
    public bool ActivationHandoffPerformed { get; set; }
    public string ActivationHandoffSummary { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class TaskboardLiveRunEntryRecord
{
    public string EntryId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string ActionName { get; set; } = "";
    public string EntryPath { get; set; } = "";
    public string SelectedImportId { get; set; } = "";
    public string SelectedImportTitle { get; set; } = "";
    public string SelectedImportState { get; set; } = "";
    public string ActivePlanImportId { get; set; } = "";
    public string ActivePlanTitle { get; set; } = "";
    public string SelectedBatchId { get; set; } = "";
    public bool ActivationHandoffPerformed { get; set; }
    public string ActivationHandoffSummary { get; set; } = "";
    public string Message { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class TaskboardActivationHandoffRecord
{
    public string HandoffId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string ActionName { get; set; } = "";
    public string SourceImportId { get; set; } = "";
    public string SourceImportTitle { get; set; } = "";
    public string SourceState { get; set; } = "";
    public bool Success { get; set; }
    public bool WasSkipped { get; set; }
    public string StatusCode { get; set; } = "";
    public string Message { get; set; } = "";
    public string ActivatedImportId { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class TaskboardBatchRunStateRecord
{
    public string BatchId { get; set; } = "";
    public int BatchNumber { get; set; }
    public string Title { get; set; } = "";
    public TaskboardBatchRuntimeStatus Status { get; set; } = TaskboardBatchRuntimeStatus.Pending;
    public int CompletedWorkItemCount { get; set; }
    public int TotalWorkItemCount { get; set; }
    public string CurrentWorkItemId { get; set; } = "";
    public string LastResultSummary { get; set; } = "";
    public string LastExecutionGoalSummary { get; set; } = "";
    public List<TaskboardWorkItemRunStateRecord> WorkItems { get; set; } = [];
}

public sealed class TaskboardWorkItemRunStateRecord
{
    public string WorkItemId { get; set; } = "";
    public int Ordinal { get; set; }
    public string DisplayOrdinal { get; set; } = "";
    public string Title { get; set; } = "";
    public string PromptText { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool IsDecomposedItem { get; set; }
    public string SourceWorkItemId { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string TargetStack { get; set; } = "";
    public string WorkFamily { get; set; } = "";
    public string ExpectedArtifact { get; set; } = "";
    public string ValidationHint { get; set; } = "";
    public string PhraseFamily { get; set; } = "";
    public string PhraseFamilySource { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public List<string> TemplateCandidateIds { get; set; } = [];
    public ToolRequest? DirectToolRequest { get; set; }
    public TaskboardExecutionGoalResolution LastExecutionGoalResolution { get; set; } = new();
    public TaskboardWorkItemRuntimeStatus Status { get; set; } = TaskboardWorkItemRuntimeStatus.Pending;
    public string LastResultKind { get; set; } = "";
    public string LastResultSummary { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
}

public sealed class TaskboardRunEventRecord
{
    public string EventId { get; set; } = "";
    public string EventKind { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string Message { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class TaskboardExecutedToolCallRecord
{
    public string ToolName { get; set; } = "";
    public string ChainTemplateId { get; set; } = "";
    public string Stage { get; set; } = "";
    public string ResultClassification { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool MutationObserved { get; set; }
    public List<string> TouchedFilePaths { get; set; } = [];
    public string StructuredDataJson { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class TaskboardExecutionBridgeResult
{
    public TaskboardExecutionBridgeDisposition Disposition { get; set; } = TaskboardExecutionBridgeDisposition.Blocked;
    public TaskboardExecutionEligibilityKind Eligibility { get; set; } = TaskboardExecutionEligibilityKind.Unknown;
    public string PromptText { get; set; } = "";
    public string Reason { get; set; } = "";
    public string ResolvedTargetPath { get; set; } = "";
    public BuilderRequestKind RequestKind { get; set; } = BuilderRequestKind.NormalQuestion;
    public ResponseMode ResponseMode { get; set; } = ResponseMode.None;
    public TaskboardBuildProfileResolutionRecord BuildProfile { get; set; } = new();
    public TaskboardWorkItemDecompositionRecord? Decomposition { get; set; }
    public ToolRequest? ToolRequest { get; set; }
    public TaskboardExecutionGoalResolution ExecutionGoalResolution { get; set; } = new();
}

public sealed class TaskboardExecutionOutcome
{
    public TaskboardWorkItemResultKind ResultKind { get; set; } = TaskboardWorkItemResultKind.ValidationFailed;
    public bool ExecutionAttempted { get; set; }
    public string ResultClassification { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<TaskboardExecutedToolCallRecord> ExecutedToolCalls { get; set; } = [];
}

public sealed class TaskboardAutoRunResult
{
    public string ActionName { get; set; } = "";
    public bool Success { get; set; }
    public bool Started { get; set; }
    public bool WasSkipped { get; set; }
    public bool ExecutionOccurred { get; set; }
    public bool StateChanged { get; set; }
    public bool CompletedPlan { get; set; }
    public string StatusCode { get; set; } = "";
    public string Message { get; set; } = "";
    public TaskboardPlanRunStateRecord RunState { get; set; } = new();
    public TaskboardRunProjection? Projection { get; set; }
}
