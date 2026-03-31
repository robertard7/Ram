namespace RAM.Models;

public sealed class ExecutionTraceEventRecord
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string EventKind { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string CommandFamily { get; set; } = "";
    public string BuildFamily { get; set; } = "";
    public string GateDecisionId { get; set; } = "";
    public string GateDecisionSummary { get; set; } = "";
    public string Message { get; set; } = "";
}
