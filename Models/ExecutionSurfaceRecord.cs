namespace RAM.Models;

public sealed class ExecutionSurfaceRecord
{
    public string FilePath { get; set; } = "";
    public string MethodName { get; set; } = "";
    public string TriggerSource { get; set; } = "";
    public ExecutionSurfaceTrustLevel TrustLevel { get; set; } = ExecutionSurfaceTrustLevel.Unknown;
    public bool ReachableFromPostWriteFlows { get; set; }
    public string Notes { get; set; } = "";
}
