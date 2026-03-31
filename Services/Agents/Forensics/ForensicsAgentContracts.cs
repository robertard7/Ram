namespace RAM.Services;

public sealed class ForensicsAgentRequestPayload
{
    public string WorkItemTitle { get; set; } = "";
    public string GoalKind { get; set; } = "";
    public string BlockerCode { get; set; } = "";
    public string BlockerMessage { get; set; } = "";
    public string StackFamily { get; set; } = "";
    public string PhraseFamily { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public List<string> CandidateTemplateIds { get; set; } = [];
    public List<string> EvidenceLines { get; set; } = [];
    public List<string> AllowedMissingPieceCategories { get; set; } = [];
    public List<string> AllowedNextActionCategories { get; set; } = [];
}

public sealed class ForensicsAgentResponsePayload
{
    public string Explanation { get; set; } = "";
    public string MissingPieceCategory { get; set; } = "";
    public string RecommendedNextActionCategory { get; set; } = "";
}

public sealed class ForensicsAgentPresentationResult
{
    public bool Accepted { get; set; }
    public bool FallbackUsed { get; set; }
    public bool Skipped { get; set; }
    public string SkipReason { get; set; } = "";
    public string TraceId { get; set; } = "";
    public AgentInvocationResult Invocation { get; set; } = new();
    public ForensicsAgentResponsePayload Payload { get; set; } = new();
}
