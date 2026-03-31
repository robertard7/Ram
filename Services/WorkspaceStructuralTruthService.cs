using RAM.Models;

namespace RAM.Services;

public sealed class WorkspaceStructuralTruthService
{
    private readonly TaskboardArtifactStore _artifactStore = new();
    private readonly WorkspaceSnapshotService _workspaceSnapshotService = new();
    private readonly WorkspaceProjectInventoryService _workspaceProjectInventoryService = new();

    public (WorkspaceSnapshotRecord Snapshot, WorkspaceProjectGraphRecord ProjectGraph) CaptureAndPersist(
        string workspaceRoot,
        RamDbService ramDbService)
    {
        var snapshot = _workspaceSnapshotService.Capture(workspaceRoot);
        var projectGraph = _workspaceProjectInventoryService.Build(workspaceRoot, snapshot);

        _artifactStore.SaveWorkspaceSnapshotArtifact(ramDbService, workspaceRoot, snapshot);
        _artifactStore.SaveWorkspaceProjectGraphArtifact(ramDbService, workspaceRoot, projectGraph);

        return (snapshot, projectGraph);
    }
}
