namespace RAM.Models;

public enum TaskboardWorkItemDecompositionDisposition
{
    NotApplicable,
    Decomposed,
    Covered,
    Blocked
}

public sealed class TaskboardDecomposedWorkItem
{
    public string SubItemId { get; set; } = "";
    public string ParentWorkItemId { get; set; } = "";
    public int Ordinal { get; set; }
    public string DisplayOrdinal { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string TargetStack { get; set; } = "";
    public string WorkFamily { get; set; } = "";
    public string Description { get; set; } = "";
    public string PromptText { get; set; } = "";
    public string Summary { get; set; } = "";
    public string ExpectedArtifact { get; set; } = "";
    public string ValidationHint { get; set; } = "";
    public string PhraseFamily { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public List<string> TemplateCandidateIds { get; set; } = [];
    public List<string> RequiredInputs { get; set; } = [];
    public ToolRequest? ToolRequest { get; set; }
}

public sealed class TaskboardWorkItemDecompositionRecord
{
    public string DecompositionId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string PlanImportId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string OriginalWorkItemId { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public string PhraseFamily { get; set; } = "";
    public string PhraseFamilyConfidence { get; set; } = "";
    public string PhraseFamilyTraceId { get; set; } = "";
    public string PhraseFamilySource { get; set; } = "";
    public string PhraseFamilyResolutionSummary { get; set; } = "";
    public List<string> PhraseFamilyCandidates { get; set; } = [];
    public string PhraseFamilyDeterministicCandidate { get; set; } = "";
    public string PhraseFamilyAdvisoryCandidate { get; set; } = "";
    public string PhraseFamilyBlockerCode { get; set; } = "";
    public string PhraseFamilyTieBreakRuleId { get; set; } = "";
    public string PhraseFamilyTieBreakSummary { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public List<string> TemplateCandidateIds { get; set; } = [];
    public string TemplateSelectionTraceId { get; set; } = "";
    public string TemplateSelectionReason { get; set; } = "";
    public TaskboardWorkItemDecompositionDisposition Disposition { get; set; } = TaskboardWorkItemDecompositionDisposition.NotApplicable;
    public string Reason { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public TaskboardBuildProfileResolutionRecord BuildProfile { get; set; } = new();
    public TaskboardPhraseFamilyResolutionRecord PhraseFamilyResolution { get; set; } = new();
    public List<TaskboardDecomposedWorkItem> SubItems { get; set; } = [];
}
