namespace RAM.Models;

public sealed class BuildDetectionSignalRecord
{
    public BuildSystemType BuildSystemType { get; set; } = BuildSystemType.Unknown;
    public string RelativePath { get; set; } = "";
    public string SignalType { get; set; } = "";
    public string Description { get; set; } = "";
    public int Depth { get; set; }
    public bool IsRootSignal { get; set; }
}
