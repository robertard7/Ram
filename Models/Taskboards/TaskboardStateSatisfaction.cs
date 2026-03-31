namespace RAM.Models;

public sealed class TaskboardStateSatisfactionResultRecord
{
    public string WorkItemId { get; set; } = "";
    public string StepId { get; set; } = "";
    public string CheckFamily { get; set; } = "";
    public bool Satisfied { get; set; }
    public bool SkipAllowed { get; set; }
    public string TrustSource { get; set; } = "";
    public string ReasonCode { get; set; } = "";
    public string EvidenceSummary { get; set; } = "";
    public string InvalidationReasonCode { get; set; } = "";
    public bool UsedFileTouchFastPath { get; set; }
    public int RepeatedTouchesAvoidedCount { get; set; }
    public List<string> CheckedFilePaths { get; set; } = [];
    public List<long> LinkedArtifactIds { get; set; } = [];
}

public sealed class TaskboardSkipDecisionRecord
{
    public long Id { get; set; }
    public string WorkspaceRoot { get; set; } = "";
    public string RunStateId { get; set; } = "";
    public string PlanImportId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string WorkItemTitle { get; set; } = "";
    public string StepId { get; set; } = "";
    public string SkipFamily { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string ReasonCode { get; set; } = "";
    public string EvidenceSource { get; set; } = "";
    public string EvidenceSummary { get; set; } = "";
    public bool UsedFileTouchFastPath { get; set; }
    public int RepeatedTouchesAvoidedCount { get; set; }
    public List<string> LinkedFilePaths { get; set; } = [];
    public List<long> LinkedArtifactIds { get; set; } = [];
    public string CreatedUtc { get; set; } = "";
}

public sealed class TaskboardSkipReasonRollupRecord
{
    public string ReasonCode { get; set; } = "";
    public int Count { get; set; }
    public int RepeatedTouchesAvoidedCount { get; set; }
    public List<string> EvidenceSources { get; set; } = [];
}
