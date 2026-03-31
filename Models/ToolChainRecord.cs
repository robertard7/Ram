namespace RAM.Models;

public sealed class ToolChainRecord
{
    public string ChainId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string CompletedUtc { get; set; } = "";
    public string InitiatingUserPrompt { get; set; } = "";
    public ResponseMode ResponseMode { get; set; } = ResponseMode.None;
    public ToolChainType ChainType { get; set; } = ToolChainType.None;
    public string ChainGoal { get; set; } = "";
    public string SelectedTemplateName { get; set; } = "";
    public ToolChainStatus CurrentStatus { get; set; } = ToolChainStatus.Planned;
    public ToolChainStopReason StopReason { get; set; } = ToolChainStopReason.Unknown;
    public string FinalOutcomeSummary { get; set; } = "";
    public bool ModelSummaryRequested { get; set; }
    public bool ModelSummaryUsed { get; set; }
    public bool FallbackSummaryUsed { get; set; }
    public string ModelSummaryRejectionReason { get; set; } = "";
    public string LastAcceptedChainStepId { get; set; } = "";
    public string LastAttemptedChainStepId { get; set; } = "";
    public List<string> LastAllowedNextStepIds { get; set; } = [];
    public string LastChainValidationBlockerCode { get; set; } = "";
    public string LastChainMismatchOrigin { get; set; } = "";
    public string LastChainValidationSummary { get; set; } = "";
    public string SuggestedNextAction { get; set; } = "";
    public List<ActionableSuggestionRecord> ActionableSuggestions { get; set; } = [];
    public List<ToolChainStepRecord> Steps { get; set; } = [];
}
