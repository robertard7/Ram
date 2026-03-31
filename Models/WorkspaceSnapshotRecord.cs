namespace RAM.Models;

public sealed class WorkspaceSnapshotRecord
{
    public string SnapshotVersion { get; set; } = "workspace_snapshot.v1";
    public string SnapshotId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string ScannerVersion { get; set; } = "";
    public string ExclusionPolicyVersion { get; set; } = "";
    public List<string> ExcludedDirectoryNames { get; set; } = [];
    public int FileCount { get; set; }
    public int SolutionCount { get; set; }
    public int ProjectCount { get; set; }
    public List<string> SolutionPaths { get; set; } = [];
    public List<string> ProjectPaths { get; set; } = [];
    public List<WorkspaceFileRecord> Files { get; set; } = [];
}
