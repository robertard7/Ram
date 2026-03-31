namespace RAM.Models;

public enum TaskboardExecutionEligibilityKind
{
    Unknown,
    WorkspaceBuildSafe,
    WorkspaceEditSafe,
    WorkspaceTestSafe,
    ManualOnlyElevated,
    ManualOnlySystemMutation,
    ManualOnlyAmbiguous,
    BlockedUnsafe
}

public sealed class TaskboardBuilderOperationResolutionResult
{
    public bool Matched { get; set; }
    public TaskboardExecutionEligibilityKind Eligibility { get; set; } = TaskboardExecutionEligibilityKind.Unknown;
    public string ToolName { get; set; } = "";
    public Dictionary<string, string> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Reason { get; set; } = "";
    public string ResolvedTargetPath { get; set; } = "";
    public string CanonicalOperationKind { get; set; } = "";
    public string CanonicalTargetPath { get; set; } = "";
    public string CanonicalizationTrace { get; set; } = "";
}
