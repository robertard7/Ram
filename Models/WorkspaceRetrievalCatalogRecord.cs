namespace RAM.Models;

public sealed class WorkspaceRetrievalCatalogRecord
{
    public string CatalogVersion { get; set; } = "workspace_retrieval_catalog.v1";
    public string CatalogId { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string SnapshotId { get; set; } = "";
    public string TruthFingerprint { get; set; } = "";
    public string ChunkPlannerVersion { get; set; } = "";
    public int ChunkCount { get; set; }
    public int IndexedFileCount { get; set; }
    public int SkippedFileCount { get; set; }
    public List<WorkspaceRetrievalChunkRecord> Chunks { get; set; } = [];
    public List<WorkspaceRetrievalEvidenceRecord> Evidence { get; set; } = [];
}
