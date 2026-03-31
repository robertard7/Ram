namespace RAM.Services;

public sealed class AgentInvocationResult
{
    public string TraceId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string ResultCategory { get; set; } = "";
    public bool Success { get; set; }
    public bool Accepted { get; set; }
    public bool FallbackUsed { get; set; }
    public bool Skipped { get; set; }
    public string SkipReason { get; set; } = "";
    public string RawModelText { get; set; } = "";
    public string ParsedPayloadJson { get; set; } = "";
    public AgentValidationResult Validation { get; set; } = new();
    public AgentRejectionReason RejectionReason { get; set; } = AgentRejectionReason.None;
    public long ElapsedMs { get; set; }
    public AgentRequestEnvelope Request { get; set; } = new();
    public AgentResponseEnvelope? Response { get; set; }
}
