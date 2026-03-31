namespace RAM.Models;

public sealed class ToolChainStepRecord
{
    public string ChainId { get; set; } = "";
    public int StepIndex { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string ChainStepId { get; set; } = "";
    public string PreviousChainStepId { get; set; } = "";
    public List<string> AllowedNextStepIds { get; set; } = [];
    public string ChainValidationBlockerCode { get; set; } = "";
    public string ChainMismatchOrigin { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string ToolArgumentsSummary { get; set; } = "";
    public bool AllowedByPolicy { get; set; }
    public string ResultClassification { get; set; } = "";
    public string ResultSummary { get; set; } = "";
    public bool ExecutionAttempted { get; set; }
    public string ExecutionBlockedReason { get; set; } = "";
    public bool MutationObserved { get; set; }
    public List<string> TouchedFilePaths { get; set; } = [];
    public List<string> LinkedArtifactPaths { get; set; } = [];
    public string StructuredDataJson { get; set; } = "";
}
