namespace RAM.Models;

public sealed class RepairEligibilityResult
{
    public bool IsEligible { get; set; }
    public string ReasonCode { get; set; } = "";
    public string Message { get; set; } = "";
    public WorkspaceExecutionStateRecord ExecutionState { get; set; } = new();
    public List<ArtifactRecord> RecentArtifacts { get; set; } = [];
    public bool HasRecordedFailureState { get; set; }
    public bool HasRecordedVerificationState { get; set; }
    public bool HasPersistedFailureArtifact { get; set; }
    public bool HasPersistedRepairArtifact { get; set; }
    public bool HasPersistedPatchDraft { get; set; }
    public bool HasPersistedPatchApply { get; set; }
    public bool HasPersistedVerificationArtifact { get; set; }

    public bool HasPersistedRepairChain =>
        HasPersistedRepairArtifact
        || HasPersistedPatchDraft
        || HasPersistedPatchApply
        || HasPersistedVerificationArtifact;
}
