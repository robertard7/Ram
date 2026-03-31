namespace RAM.Models;

public enum TaskboardPhraseFamilyResolutionSource
{
    Unknown,
    OperationKind,
    CommandCanonicalization,
    DeterministicFallback,
    AdvisoryAgent
}

public enum TaskboardPhraseFamilyBlockerCode
{
    None,
    NotBroadBuilderPhrase,
    NoDeterministicRule,
    DeterministicRuleConflict,
    TieBreakRuleNotApplicable,
    PhraseFamilyTieUnresolved,
    AdvisoryUnavailable,
    AdvisoryRejected,
    PhraseFamilyUnresolved
}

public sealed class TaskboardPhraseFamilyResolutionRecord
{
    public string ResolutionId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string PlanImportId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string WorkItemTitle { get; set; } = "";
    public bool ShouldDecompose { get; set; }
    public bool IsBlocked { get; set; }
    public string RawPhraseText { get; set; } = "";
    public string NormalizedPhraseText { get; set; } = "";
    public string NormalizationSummary { get; set; } = "";
    public string CanonicalOperationKind { get; set; } = "";
    public string CanonicalTargetPath { get; set; } = "";
    public string CanonicalProjectName { get; set; } = "";
    public string CanonicalTemplateHint { get; set; } = "";
    public string CanonicalRoleHint { get; set; } = "";
    public string CanonicalizationTrace { get; set; } = "";
    public string ClosestKnownFamilyGroup { get; set; } = "";
    public string ResolutionPathTrace { get; set; } = "";
    public string TerminalResolverStage { get; set; } = "";
    public string BuilderOperationResolutionStatus { get; set; } = "";
    public string LaneResolutionStatus { get; set; } = "";
    public string PhraseFamily { get; set; } = "";
    public string Confidence { get; set; } = "";
    public TaskboardPhraseFamilyResolutionSource ResolutionSource { get; set; } = TaskboardPhraseFamilyResolutionSource.Unknown;
    public string ResolutionSummary { get; set; } = "";
    public List<string> CandidatePhraseFamilies { get; set; } = [];
    public string DeterministicCandidate { get; set; } = "";
    public string DeterministicConfidence { get; set; } = "";
    public string DeterministicReason { get; set; } = "";
    public string TieBreakRuleId { get; set; } = "";
    public string TieBreakSummary { get; set; } = "";
    public bool AdvisoryAttempted { get; set; }
    public bool AdvisoryAccepted { get; set; }
    public string AdvisoryPhraseFamily { get; set; } = "";
    public string AdvisoryConfidence { get; set; } = "";
    public string AdvisoryTraceId { get; set; } = "";
    public string AdvisoryStatus { get; set; } = "";
    public TaskboardPhraseFamilyBlockerCode BlockerCode { get; set; } = TaskboardPhraseFamilyBlockerCode.None;
    public string BlockerMessage { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}
