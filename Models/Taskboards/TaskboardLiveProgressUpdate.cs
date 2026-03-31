namespace RAM.Models;

public sealed class TaskboardLiveProgressUpdate
{
    public string PhaseCode { get; set; } = "";
    public string PhaseText { get; set; } = "";
    public string EventKind { get; set; } = "";
    public string ActivitySummary { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string WorkItemTitle { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string ChainTemplateId { get; set; } = "";
}
