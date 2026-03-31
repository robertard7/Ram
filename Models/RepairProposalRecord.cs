namespace RAM.Models;

public sealed class RepairProposalRecord
{
    public string ProposalId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public long SourceArtifactId { get; set; }
    public string SourceArtifactType { get; set; } = "";
    public string TargetFilePath { get; set; } = "";
    public int TargetLineNumber { get; set; }
    public int TargetColumnNumber { get; set; }
    public string FailureKind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string ProposedActionType { get; set; } = "unknown";
    public List<RepairProposalStep> Steps { get; set; } = [];
    public string Confidence { get; set; } = "";
    public bool RequiresModel { get; set; }
    public string ModelBrief { get; set; } = "";
    public string FailureSummary { get; set; } = "";
    public string FileExcerpt { get; set; } = "";
    public string TargetProjectPath { get; set; } = "";
    public string TargetingStrategy { get; set; } = "";
    public string TargetingSummary { get; set; } = "";
    public string ReferencedSymbolName { get; set; } = "";
    public string ReferencedMemberName { get; set; } = "";
    public string SymbolRecoveryStatus { get; set; } = "";
    public string SymbolRecoverySummary { get; set; } = "";
    public string SymbolRecoveryCandidatePath { get; set; } = "";
    public string SymbolRecoveryCandidateNamespace { get; set; } = "";
    public bool HasAmbiguity { get; set; }
    public string AmbiguitySummary { get; set; } = "";
    public List<string> CandidatePaths { get; set; } = [];
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
    public string RetrievalSummary { get; set; } = "";
}
