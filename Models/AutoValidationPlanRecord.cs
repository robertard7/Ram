namespace RAM.Models;

public sealed class AutoValidationPlanRecord
{
    public string PlanId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public long SourceArtifactId { get; set; }
    public string SourceArtifactType { get; set; } = "";
    public string SourceActionType { get; set; } = "unknown";
    public List<string> ChangedFilePaths { get; set; } = [];
    public string BuildFamily { get; set; } = "";
    public string PolicyMode { get; set; } = "";
    public string SelectedValidationTool { get; set; } = "";
    public string SelectedTargetPath { get; set; } = "";
    public string ValidationReason { get; set; } = "";
    public string SafetySummary { get; set; } = "";
    public string ScopeRiskClassification { get; set; } = "";
    public bool ExecutionAllowed { get; set; }
    public string BlockedReason { get; set; } = "";
    public string RecommendedNextStep { get; set; } = "";
}
