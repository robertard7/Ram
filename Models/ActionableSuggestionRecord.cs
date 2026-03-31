namespace RAM.Models;

public sealed class ActionableSuggestionRecord
{
    public string SuggestionId { get; set; } = "";
    public string SourceChainId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string SuggestionKind { get; set; } = "tool";
    public string Title { get; set; } = "";
    public string PromptText { get; set; } = "";
    public string TargetToolName { get; set; } = "";
    public string TargetChainTemplate { get; set; } = "";
    public string ShortRationale { get; set; } = "";
    public SuggestionReadiness Readiness { get; set; } = SuggestionReadiness.InformationalOnly;
    public string BlockedReason { get; set; } = "";
    public bool ManualOnly { get; set; }
    public int Priority { get; set; }
}
