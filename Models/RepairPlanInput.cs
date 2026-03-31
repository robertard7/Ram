namespace RAM.Models;

public sealed class RepairPlanInput
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string FailureKind { get; set; } = "unknown";
    public string ContextSource { get; set; } = "";
    public long SourceArtifactId { get; set; }
    public string SourceArtifactType { get; set; } = "";
    public string TargetFilePath { get; set; } = "";
    public int TargetLineNumber { get; set; }
    public int TargetColumnNumber { get; set; }
    public string FailureCode { get; set; } = "";
    public string FailureTitle { get; set; } = "";
    public string FailureMessage { get; set; } = "";
    public string FileExcerpt { get; set; } = "";
    public string TargetProjectPath { get; set; } = "";
    public string TargetingStrategy { get; set; } = "";
    public string TargetingSummary { get; set; } = "";
    public string Confidence { get; set; } = "";
    public bool HasAmbiguity { get; set; }
    public string AmbiguitySummary { get; set; } = "";
    public List<string> CandidatePaths { get; set; } = [];
    public string ExplicitPath { get; set; } = "";
    public string RunStateId { get; set; } = "";
    public List<string> RecentRunTouchedFilePaths { get; set; } = [];
    public string ReferencedSymbolName { get; set; } = "";
    public string ReferencedMemberName { get; set; } = "";
    public string SymbolRecoveryStatus { get; set; } = "";
    public string SymbolRecoverySummary { get; set; } = "";
    public string SymbolRecoveryCandidatePath { get; set; } = "";
    public string SymbolRecoveryCandidateNamespace { get; set; } = "";
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
    public string RetrievalContextText { get; set; } = "";
    public string MaintenanceBaselineSummary { get; set; } = "";
    public string BaselineSolutionPath { get; set; } = "";
    public List<string> BaselineAllowedRoots { get; set; } = [];
    public List<string> BaselineExcludedRoots { get; set; } = [];
}
