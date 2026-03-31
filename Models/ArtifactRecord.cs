namespace RAM.Models;

public sealed class ArtifactRecord
{
    public long Id { get; set; }
    public string WorkspaceRoot { get; set; } = "";
    public string IntentTitle { get; set; } = "";
    public string ArtifactType { get; set; } = "";
    public string Title { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Content { get; set; } = "";
    public string Summary { get; set; } = "";
    public string DataCategory { get; set; } = "";
    public string RetentionClass { get; set; } = "";
    public string LifecycleState { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public long ContentLengthBytes { get; set; }
    public string SourceRunStateId { get; set; } = "";
    public string SourceBatchId { get; set; } = "";
    public string SourceWorkItemId { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
}
