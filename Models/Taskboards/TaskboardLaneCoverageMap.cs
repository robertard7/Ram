namespace RAM.Models;

public sealed class TaskboardLaneCoverageEntry
{
    public string WorkItemId { get; set; } = "";
    public string WorkItemTitle { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string BatchTitle { get; set; } = "";
    public string WorkFamily { get; set; } = "";
    public string WorkFamilySource { get; set; } = "";
    public string PhraseFamily { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string StackFamily { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public TaskboardExecutionLaneKind ExpectedLaneKind { get; set; } = TaskboardExecutionLaneKind.Unknown;
    public string SelectedToolId { get; set; } = "";
    public string SelectedChainTemplateId { get; set; } = "";
    public List<string> CandidateLaneIds { get; set; } = [];
    public string Status { get; set; } = "";
    public string Summary { get; set; } = "";
    public string BlockerCode { get; set; } = "";
    public string BlockerMessage { get; set; } = "";
    public bool IsCurrent { get; set; }
    public bool IsNext { get; set; }
}

public sealed class TaskboardLaneCoverageMap
{
    public string MapId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string PlanImportId { get; set; } = "";
    public string PlanTitle { get; set; } = "";
    public string RuntimeStateVersion { get; set; } = "";
    public string RuntimeStateFingerprint { get; set; } = "";
    public string RuntimeStateStatusCode { get; set; } = "";
    public string CurrentBatchId { get; set; } = "";
    public string CurrentWorkItemId { get; set; } = "";
    public string CurrentWorkFamily { get; set; } = "";
    public string NextWorkFamily { get; set; } = "";
    public string Summary { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public List<TaskboardLaneCoverageEntry> Entries { get; set; } = [];
}
