using RAM.Models;

namespace RAM.Services;

public sealed class WorkspacePreparationQueryService
{
    private readonly TaskboardArtifactStore _artifactStore = new();

    public WorkspacePreparationStateRecord? LoadLatestPreparationState(RamDbService ramDbService, string workspaceRoot)
    {
        return _artifactStore.LoadWorkspacePreparationState(ramDbService, workspaceRoot);
    }

    public WorkspaceRetrievalCatalogRecord? LoadLatestRetrievalCatalog(RamDbService ramDbService, string workspaceRoot)
    {
        return _artifactStore.LoadWorkspaceRetrievalCatalog(ramDbService, workspaceRoot);
    }

    public WorkspaceRetrievalDeltaRecord? LoadLatestRetrievalDelta(RamDbService ramDbService, string workspaceRoot)
    {
        return _artifactStore.LoadWorkspaceRetrievalDelta(ramDbService, workspaceRoot);
    }

    public WorkspaceRetrievalSyncResultRecord? LoadLatestRetrievalSyncResult(RamDbService ramDbService, string workspaceRoot)
    {
        return _artifactStore.LoadWorkspaceRetrievalSyncResult(ramDbService, workspaceRoot);
    }

    public string GetRetrievalReadinessStatus(RamDbService ramDbService, string workspaceRoot)
    {
        return LoadLatestRetrievalSyncResult(ramDbService, workspaceRoot)?.SyncStatus ?? "";
    }

    public bool IsRetrievalCurrent(RamDbService ramDbService, string workspaceRoot)
    {
        var state = LoadLatestPreparationState(ramDbService, workspaceRoot);
        var sync = LoadLatestRetrievalSyncResult(ramDbService, workspaceRoot);
        return state is not null
            && sync is not null
            && string.Equals(sync.SyncStatus, "sync_current", StringComparison.OrdinalIgnoreCase)
            && string.Equals(sync.TruthFingerprint, state.TruthFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    public string GetLastSuccessfulSyncUtc(RamDbService ramDbService, string workspaceRoot)
    {
        return LoadLatestPreparationState(ramDbService, workspaceRoot)?.LastSuccessfulSyncUtc ?? "";
    }

    public int GetIndexedChunkCount(RamDbService ramDbService, string workspaceRoot)
    {
        return LoadLatestRetrievalCatalog(ramDbService, workspaceRoot)?.ChunkCount ?? 0;
    }

    public IReadOnlyList<WorkspaceRetrievalChunkRecord> GetChunksForFile(RamDbService ramDbService, string workspaceRoot, string relativePath)
    {
        var catalog = LoadLatestRetrievalCatalog(ramDbService, workspaceRoot);
        if (catalog is null || string.IsNullOrWhiteSpace(relativePath))
            return [];

        var normalizedPath = Normalize(relativePath);
        return catalog.Chunks
            .Where(chunk => string.Equals(chunk.RelativePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(chunk => chunk.ChunkOrder)
            .ToList();
    }

    public IReadOnlyList<WorkspaceRetrievalChunkRecord> GetChunksForProject(RamDbService ramDbService, string workspaceRoot, string projectPathOrName)
    {
        var catalog = LoadLatestRetrievalCatalog(ramDbService, workspaceRoot);
        if (catalog is null || string.IsNullOrWhiteSpace(projectPathOrName))
            return [];

        var normalized = Normalize(projectPathOrName);
        return catalog.Chunks
            .Where(chunk =>
                string.Equals(chunk.ProjectPath, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(chunk.ProjectName, normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(chunk => chunk.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(chunk => chunk.ChunkOrder)
            .ToList();
    }

    private static string Normalize(string value)
    {
        return (value ?? "").Replace('\\', '/').Trim().Trim('/');
    }
}
