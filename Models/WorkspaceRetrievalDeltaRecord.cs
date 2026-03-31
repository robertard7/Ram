namespace RAM.Models;

public sealed class WorkspaceRetrievalDeltaRecord
{
    public string DeltaVersion { get; set; } = "workspace_retrieval_delta.v1";
    public string DeltaId { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string SnapshotId { get; set; } = "";
    public string TruthFingerprint { get; set; } = "";
    public string PreviousCatalogId { get; set; } = "";
    public string CurrentCatalogId { get; set; } = "";
    public int AddedCount { get; set; }
    public int ChangedCount { get; set; }
    public int RemovedCount { get; set; }
    public int UnchangedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<WorkspaceRetrievalDeltaItemRecord> Items { get; set; } = [];
    public List<WorkspaceRetrievalEvidenceRecord> Evidence { get; set; } = [];
}
