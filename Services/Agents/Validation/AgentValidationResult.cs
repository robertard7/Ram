namespace RAM.Services;

public sealed class AgentValidationResult
{
    public bool IsValid { get; set; }
    public AgentRejectionReason RejectionReason { get; set; } = AgentRejectionReason.None;
    public string Message { get; set; } = "";
    public string NormalizedPayloadJson { get; set; } = "";
}
