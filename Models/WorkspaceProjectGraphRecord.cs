namespace RAM.Models;

public sealed class WorkspaceProjectGraphRecord
{
    public string GraphVersion { get; set; } = "workspace_project_graph.v1";
    public string GraphId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string InventoryVersion { get; set; } = "";
    public string SnapshotId { get; set; } = "";
    public int SolutionCount { get; set; }
    public int ProjectCount { get; set; }
    public int ReferenceCount { get; set; }
    public List<WorkspaceSolutionRecord> Solutions { get; set; } = [];
    public List<WorkspaceProjectRecord> Projects { get; set; } = [];
    public List<WorkspaceReferenceRecord> References { get; set; } = [];
}
