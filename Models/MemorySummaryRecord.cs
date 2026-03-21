namespace RAM.Models;

public sealed class MemorySummaryRecord
{
    public long Id { get; set; }
    public string WorkspaceRoot { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string SummaryText { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}