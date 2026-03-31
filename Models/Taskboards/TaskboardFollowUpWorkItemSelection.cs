namespace RAM.Models;

public sealed class TaskboardFollowUpWorkItemSelectionRecord
{
    public string SelectionId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string PlanImportId { get; set; } = "";
    public string PlanTitle { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string BatchTitle { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string WorkItemTitle { get; set; } = "";
    public string SelectionReason { get; set; } = "";
    public bool SelectedAfterPostChainReconciliation { get; set; }
    public string Summary { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class TaskboardFollowUpWorkItemResolutionRecord
{
    public string ResolutionId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string PlanImportId { get; set; } = "";
    public string PlanTitle { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string BatchTitle { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string WorkItemTitle { get; set; } = "";
    public string SelectionReason { get; set; } = "";
    public string WorkFamily { get; set; } = "";
    public string PhraseFamily { get; set; } = "";
    public string PhraseFamilyReasonCode { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string OperationKindReasonCode { get; set; } = "";
    public string StackFamily { get; set; } = "";
    public string StackFamilyReasonCode { get; set; } = "";
    public string LaneKind { get; set; } = "";
    public string LaneTarget { get; set; } = "";
    public string LaneBlockerCode { get; set; } = "";
    public string LaneBlockerMessage { get; set; } = "";
    public string FailureKind { get; set; } = "";
    public string FailureFamily { get; set; } = "";
    public string FailureErrorCode { get; set; } = "";
    public string FailureNormalizedSummary { get; set; } = "";
    public string FailureTargetPath { get; set; } = "";
    public string FailureSourcePath { get; set; } = "";
    public string RepairContextPath { get; set; } = "";
    public string ResolutionOrigin { get; set; } = "";
    public bool DeterministicRefreshAttempted { get; set; }
    public bool AdvisoryAssistAttempted { get; set; }
    public string Summary { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class TaskboardFollowUpWorkItemSelectionResult
{
    public TaskboardBatchRunStateRecord? Batch { get; set; }
    public TaskboardWorkItemRunStateRecord? WorkItem { get; set; }
    public TaskboardFollowUpWorkItemSelectionRecord Record { get; set; } = new();
}

public sealed class TaskboardPostChainReconciliationResult
{
    public TaskboardPostChainReconciliationRecord Reconciliation { get; set; } = new();
    public TaskboardFollowUpWorkItemSelectionRecord FollowUpSelection { get; set; } = new();
    public TaskboardFollowUpWorkItemResolutionRecord FollowUpResolution { get; set; } = new();
}
