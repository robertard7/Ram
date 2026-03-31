namespace RAM.Models;

public sealed class BuildScopeAssessmentRecord
{
    public string WorkspaceRoot { get; set; } = "";
    public BuildSystemType BuildFamily { get; set; } = BuildSystemType.Unknown;
    public string RequestedCommandType { get; set; } = "";
    public string ResolvedTargetPath { get; set; } = "";
    public string TargetKind { get; set; } = "";
    public BuildScopeRiskLevel RiskLevel { get; set; } = BuildScopeRiskLevel.BlockedUnknown;
    public string Reason { get; set; } = "";
    public string RecommendedSaferAlternative { get; set; } = "";
    public string RecommendedToolName { get; set; } = "";
    public bool LiveExecutionAllowed { get; set; }
    public bool ExplicitPathProvided { get; set; }
    public bool ExplicitTargetProvided { get; set; }
}
