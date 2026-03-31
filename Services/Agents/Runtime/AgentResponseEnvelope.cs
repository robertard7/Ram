namespace RAM.Services;

public sealed class AgentResponseEnvelope
{
    public string RequestId { get; set; } = "";
    public string AgentRole { get; set; } = "";
    public string SchemaName { get; set; } = "";
    public string SchemaVersion { get; set; } = "";
    public string ReceivedUtc { get; set; } = "";
    public string RawModelText { get; set; } = "";
}
