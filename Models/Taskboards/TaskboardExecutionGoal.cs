namespace RAM.Models;

public enum TaskboardExecutionGoalKind
{
    Unknown,
    ToolGoal,
    ChainGoal,
    ManualOnlyGoal,
    BlockedGoal
}

public enum TaskboardExecutionGoalBlockerCode
{
    None,
    EmptyPrompt,
    RequiresElevation,
    SystemMutationManualOnly,
    AmbiguousManualOnly,
    DestructiveOutsideWorkspace,
    MissingToolMapping,
    MissingChainTemplate,
    MissingRequiredArgument,
    UnsupportedExecutionCoverage,
    UnsupportedStackForOperation,
    MissingPhraseFamily,
    MissingTemplateResolution,
    UnresolvedWorkspaceTarget,
    UnknownTool,
    NoDeterministicLane,
    InvalidResponseMode
}

public sealed class TaskboardExecutionGoalEvidence
{
    public string Code { get; set; } = "";
    public string Value { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class TaskboardExecutionGoal
{
    public string SourceWorkItemId { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string TargetStack { get; set; } = "";
    public string WorkFamily { get; set; } = "";
    public string WorkFamilySource { get; set; } = "";
    public TaskboardExecutionGoalKind GoalKind { get; set; } = TaskboardExecutionGoalKind.Unknown;
    public string SelectedToolId { get; set; } = "";
    public string SelectedChainTemplateId { get; set; } = "";
    public string PhraseFamily { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public List<string> TemplateCandidateIds { get; set; } = [];
    public string SelectionPath { get; set; } = "";
    public Dictionary<string, string> BoundedArguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ResolutionReason { get; set; } = "";
    public string ResolvedTargetPath { get; set; } = "";
    public string ExpectedValidationHint { get; set; } = "";
    public List<TaskboardExecutionGoalEvidence> ExpectedEvidence { get; set; } = [];
}

public sealed class TaskboardExecutionGoalBlocker
{
    public TaskboardExecutionGoalBlockerCode Code { get; set; } = TaskboardExecutionGoalBlockerCode.None;
    public string Message { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class TaskboardExecutionGoalResolution
{
    public string ResolutionId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string SourceWorkItemId { get; set; } = "";
    public string SourceWorkItemTitle { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string TargetStack { get; set; } = "";
    public string WorkFamily { get; set; } = "";
    public string WorkFamilySource { get; set; } = "";
    public TaskboardExecutionGoalKind GoalKind { get; set; } = TaskboardExecutionGoalKind.Unknown;
    public string PromptText { get; set; } = "";
    public string ResolutionReason { get; set; } = "";
    public string ResolvedTargetPath { get; set; } = "";
    public string PhraseFamily { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public List<string> TemplateCandidateIds { get; set; } = [];
    public string SelectionPath { get; set; } = "";
    public string ForensicsExplanation { get; set; } = "";
    public TaskboardExecutionEligibilityKind Eligibility { get; set; } = TaskboardExecutionEligibilityKind.Unknown;
    public BuilderRequestKind RequestKind { get; set; } = BuilderRequestKind.NormalQuestion;
    public ResponseMode ResponseMode { get; set; } = ResponseMode.None;
    public string CreatedUtc { get; set; } = "";
    public TaskboardExecutionGoal Goal { get; set; } = new();
    public TaskboardExecutionGoalBlocker Blocker { get; set; } = new();
    public List<TaskboardExecutionGoalEvidence> Evidence { get; set; } = [];
    public TaskboardExecutionLaneResolution LaneResolution { get; set; } = new();
}
