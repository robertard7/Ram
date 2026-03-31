namespace RAM.Models;

public sealed class AutoValidationResultRecord
{
    public string ResultId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string PlanId { get; set; } = "";
    public long SourceArtifactId { get; set; }
    public string SourceArtifactType { get; set; } = "";
    public string SourceActionType { get; set; } = "unknown";
    public string BuildFamily { get; set; } = "";
    public List<string> ChangedFilePaths { get; set; } = [];
    public string ExecutedTool { get; set; } = "";
    public string ResolvedTarget { get; set; } = "";
    public string OutcomeClassification { get; set; } = "not_applicable";
    public string Summary { get; set; } = "";
    public bool ExecutionAttempted { get; set; }
    public string LinkedOutcomeType { get; set; } = "";
    public List<string> TopFailures { get; set; } = [];
    public string SafetyTrigger { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string SuggestedNextStep { get; set; } = "";
}
