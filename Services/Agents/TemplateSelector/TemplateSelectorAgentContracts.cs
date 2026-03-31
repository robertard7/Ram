namespace RAM.Services;

public sealed class TemplateSelectorAgentRequestPayload
{
    public string PhraseFamily { get; set; } = "";
    public string StackFamily { get; set; } = "";
    public string WorkItemTitle { get; set; } = "";
    public string WorkItemSummary { get; set; } = "";
    public List<string> CandidateTemplateIds { get; set; } = [];
    public List<string> WorkspaceEvidence { get; set; } = [];
}

public sealed class TemplateSelectorAgentResponsePayload
{
    public string TemplateId { get; set; } = "";
    public string Confidence { get; set; } = "";
    public List<string> ReasonCodes { get; set; } = [];
}

public sealed class TemplateSelectorAgentPresentationResult
{
    public bool Accepted { get; set; }
    public bool FallbackUsed { get; set; }
    public bool Skipped { get; set; }
    public string SkipReason { get; set; } = "";
    public string TraceId { get; set; } = "";
    public AgentInvocationResult Invocation { get; set; } = new();
    public TemplateSelectorAgentResponsePayload Payload { get; set; } = new();
}
