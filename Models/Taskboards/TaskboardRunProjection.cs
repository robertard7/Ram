namespace RAM.Models;

public sealed class TaskboardRunProjection
{
    public string RunId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string Scope { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string ActivePlanImportId { get; set; } = "";
    public string ActivePlanTitle { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string BatchTitle { get; set; } = "";
    public int BatchNumber { get; set; }
    public bool ExecutionStarted { get; set; }
    public string BuildProfileSummary { get; set; } = "";
    public string DecompositionSummary { get; set; } = "";
    public string ExecutionLaneSummary { get; set; } = "";
    public string ExecutionLaneBlockerSummary { get; set; } = "";
    public string ExecutionGoalSummary { get; set; } = "";
    public string ExecutionGoalBlockerSummary { get; set; } = "";
    public string WorkFamilySummary { get; set; } = "";
    public string LaneCoverageSummary { get; set; } = "";
    public List<TaskboardRunWorkItem> WorkItems { get; set; } = [];
}

public sealed class TaskboardRunWorkItem
{
    public string WorkItemId { get; set; } = "";
    public int Ordinal { get; set; }
    public string DisplayOrdinal { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string PromptText { get; set; } = "";
    public bool IsDecomposedItem { get; set; }
    public string SourceWorkItemId { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string TargetStack { get; set; } = "";
    public string WorkFamily { get; set; } = "";
    public string ExpectedArtifact { get; set; } = "";
    public string ValidationHint { get; set; } = "";
    public string PhraseFamily { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public List<string> TemplateCandidateIds { get; set; } = [];
    public ToolRequest? DirectToolRequest { get; set; }
}
