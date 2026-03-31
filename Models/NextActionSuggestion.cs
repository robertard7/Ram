namespace RAM.Models;

public sealed class NextActionSuggestion
{
    public string Title { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string SuggestedPrompt { get; set; } = "";
    public string Reason { get; set; } = "";
}
