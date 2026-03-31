namespace RAM.Models;

public sealed class LatestActionableStateRecord
{
    public string WorkspaceRoot { get; set; } = "";
    public WorkspaceExecutionStateRecord ExecutionState { get; set; } = new();
    public ArtifactRecord? LatestFailureArtifact { get; set; }
    public ArtifactRecord? LatestRepairArtifact { get; set; }
    public ArtifactRecord? LatestPatchArtifact { get; set; }
    public ArtifactRecord? LatestVerificationArtifact { get; set; }
    public ArtifactRecord? LatestAutoValidationArtifact { get; set; }
    public ArtifactRecord? LatestSafetyArtifact { get; set; }
    public string LatestResultKind { get; set; } = "none";
    public string LatestResultToolName { get; set; } = "";
    public string LatestResultTargetPath { get; set; } = "";
    public string LatestResultOutcomeType { get; set; } = "";
    public string LatestResultSummary { get; set; } = "";
    public string LatestBuildFamily { get; set; } = "";
    public string LatestBuildTarget { get; set; } = "";
    public bool HasFailureContext { get; set; }
    public bool HasRepairContext { get; set; }
    public bool HasRepairChain { get; set; }
    public bool HasSafetyAbort { get; set; }
    public bool HasSuccessfulBuild { get; set; }
    public bool HasAutoValidationResult { get; set; }
}
