namespace RAM.Services;

public sealed class AgentInvocationOptions
{
    public string ModelName { get; set; } = "";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    public int MaxResponseCharacters { get; set; } = 12000;
}
