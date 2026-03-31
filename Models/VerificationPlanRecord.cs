namespace RAM.Models;

public sealed class VerificationPlanRecord
{
    public string VerificationPlanId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string ModificationIntent { get; set; } = "";
    public string SourcePatchDraftId { get; set; } = "";
    public string SourceRepairProposalId { get; set; } = "";
    public string FailureKind { get; set; } = "unknown";
    public string BuildSystemType { get; set; } = "";
    public string VerificationTool { get; set; } = "unknown";
    public string TargetPath { get; set; } = "";
    public string TargetSurfaceType { get; set; } = "";
    public List<string> TargetFiles { get; set; } = [];
    public string Rationale { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string Filter { get; set; } = "";
    public string SafetyPolicySummary { get; set; } = "";
    public string SourcePatchContractId { get; set; } = "";
    public string SourcePatchPlanId { get; set; } = "";
    public string WarningPolicyMode { get; set; } = "track_only";
    public List<string> ValidationRequirements { get; set; } = [];
    public List<string> RerunRequirements { get; set; } = [];
}
