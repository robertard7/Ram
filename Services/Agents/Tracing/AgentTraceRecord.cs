namespace RAM.Services;

public sealed class AgentTraceRecord
{
    public string TraceId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string AgentRole { get; set; } = "";
    public string Model { get; set; } = "";
    public string SchemaName { get; set; } = "";
    public string SchemaVersion { get; set; } = "";
    public string InputHash { get; set; } = "";
    public string StartedUtc { get; set; } = "";
    public long ElapsedMs { get; set; }
    public string ResultCategory { get; set; } = "";
    public bool Accepted { get; set; }
    public bool FallbackUsed { get; set; }
    public AgentRejectionReason RejectionReason { get; set; } = AgentRejectionReason.None;
    public string RawRequestJson { get; set; } = "";
    public string RawModelText { get; set; } = "";
    public string ValidationMessage { get; set; } = "";
    public string ParsedPayloadJson { get; set; } = "";
}
