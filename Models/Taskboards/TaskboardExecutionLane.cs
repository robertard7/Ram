namespace RAM.Models;

public enum TaskboardExecutionLaneKind
{
    Unknown,
    ToolLane,
    ChainLane,
    ManualOnlyLane,
    BlockedLane
}

public enum TaskboardExecutionLaneBlockerCode
{
    None,
    EmptyPrompt,
    NoLaneCandidates,
    UnsupportedRuntimeCoverage,
    MissingToolLaneForOperationKind,
    MissingChainLaneForPhraseFamily,
    MissingTemplateSelection,
    MissingRequiredArgumentForLane,
    UnsupportedStackLaneMapping,
    AmbiguousLaneCandidates,
    UnresolvedWorkspaceTargetForLane,
    UnknownToolLaneTarget,
    InvalidResponseModeForLane,
    MissingGroupedShellLane,
    MissingUiWiringLane,
    MissingAppStateLane,
    MissingViewmodelScaffoldLane,
    MissingStorageBootstrapLane,
    MissingRepositoryScaffoldLane,
    MissingCheckRunnerLane,
    MissingBuildVerifyLane,
    MissingBuildRepairLane,
    MissingNativeLaneMapping,
    ManualOnlyBoundary,
    UnsafeBlocked
}

public sealed class TaskboardExecutionLaneCandidate
{
    public string CandidateId { get; set; } = "";
    public TaskboardExecutionLaneKind LaneKind { get; set; } = TaskboardExecutionLaneKind.Unknown;
    public string ToolId { get; set; } = "";
    public string ChainTemplateId { get; set; } = "";
    public string Source { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool IsViable { get; set; } = true;
    public string CoverageStatus { get; set; } = "";
    public string CoverageSummary { get; set; } = "";
}

public sealed class TaskboardExecutionLaneEvidence
{
    public string Code { get; set; } = "";
    public string Value { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class TaskboardExecutionLaneBlocker
{
    public TaskboardExecutionLaneBlockerCode Code { get; set; } = TaskboardExecutionLaneBlockerCode.None;
    public string Message { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class TaskboardExecutionLaneResolution
{
    public string ResolutionId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string SourceWorkItemId { get; set; } = "";
    public string SourceWorkItemTitle { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string TargetStack { get; set; } = "";
    public string WorkFamily { get; set; } = "";
    public string WorkFamilySource { get; set; } = "";
    public List<string> WorkFamilyCandidates { get; set; } = [];
    public string PhraseFamily { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public List<string> TemplateCandidateIds { get; set; } = [];
    public string PromptText { get; set; } = "";
    public TaskboardExecutionLaneKind LaneKind { get; set; } = TaskboardExecutionLaneKind.Unknown;
    public string SelectedToolId { get; set; } = "";
    public string SelectedChainTemplateId { get; set; } = "";
    public Dictionary<string, string> BoundedArguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ResolutionReason { get; set; } = "";
    public string ResolvedTargetPath { get; set; } = "";
    public string SelectionPath { get; set; } = "";
    public TaskboardExecutionEligibilityKind Eligibility { get; set; } = TaskboardExecutionEligibilityKind.Unknown;
    public BuilderRequestKind RequestKind { get; set; } = BuilderRequestKind.NormalQuestion;
    public ResponseMode ResponseMode { get; set; } = ResponseMode.None;
    public string CanonicalOperationKind { get; set; } = "";
    public string CanonicalTargetPath { get; set; } = "";
    public string CanonicalizationTrace { get; set; } = "";
    public FileIdentityRecord ResolvedTargetIdentity { get; set; } = new();
    public string CreatedUtc { get; set; } = "";
    public List<TaskboardExecutionLaneCandidate> Candidates { get; set; } = [];
    public TaskboardExecutionLaneBlocker Blocker { get; set; } = new();
    public List<TaskboardExecutionLaneEvidence> Evidence { get; set; } = [];
}
