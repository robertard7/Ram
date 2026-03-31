namespace RAM.Models;

public sealed class VerificationOutcomeRecord
{
    public string VerificationOutcomeId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string VerificationPlanId { get; set; } = "";
    public string SourcePatchDraftId { get; set; } = "";
    public string SourceRepairProposalId { get; set; } = "";
    public string ExecutedTool { get; set; } = "";
    public string ResolvedTarget { get; set; } = "";
    public string OutcomeClassification { get; set; } = "not_verifiable";
    public string BeforeSummary { get; set; } = "";
    public string AfterSummary { get; set; } = "";
    public int? BeforeFailureCount { get; set; }
    public int? AfterFailureCount { get; set; }
    public int? FailureCountDelta { get; set; }
    public int? BeforeWarningCount { get; set; }
    public int? AfterWarningCount { get; set; }
    public int? WarningCountDelta { get; set; }
    public List<string> WarningCodes { get; set; } = [];
    public string WarningPolicyMode { get; set; } = "track_only";
    public string PatchContractId { get; set; } = "";
    public string PatchPlanId { get; set; } = "";
    public string MutationFamily { get; set; } = "";
    public string AllowedEditScope { get; set; } = "";
    public List<string> TopRemainingFailures { get; set; } = [];
    public string Explanation { get; set; } = "";
}
