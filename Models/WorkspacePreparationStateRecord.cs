namespace RAM.Models;

public sealed class WorkspacePreparationStateRecord
{
    public string PreparationVersion { get; set; } = "workspace_preparation_state.v1";
    public string PreparationId { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string SnapshotId { get; set; } = "";
    public string TruthFingerprint { get; set; } = "";
    public string CatalogId { get; set; } = "";
    public string DeltaId { get; set; } = "";
    public string SyncResultId { get; set; } = "";
    public string LastPreparedUtc { get; set; } = "";
    public string LastSuccessfulSyncUtc { get; set; } = "";
    public string EmbedderBackend { get; set; } = "";
    public string EmbedderModel { get; set; } = "";
    public string QdrantEndpoint { get; set; } = "";
    public string QdrantCollection { get; set; } = "";
    public string PreparationStatus { get; set; } = "";
    public string PersistenceStatus { get; set; } = "";
    public string DatabasePersistenceStatus { get; set; } = "";
    public string ArtifactFilePersistenceStatus { get; set; } = "";
    public string SyncMode { get; set; } = "";
    public string SyncStatus { get; set; } = "";
    public long PreparationDurationMs { get; set; }
    public int ChunkCount { get; set; }
    public int IndexedFileCount { get; set; }
    public int ChangedFileCount { get; set; }
    public int RemovedFileCount { get; set; }
    public int FailedItemCount { get; set; }
    public List<WorkspaceRetrievalEvidenceRecord> Evidence { get; set; } = [];
}
