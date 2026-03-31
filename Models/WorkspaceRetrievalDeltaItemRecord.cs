namespace RAM.Models;

public sealed class WorkspaceRetrievalDeltaItemRecord
{
    public string ChunkKey { get; set; } = "";
    public string WorkspacePointId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string DeltaState { get; set; } = "";
    public string PreviousContentSha256 { get; set; } = "";
    public string CurrentContentSha256 { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<WorkspaceRetrievalEvidenceRecord> Evidence { get; set; } = [];
}
