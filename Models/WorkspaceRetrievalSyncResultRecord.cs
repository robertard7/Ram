namespace RAM.Models;

public sealed class WorkspaceRetrievalSyncResultRecord
{
    public string SyncVersion { get; set; } = "workspace_retrieval_sync_result.v1";
    public string SyncResultId { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string SnapshotId { get; set; } = "";
    public string TruthFingerprint { get; set; } = "";
    public string CatalogId { get; set; } = "";
    public string DeltaId { get; set; } = "";
    public string ExecutionMode { get; set; } = "";
    public string SyncStatus { get; set; } = "";
    public string BackendType { get; set; } = "";
    public string EmbedderModel { get; set; } = "";
    public string CollectionName { get; set; } = "";
    public string AttemptedUtc { get; set; } = "";
    public string LastSuccessfulSyncUtc { get; set; } = "";
    public string FailureSummary { get; set; } = "";
    public bool FullResyncApplied { get; set; }
    public int PlannedUpsertCount { get; set; }
    public int PlannedDeleteCount { get; set; }
    public int PlannedSkipCount { get; set; }
    public int AppliedUpsertCount { get; set; }
    public int AppliedDeleteCount { get; set; }
    public int AppliedSkipCount { get; set; }
    public int FailedPointCount { get; set; }
    public List<string> PlannedPointIds { get; set; } = [];
    public List<string> PlannedRemovedPointIds { get; set; } = [];
    public List<string> AppliedPointIds { get; set; } = [];
    public List<string> DeletedPointIds { get; set; } = [];
    public List<string> FailedPointIds { get; set; } = [];
    public List<WorkspaceRetrievalEvidenceRecord> Evidence { get; set; } = [];
}
