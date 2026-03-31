namespace RAM.Models;

public sealed class WorkspaceBuildProfileRecord
{
    public string WorkspaceRoot { get; set; } = "";
    public BuildSystemType BuildSystemType { get; set; } = BuildSystemType.Unknown;
    public string PrimaryTargetPath { get; set; } = "";
    public string ConfigureToolFamily { get; set; } = "";
    public string BuildToolFamily { get; set; } = "";
    public string TestToolFamily { get; set; } = "";
    public string ConfigureTargetPath { get; set; } = "";
    public string BuildTargetPath { get; set; } = "";
    public string TestTargetPath { get; set; } = "";
    public string BuildDirectoryPath { get; set; } = "";
    public string Confidence { get; set; } = "";
    public bool PreferredProfile { get; set; }
    public List<string> DetectionSignals { get; set; } = [];
}
