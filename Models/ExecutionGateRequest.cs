namespace RAM.Models;

public sealed class ExecutionGateRequest
{
    public ExecutionSourceType SourceType { get; set; } = ExecutionSourceType.Unknown;
    public string SourceName { get; set; } = "";
    public string CommandFamily { get; set; } = "";
    public string BuildFamily { get; set; } = "";
    public string PolicyMode { get; set; } = "";
    public string ScopeRiskClassification { get; set; } = "";
    public bool IsAutomaticTrigger { get; set; }
    public bool ExecutionAllowed { get; set; }
    public string TargetPath { get; set; } = "";
    public string Reason { get; set; } = "";
}
