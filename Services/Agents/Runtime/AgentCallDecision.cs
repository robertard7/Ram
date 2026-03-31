namespace RAM.Services;

public sealed class AgentCallDecision
{
    public bool ShouldCall { get; set; }
    public string Reason { get; set; } = "";
    public string ModelName { get; set; } = "";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}
