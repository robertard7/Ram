namespace RAM.Models;

public sealed class TaskboardPostChainReconciliationRecord
{
    public string ReconciliationId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string PlanImportId { get; set; } = "";
    public string PlanTitle { get; set; } = "";
    public string SuccessfulBatchId { get; set; } = "";
    public string SuccessfulBatchTitle { get; set; } = "";
    public string SuccessfulWorkItemId { get; set; } = "";
    public string SuccessfulWorkItemTitle { get; set; } = "";
    public string SuccessfulWorkFamily { get; set; } = "";
    public string SuccessfulPhraseFamily { get; set; } = "";
    public string SuccessfulOperationKind { get; set; } = "";
    public string SuccessfulStackFamily { get; set; } = "";
    public string SuccessfulLaneKind { get; set; } = "";
    public string SuccessfulLaneTarget { get; set; } = "";
    public string FollowupBatchId { get; set; } = "";
    public string FollowupBatchTitle { get; set; } = "";
    public string FollowupWorkItemId { get; set; } = "";
    public string FollowupWorkItemTitle { get; set; } = "";
    public string FollowupSelectionReason { get; set; } = "";
    public string FollowupWorkFamily { get; set; } = "";
    public string FollowupPhraseFamily { get; set; } = "";
    public string FollowupPhraseFamilyReasonCode { get; set; } = "";
    public string FollowupOperationKind { get; set; } = "";
    public string FollowupOperationKindReasonCode { get; set; } = "";
    public string FollowupStackFamily { get; set; } = "";
    public string FollowupStackFamilyReasonCode { get; set; } = "";
    public string FollowupLaneKind { get; set; } = "";
    public string FollowupLaneTarget { get; set; } = "";
    public string FollowupBlockerCode { get; set; } = "";
    public string FollowupBlockerMessage { get; set; } = "";
    public string Summary { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class TaskboardFinalBlockerAssignmentRecord
{
    public string AssignmentId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string PlanImportId { get; set; } = "";
    public string PlanTitle { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string WorkItemTitle { get; set; } = "";
    public string WorkFamily { get; set; } = "";
    public string PhraseFamily { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string StackFamily { get; set; } = "";
    public string GoalKind { get; set; } = "";
    public string LaneKind { get; set; } = "";
    public string LaneBlockerCode { get; set; } = "";
    public string GoalBlockerCode { get; set; } = "";
    public string BlockerOrigin { get; set; } = "";
    public string BlockerPhase { get; set; } = "";
    public int BlockerGeneration { get; set; }
    public string Summary { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}
