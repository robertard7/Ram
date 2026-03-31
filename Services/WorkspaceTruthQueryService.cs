using RAM.Models;

namespace RAM.Services;

public sealed class WorkspaceTruthQueryService
{
    private readonly TaskboardArtifactStore _artifactStore = new();

    public WorkspaceSnapshotRecord? LoadLatestSnapshot(RamDbService ramDbService, string workspaceRoot)
    {
        return _artifactStore.LoadWorkspaceSnapshot(ramDbService, workspaceRoot);
    }

    public WorkspaceProjectGraphRecord? LoadLatestProjectGraph(RamDbService ramDbService, string workspaceRoot)
    {
        return _artifactStore.LoadWorkspaceProjectGraph(ramDbService, workspaceRoot);
    }

    public WorkspaceProjectRecord? GetProjectByPathOrName(RamDbService ramDbService, string workspaceRoot, string projectPathOrName)
    {
        var graph = LoadLatestProjectGraph(ramDbService, workspaceRoot);
        if (graph is null || string.IsNullOrWhiteSpace(projectPathOrName))
            return null;

        var normalized = NormalizePath(projectPathOrName);
        var looseKey = ToLooseKey(projectPathOrName);
        return graph.Projects.FirstOrDefault(project =>
            string.Equals(project.RelativePath, normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(project.ProjectName, normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ToLooseKey(project.ProjectName), looseKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ToLooseKey(project.RelativePath), looseKey, StringComparison.OrdinalIgnoreCase));
    }

    public WorkspaceFileRecord? GetFileClassification(RamDbService ramDbService, string workspaceRoot, string relativePath)
    {
        var snapshot = LoadLatestSnapshot(ramDbService, workspaceRoot);
        if (snapshot is null || string.IsNullOrWhiteSpace(relativePath))
            return null;

        var normalized = NormalizePath(relativePath);
        return snapshot.Files.FirstOrDefault(file =>
            string.Equals(file.RelativePath, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<WorkspaceFileRecord> GetFilesOwnedByProject(RamDbService ramDbService, string workspaceRoot, string projectPathOrName)
    {
        var snapshot = LoadLatestSnapshot(ramDbService, workspaceRoot);
        var project = GetProjectByPathOrName(ramDbService, workspaceRoot, projectPathOrName);
        if (snapshot is null || project is null)
            return [];

        return snapshot.Files
            .Where(file => string.Equals(file.OwningProjectPath, project.RelativePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizePath(string value)
    {
        return (value ?? "").Replace('\\', '/').Trim().Trim('/');
    }

    private static string ToLooseKey(string value)
    {
        return new string((value ?? "")
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }
}
