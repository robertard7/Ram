namespace RAM.Models;

public sealed class BuildSystemDetectionResult
{
    public string WorkspaceRoot { get; set; } = "";
    public BuildSystemType DetectedType { get; set; } = BuildSystemType.Unknown;
    public string Confidence { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<BuildDetectionSignalRecord> Signals { get; set; } = [];
    public List<WorkspaceBuildProfileRecord> Profiles { get; set; } = [];
    public WorkspaceBuildProfileRecord? PreferredProfile { get; set; }
}
