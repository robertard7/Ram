namespace RAM.Models;

public sealed class ToolChainSummaryInput
{
    public string ChainId { get; set; } = "";
    public ResponseMode ResponseMode { get; set; } = ResponseMode.None;
    public ToolChainType ChainType { get; set; } = ToolChainType.None;
    public string TemplateName { get; set; } = "";
    public string UserGoal { get; set; } = "";
    public string InitiatingUserPrompt { get; set; } = "";
    public ToolChainStatus Status { get; set; } = ToolChainStatus.Planned;
    public ToolChainStopReason StopReason { get; set; } = ToolChainStopReason.Unknown;
    public string FinalOutcomeSummary { get; set; } = "";
    public string SuggestedNextAction { get; set; } = "";
    public bool ExecutionOccurred { get; set; }
    public bool ExecutionBlocked { get; set; }
    public List<ActionableSuggestionRecord> ActionableSuggestions { get; set; } = [];
    public List<ToolChainStepRecord> Steps { get; set; } = [];
}
