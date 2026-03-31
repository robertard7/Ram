namespace RAM.Models;

public sealed class PatchDraftRecord
{
    public string DraftId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string SourceProposalId { get; set; } = "";
    public long SourceProposalArtifactId { get; set; }
    public string SourceProposalArtifactType { get; set; } = "";
    public string FailureKind { get; set; } = "";
    public string TargetFilePath { get; set; } = "";
    public string ModificationIntent { get; set; } = "";
    public string TargetSurfaceType { get; set; } = "";
    public int TargetLineNumber { get; set; }
    public int TargetColumnNumber { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string DraftKind { get; set; } = "unknown";
    public string OriginalExcerpt { get; set; } = "";
    public string ReplacementText { get; set; } = "";
    public string RationaleSummary { get; set; } = "";
    public string Confidence { get; set; } = "";
    public bool CanApplyLocally { get; set; }
    public bool RequiresModel { get; set; }
    public string FailureSummary { get; set; } = "";
    public string ProposalSummary { get; set; } = "";
    public string TargetProjectPath { get; set; } = "";
    public string ModelBrief { get; set; } = "";
    public string PatchContractId { get; set; } = "";
    public string PatchContractArtifactRelativePath { get; set; } = "";
    public string PatchPlanId { get; set; } = "";
    public string PatchPlanArtifactRelativePath { get; set; } = "";
    public string MutationFamily { get; set; } = "";
    public string AllowedEditScope { get; set; } = "";
    public List<string> SupportingFiles { get; set; } = [];
    public string WarningPolicyMode { get; set; } = "";
    public string RetrievalBackend { get; set; } = "";
    public string RetrievalEmbedderModel { get; set; } = "";
    public string RetrievalQueryKind { get; set; } = "";
    public int RetrievalHitCount { get; set; }
    public List<string> RetrievalSourceKinds { get; set; } = [];
    public string RetrievalContextPacketArtifactRelativePath { get; set; } = "";
    public string ReferencedSymbolName { get; set; } = "";
    public string ReferencedMemberName { get; set; } = "";
    public string SymbolRecoveryStatus { get; set; } = "";
    public string SymbolRecoverySummary { get; set; } = "";
    public string SymbolRecoveryCandidatePath { get; set; } = "";
    public string SymbolRecoveryCandidateNamespace { get; set; } = "";
}
