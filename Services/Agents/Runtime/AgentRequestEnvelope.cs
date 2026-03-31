namespace RAM.Services;

public sealed class AgentRequestEnvelope
{
    public string AgentRole { get; set; } = "";
    public string SchemaName { get; set; } = "";
    public string SchemaVersion { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string TimestampUtc { get; set; } = "";
    public string InputPayloadJson { get; set; } = "";
    public List<string> Constraints { get; set; } = [];
}
