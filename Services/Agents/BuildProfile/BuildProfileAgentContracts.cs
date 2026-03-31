namespace RAM.Services;

public sealed class BuildProfileAgentRequestPayload
{
    public string TaskboardTitle { get; set; } = "";
    public string ObjectiveExcerpt { get; set; } = "";
    public string BatchTitle { get; set; } = "";
    public string WorkItemTitle { get; set; } = "";
    public string WorkItemSummary { get; set; } = "";
    public List<string> WorkspaceEvidence { get; set; } = [];
    public List<string> ArtifactEvidence { get; set; } = [];
    public List<string> MissingEvidence { get; set; } = [];
    public List<string> AllowedStackFamilies { get; set; } = [];
}

public sealed class BuildProfileAgentResponsePayload
{
    public string StackFamily { get; set; } = "";
    public string Language { get; set; } = "";
    public string Framework { get; set; } = "";
    public string UiShellKind { get; set; } = "";
    public string Confidence { get; set; } = "";
    public List<string> MissingEvidence { get; set; } = [];
    public List<string> RationaleCodes { get; set; } = [];
}

public sealed class BuildProfileAgentPresentationResult
{
    public bool Accepted { get; set; }
    public bool FallbackUsed { get; set; }
    public bool Skipped { get; set; }
    public string SkipReason { get; set; } = "";
    public string TraceId { get; set; } = "";
    public AgentInvocationResult Invocation { get; set; } = new();
    public BuildProfileAgentResponsePayload Payload { get; set; } = new();
}
