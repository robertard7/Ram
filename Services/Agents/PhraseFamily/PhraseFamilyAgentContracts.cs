namespace RAM.Services;

public sealed class PhraseFamilyAgentRequestPayload
{
    public string TaskboardTitle { get; set; } = "";
    public string BatchTitle { get; set; } = "";
    public string WorkItemTitle { get; set; } = "";
    public string WorkItemSummary { get; set; } = "";
    public string WorkItemPrompt { get; set; } = "";
    public List<string> AllowedPhraseFamilies { get; set; } = [];
}

public sealed class PhraseFamilyAgentResponsePayload
{
    public string PhraseFamily { get; set; } = "";
    public string Confidence { get; set; } = "";
    public List<string> RationaleCodes { get; set; } = [];
}

public sealed class PhraseFamilyAgentPresentationResult
{
    public bool Accepted { get; set; }
    public bool FallbackUsed { get; set; }
    public bool Skipped { get; set; }
    public string SkipReason { get; set; } = "";
    public string TraceId { get; set; } = "";
    public AgentInvocationResult Invocation { get; set; } = new();
    public PhraseFamilyAgentResponsePayload Payload { get; set; } = new();
}
