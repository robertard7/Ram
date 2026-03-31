namespace RAM.Models;

public sealed class TaskboardRuntimeStateAssessment
{
    public bool HasSnapshot { get; set; }
    public bool IsCompatible { get; set; }
    public string StatusCode { get; set; } = "";
    public string Summary { get; set; } = "";
    public string InvalidationReason { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string CurrentFingerprint { get; set; } = "";
    public string CachedVersion { get; set; } = "";
    public string CachedFingerprint { get; set; } = "";
}
