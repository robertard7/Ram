namespace RAM.Models;

public sealed class ExecutionGateDecision
{
    public string DecisionId { get; set; } = Guid.NewGuid().ToString("N");
    public bool IsAllowed { get; set; }
    public ExecutionSourceType SourceType { get; set; } = ExecutionSourceType.Unknown;
    public string SourceName { get; set; } = "";
    public string CommandFamily { get; set; } = "";
    public string BuildFamily { get; set; } = "";
    public string PolicyMode { get; set; } = "";
    public string ScopeRiskClassification { get; set; } = "";
    public bool IsAutomaticTrigger { get; set; }
    public string BlockedReason { get; set; } = "";
    public string Summary { get; set; } = "";
}
