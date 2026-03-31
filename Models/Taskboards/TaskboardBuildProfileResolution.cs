namespace RAM.Models;

public enum TaskboardStackFamily
{
    Unknown,
    DotnetDesktop,
    NativeCppDesktop,
    WebApp,
    RustApp
}

public enum TaskboardBuildProfileResolutionStatus
{
    Unknown,
    Resolved,
    Conflict
}

public enum TaskboardBuildProfileConfidence
{
    None,
    Low,
    Medium,
    High
}

public enum TaskboardBuildProfileEvidenceSource
{
    Unknown,
    TaskboardIntent,
    WorkspaceEvidence,
    ActiveArtifactEvidence,
    AdvisoryAgent
}

public sealed class TaskboardBuildProfileEvidenceRecord
{
    public TaskboardBuildProfileEvidenceSource Source { get; set; } = TaskboardBuildProfileEvidenceSource.Unknown;
    public string Code { get; set; } = "";
    public string Value { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class TaskboardBuildProfileResolutionRecord
{
    public string ResolutionId { get; set; } = "";
    public TaskboardBuildProfileResolutionStatus Status { get; set; } = TaskboardBuildProfileResolutionStatus.Unknown;
    public TaskboardStackFamily StackFamily { get; set; } = TaskboardStackFamily.Unknown;
    public string Language { get; set; } = "";
    public string Framework { get; set; } = "";
    public string UiShellKind { get; set; } = "";
    public TaskboardBuildProfileConfidence Confidence { get; set; } = TaskboardBuildProfileConfidence.None;
    public string ResolutionReason { get; set; } = "";
    public List<TaskboardBuildProfileEvidenceRecord> SourceEvidence { get; set; } = [];
    public List<string> MissingEvidence { get; set; } = [];
    public bool AdvisoryUsed { get; set; }
    public string AdvisoryTraceId { get; set; } = "";
}
