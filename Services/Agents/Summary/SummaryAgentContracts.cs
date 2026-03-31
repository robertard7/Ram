namespace RAM.Services;

public sealed class SummaryAgentStepFact
{
    public int StepIndex { get; set; }
    public string ToolName { get; set; } = "";
    public string ResultClassification { get; set; } = "";
    public string ResultSummary { get; set; } = "";
    public bool ExecutionAttempted { get; set; }
    public string ExecutionBlockedReason { get; set; } = "";
}

public sealed class SummaryAgentRequestPayload
{
    public string ChainId { get; set; } = "";
    public string ChainType { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string UserGoal { get; set; } = "";
    public string Status { get; set; } = "";
    public string StopReason { get; set; } = "";
    public string FinalOutcomeSummary { get; set; } = "";
    public bool ExecutionOccurred { get; set; }
    public bool ExecutionBlocked { get; set; }
    public int ReadySuggestionCount { get; set; }
    public int BlockedSuggestionCount { get; set; }
    public int ManualOnlySuggestionCount { get; set; }
    public List<SummaryAgentStepFact> Steps { get; set; } = [];
}

public sealed class SummaryAgentResponsePayload
{
    public string SummaryTitle { get; set; } = "";
    public string StatusLine { get; set; } = "";
    public List<string> SummaryLines { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class SummaryPresentationResult
{
    public bool Accepted { get; set; }
    public bool FallbackUsed { get; set; }
    public bool Skipped { get; set; }
    public string SkipReason { get; set; } = "";
    public string TraceId { get; set; } = "";
    public AgentInvocationResult Invocation { get; set; } = new();
    public SummaryAgentResponsePayload Payload { get; set; } = new();
}
