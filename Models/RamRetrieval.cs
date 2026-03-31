namespace RAM.Models;

public sealed class RamRetrievalIndexRecord
{
    public string RecordId { get; set; } = "";
    public string RecordVersion { get; set; } = "ram_retrieval_index_record.v1";
    public string WorkspaceRoot { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string Summary { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string ArtifactType { get; set; } = "";
    public long SourceArtifactId { get; set; }
    public string SourceRunStateId { get; set; } = "";
    public string Language { get; set; } = "csharp";
    public string ProjectId { get; set; } = "";
    public string TrustLabel { get; set; } = "";
    public string RecencyLabel { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<string> TargetPaths { get; set; } = [];
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
}

public sealed class RamRetrievalIndexBatchRecord
{
    public string BatchId { get; set; } = "";
    public string BatchVersion { get; set; } = "ram_retrieval_index_batch.v1";
    public string WorkspaceRoot { get; set; } = "";
    public string BackendType { get; set; } = "";
    public string EmbedderModel { get; set; } = "";
    public string CollectionName { get; set; } = "";
    public string QueryKind { get; set; } = "";
    public int RecordCount { get; set; }
    public List<string> SourceKinds { get; set; } = [];
    public List<RamRetrievalIndexRecord> Records { get; set; } = [];
    public string CreatedUtc { get; set; } = "";
}

public sealed class RamRetrievalQueryPacketRecord
{
    public string QueryId { get; set; } = "";
    public string QueryVersion { get; set; } = "ram_retrieval_query_packet.v1";
    public string WorkspaceRoot { get; set; } = "";
    public string QueryKind { get; set; } = "";
    public string ProblemSummary { get; set; } = "";
    public string PlanTitle { get; set; } = "";
    public string MaintenanceMode { get; set; } = "existing_project_maintenance";
    public string Language { get; set; } = "csharp";
    public string StackFamily { get; set; } = "";
    public List<string> ScopePaths { get; set; } = [];
    public List<string> TargetPaths { get; set; } = [];
    public List<string> TrustFilters { get; set; } = [];
    public List<string> RecencyFilters { get; set; } = [];
    public List<string> RequiredTags { get; set; } = [];
    public List<string> RelatedArtifactPaths { get; set; } = [];
    public string CreatedUtc { get; set; } = "";
}

public sealed class RamRetrievalHitRecord
{
    public string RecordId { get; set; } = "";
    public double Score { get; set; }
    public double AdjustedScore { get; set; }
    public string SourceKind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Snippet { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string ArtifactType { get; set; } = "";
    public long SourceArtifactId { get; set; }
    public string SourceRunStateId { get; set; } = "";
    public string TrustLabel { get; set; } = "";
    public string RecencyLabel { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<string> TargetPaths { get; set; } = [];
}

public sealed class RamRetrievalResultRecord
{
    public string ResultId { get; set; } = "";
    public string ResultVersion { get; set; } = "ram_retrieval_result.v1";
    public string QueryId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string BackendType { get; set; } = "";
    public string EmbedderModel { get; set; } = "";
    public string CollectionName { get; set; } = "";
    public int HitCount { get; set; }
    public List<string> SourceKinds { get; set; } = [];
    public List<RamRetrievalHitRecord> Hits { get; set; } = [];
    public string QueryArtifactRelativePath { get; set; } = "";
    public string IndexBatchArtifactRelativePath { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class RamCoderContextPacketRecord
{
    public string PacketId { get; set; } = "";
    public string PacketVersion { get; set; } = "ram_coder_context_packet.v1";
    public string QueryId { get; set; } = "";
    public string ResultId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string BackendType { get; set; } = "";
    public string EmbedderModel { get; set; } = "";
    public string QueryKind { get; set; } = "";
    public int HitCount { get; set; }
    public List<string> SourceKinds { get; set; } = [];
    public List<string> SourcePaths { get; set; } = [];
    public List<string> ScopePaths { get; set; } = [];
    public List<string> TargetPaths { get; set; } = [];
    public List<string> HotspotPaths { get; set; } = [];
    public string RetrievalSummary { get; set; } = "";
    public string ContextText { get; set; } = "";
    public string QueryArtifactRelativePath { get; set; } = "";
    public string RetrievalResultArtifactRelativePath { get; set; } = "";
    public string IndexBatchArtifactRelativePath { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class RamRetrievalBackendStatusRecord
{
    public string BackendType { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string CollectionName { get; set; } = "";
    public bool ConnectionOk { get; set; }
    public bool CollectionReady { get; set; }
    public string StatusSummary { get; set; } = "";
}

public sealed class RamRetrievalPreparationResult
{
    public bool Enabled { get; set; }
    public bool Success { get; set; }
    public string StatusCode { get; set; } = "";
    public string Message { get; set; } = "";
    public RamRetrievalBackendStatusRecord BackendStatus { get; set; } = new();
    public RamRetrievalIndexBatchRecord? IndexBatch { get; set; }
    public ArtifactRecord? IndexBatchArtifact { get; set; }
    public RamRetrievalQueryPacketRecord? QueryPacket { get; set; }
    public ArtifactRecord? QueryArtifact { get; set; }
    public RamRetrievalResultRecord? RetrievalResult { get; set; }
    public ArtifactRecord? RetrievalResultArtifact { get; set; }
    public RamCoderContextPacketRecord? ContextPacket { get; set; }
    public ArtifactRecord? ContextPacketArtifact { get; set; }
}
