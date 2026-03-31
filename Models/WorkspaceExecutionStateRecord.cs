namespace RAM.Models;

public sealed class WorkspaceExecutionStateRecord
{
    public string WorkspaceRoot { get; set; } = "";

    public string LastFailureToolName { get; set; } = "";
    public string LastFailureOutcomeType { get; set; } = "";
    public string LastFailureTargetPath { get; set; } = "";
    public string LastFailureSummary { get; set; } = "";
    public string LastFailureDataJson { get; set; } = "";
    public string LastFailureUtc { get; set; } = "";

    public string LastSuccessToolName { get; set; } = "";
    public string LastSuccessOutcomeType { get; set; } = "";
    public string LastSuccessTargetPath { get; set; } = "";
    public string LastSuccessSummary { get; set; } = "";
    public string LastSuccessDataJson { get; set; } = "";
    public string LastSuccessUtc { get; set; } = "";

    public string LastVerificationPlanId { get; set; } = "";
    public string LastVerifiedPatchDraftId { get; set; } = "";
    public string LastVerificationToolName { get; set; } = "";
    public string LastVerificationOutcomeType { get; set; } = "";
    public string LastVerificationTargetPath { get; set; } = "";
    public string LastVerificationSummary { get; set; } = "";
    public string LastVerificationDataJson { get; set; } = "";
    public string LastVerificationUtc { get; set; } = "";

    public string LastDetectedBuildSystemType { get; set; } = "";
    public string LastSelectedBuildProfileType { get; set; } = "";
    public string LastSelectedBuildProfileTargetPath { get; set; } = "";
    public string LastSelectedBuildProfileJson { get; set; } = "";
    public string LastConfigureToolName { get; set; } = "";
    public string LastBuildToolFamily { get; set; } = "";
    public string LastVerificationFamily { get; set; } = "";
}
