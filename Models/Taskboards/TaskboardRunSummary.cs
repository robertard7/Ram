namespace RAM.Models;

public sealed class TaskboardRunTerminalSummaryRecord
{
    public string SummaryId { get; set; } = "";
    public string SummaryVersion { get; set; } = "taskboard_run_summary.v1";
    public string SummaryArtifactRelativePath { get; set; } = "";
    public string TerminalStatusCode { get; set; } = "";
    public string TerminalFingerprint { get; set; } = "";
    public string RunStateId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string PlanImportId { get; set; } = "";
    public string PlanTitle { get; set; } = "";
    public string ActionName { get; set; } = "";
    public string RunScope { get; set; } = "";
    public string StartedUtc { get; set; } = "";
    public string EndedUtc { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string FinalStatus { get; set; } = "";
    public string TerminalCategory { get; set; } = "";
    public int CompletedBatchCount { get; set; }
    public int TotalBatchCount { get; set; }
    public int CompletedWorkItemCount { get; set; }
    public int TotalWorkItemCount { get; set; }
    public string TerminalBatchId { get; set; } = "";
    public string TerminalBatchTitle { get; set; } = "";
    public string TerminalWorkItemId { get; set; } = "";
    public string TerminalWorkItemTitle { get; set; } = "";
    public string LastSuccessfulChainTemplateId { get; set; } = "";
    public List<string> LastSuccessfulToolNames { get; set; } = [];
    public string PreferredCompletionProofTemplateId { get; set; } = "";
    public List<string> PreferredCompletionProofToolNames { get; set; } = [];
    public string PreferredCompletionProofReason { get; set; } = "";
    public string PreferredCompletionProofProfile { get; set; } = "";
    public string PreferredCompletionProofQuality { get; set; } = "";
    public string PreferredCompletionProofStrength { get; set; } = "";
    public bool PreferredCompletionProofStrongerBehaviorMissing { get; set; }
    public List<string> PreferredCompletionProofInsufficiencyReasons { get; set; } = [];
    public int ChangedFileCount { get; set; }
    public List<string> KeyChangedPaths { get; set; } = [];
    public string BlockerWorkFamily { get; set; } = "";
    public string BlockerPhraseFamily { get; set; } = "";
    public string BlockerOperationKind { get; set; } = "";
    public string BlockerLaneKind { get; set; } = "";
    public string BlockerReason { get; set; } = "";
    public string LastVerificationOutcome { get; set; } = "";
    public string LastVerificationTarget { get; set; } = "";
    public int LastVerificationWarningCount { get; set; }
    public List<string> LastVerificationWarningCodes { get; set; } = [];
    public string WarningPolicyMode { get; set; } = "";
    public string RepairAttemptSummary { get; set; } = "";
    public bool RepairMutationObserved { get; set; }
    public string RepairMutationToolName { get; set; } = "";
    public string RepairMutationUtc { get; set; } = "";
    public List<string> RepairMutatedFiles { get; set; } = [];
    public string VerificationAfterMutationOutcome { get; set; } = "";
    public string VerificationAfterMutationUtc { get; set; } = "";
    public string MaintenanceBaselineSolutionPath { get; set; } = "";
    public List<string> MaintenanceAllowedRoots { get; set; } = [];
    public List<string> MaintenanceExcludedRoots { get; set; } = [];
    public string MaintenanceGuardSummary { get; set; } = "";
    public string LastHeadingPolicyNormalizedTitle { get; set; } = "";
    public string LastHeadingPolicyClass { get; set; } = "";
    public string LastHeadingPolicyTreatment { get; set; } = "";
    public string LastHeadingPolicyReasonCode { get; set; } = "";
    public string LastHeadingPolicySummary { get; set; } = "";
    public string PatchMutationFamily { get; set; } = "";
    public string PatchAllowedEditScope { get; set; } = "";
    public List<string> PatchTargetFiles { get; set; } = [];
    public string PatchContractArtifactRelativePath { get; set; } = "";
    public string PatchPlanArtifactRelativePath { get; set; } = "";
    public string RetrievalBackend { get; set; } = "";
    public string RetrievalEmbedderModel { get; set; } = "";
    public string RetrievalQueryKind { get; set; } = "";
    public int RetrievalHitCount { get; set; }
    public List<string> RetrievalSourceKinds { get; set; } = [];
    public List<string> RetrievalSourcePaths { get; set; } = [];
    public string RetrievalQueryArtifactRelativePath { get; set; } = "";
    public string RetrievalResultArtifactRelativePath { get; set; } = "";
    public string RetrievalContextPacketArtifactRelativePath { get; set; } = "";
    public string RetrievalIndexBatchArtifactRelativePath { get; set; } = "";
    public string NormalizedRunRecordId { get; set; } = "";
    public string NormalizedRunArtifactRelativePath { get; set; } = "";
    public string IndexExportArtifactRelativePath { get; set; } = "";
    public string CorpusExportArtifactRelativePath { get; set; } = "";
    public int FileTouchCount { get; set; }
    public int RepeatedFileTouchCount { get; set; }
    public int TouchedFileCount { get; set; }
    public List<string> TopRepeatedTouchPaths { get; set; } = [];
    public int SatisfactionSkipCount { get; set; }
    public int RepeatedFileTouchesAvoidedCount { get; set; }
    public string LastSatisfactionSkipWorkItemTitle { get; set; } = "";
    public string LastSatisfactionSkipReasonCode { get; set; } = "";
    public List<string> TopSatisfactionSkipReasonCodes { get; set; } = [];
    public string TerminalNote { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}
