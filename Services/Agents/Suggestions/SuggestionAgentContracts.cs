using RAM.Models;

namespace RAM.Services;

public sealed class SuggestionAgentCandidate
{
    public string SuggestionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string PromptText { get; set; } = "";
    public string Readiness { get; set; } = "";
    public string BlockedReason { get; set; } = "";
    public bool ManualOnly { get; set; }
}

public sealed class SuggestionAgentRequestPayload
{
    public string ChainId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public List<SuggestionAgentCandidate> Candidates { get; set; } = [];
}

public sealed class SuggestionAgentDisplayGroup
{
    public string Title { get; set; } = "";
    public List<string> SuggestionIds { get; set; } = [];
}

public sealed class SuggestionAgentResponsePayload
{
    public List<string> OrderedSuggestionIds { get; set; } = [];
    public List<SuggestionAgentDisplayGroup> DisplayGroups { get; set; } = [];
    public List<string> PresentationNotes { get; set; } = [];
}

public sealed class SuggestionPresentationGroup
{
    public string Title { get; set; } = "";
    public List<ActionableSuggestionRecord> Suggestions { get; set; } = [];
}

public sealed class SuggestionPresentationResult
{
    public bool Accepted { get; set; }
    public bool FallbackUsed { get; set; }
    public bool Skipped { get; set; }
    public string SkipReason { get; set; } = "";
    public string TraceId { get; set; } = "";
    public AgentInvocationResult Invocation { get; set; } = new();
    public List<SuggestionPresentationGroup> Groups { get; set; } = [];
    public List<string> PresentationNotes { get; set; } = [];
}
