using System.IO;
using System.Text;
using RAM.Models;

namespace RAM.Services;

public sealed class WorkspaceRetrievalLiveSyncService
{
    public const string SyncExecutionVersion = "workspace_live_sync.v1";

    private readonly OllamaClient _ollamaClient = new();
    private readonly QdrantService _qdrantService = new();
    private readonly ModelRoleConfigurationService _modelRoleConfigurationService = new();

    public WorkspaceRetrievalSyncResultRecord Execute(
        string workspaceRoot,
        WorkspaceRetrievalCatalogRecord catalog,
        WorkspaceRetrievalDeltaRecord delta,
        WorkspaceRetrievalSyncResultRecord? priorSyncResult,
        AppSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(settings);

        _modelRoleConfigurationService.Normalize(settings);

        var collectionName = ResolveWorkspaceCollectionName(settings.QdrantCollection);
        var attemptedUtc = DateTime.UtcNow.ToString("O");
        var workspaceId = catalog.WorkspaceId;
        var truthFingerprint = catalog.TruthFingerprint;
        var fullResyncRequired = RequiresFullResync(priorSyncResult, truthFingerprint, collectionName);

        if (!IsConfigured(settings))
        {
            return new WorkspaceRetrievalSyncResultRecord
            {
                SyncResultId = Guid.NewGuid().ToString("N"),
                WorkspaceId = workspaceId,
                WorkspaceRoot = workspaceRoot,
                SnapshotId = catalog.SnapshotId,
                TruthFingerprint = truthFingerprint,
                CatalogId = catalog.CatalogId,
                DeltaId = delta.DeltaId,
                ExecutionMode = "disabled",
                SyncStatus = "backend_not_configured",
                BackendType = FirstNonEmpty(settings.EmbedderBackend, "qdrant"),
                EmbedderModel = settings.EmbedderModel,
                CollectionName = collectionName,
                AttemptedUtc = attemptedUtc,
                PlannedSkipCount = catalog.ChunkCount,
                AppliedSkipCount = catalog.ChunkCount,
                Evidence =
                [
                    Evidence("sync_execution_version", SyncExecutionVersion, "Stage 0.8.2 executes live sync only when the embedder and Qdrant backend are configured."),
                    Evidence("workspace_id", workspaceId, "Workspace chunk sync remains partitioned by active workspace id."),
                    Evidence("collection_name", collectionName, "Workspace chunk sync uses a dedicated retrieval collection derived from the configured base collection.")
                ]
            };
        }

        var chunksToUpsert = ResolveUpsertChunks(catalog, delta, fullResyncRequired);
        var pointIdsToDelete = delta.Items
            .Where(item => string.Equals(item.DeltaState, "removed", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.WorkspacePointId)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var plannedSkipCount = Math.Max(catalog.ChunkCount - chunksToUpsert.Count, 0);

        var result = new WorkspaceRetrievalSyncResultRecord
        {
            SyncResultId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            WorkspaceRoot = workspaceRoot,
            SnapshotId = catalog.SnapshotId,
            TruthFingerprint = truthFingerprint,
            CatalogId = catalog.CatalogId,
            DeltaId = delta.DeltaId,
            ExecutionMode = "live_sync",
            SyncStatus = "sync_failed",
            BackendType = FirstNonEmpty(settings.EmbedderBackend, "qdrant"),
            EmbedderModel = settings.EmbedderModel,
            CollectionName = collectionName,
            AttemptedUtc = attemptedUtc,
            FullResyncApplied = fullResyncRequired,
            PlannedUpsertCount = chunksToUpsert.Count,
            PlannedDeleteCount = pointIdsToDelete.Count,
            PlannedSkipCount = plannedSkipCount,
            PlannedPointIds = chunksToUpsert.Select(chunk => chunk.WorkspacePointId).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            PlannedRemovedPointIds = pointIdsToDelete,
            AppliedSkipCount = plannedSkipCount
        };

        try
        {
            if (chunksToUpsert.Count > 0)
            {
                var embeddingInputs = chunksToUpsert.Select(chunk => BuildEmbeddingText(workspaceRoot, catalog, chunk)).ToList();
                var embeddings = _ollamaClient.GenerateEmbeddingsAsync(
                    settings.Endpoint,
                    settings.EmbedderModel,
                    embeddingInputs).GetAwaiter().GetResult();

                if (embeddings.Count != chunksToUpsert.Count || embeddings.Count == 0)
                    throw new InvalidOperationException("Embedder returned an unexpected number of workspace chunk vectors.");

                _qdrantService.EnsureCollectionAsync(
                    settings.QdrantEndpoint,
                    collectionName,
                    embeddings[0].Count).GetAwaiter().GetResult();

                var points = chunksToUpsert
                    .Zip(embeddings, (chunk, vector) => BuildPoint(workspaceRoot, catalog, chunk, vector))
                    .ToList();
                _qdrantService.UpsertAsync(
                    settings.QdrantEndpoint,
                    collectionName,
                    points).GetAwaiter().GetResult();
                result.AppliedUpsertCount = points.Count;
                result.AppliedPointIds = points.Select(point => point.Id).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
            }

            if (pointIdsToDelete.Count > 0)
            {
                _qdrantService.DeleteAsync(
                    settings.QdrantEndpoint,
                    collectionName,
                    pointIdsToDelete).GetAwaiter().GetResult();
                result.AppliedDeleteCount = pointIdsToDelete.Count;
                result.DeletedPointIds = [.. pointIdsToDelete];
            }

            result.SyncStatus = "sync_current";
            result.LastSuccessfulSyncUtc = attemptedUtc;
        }
        catch (Exception ex)
        {
            result.FailureSummary = $"{ex.GetType().Name}: {ex.Message}";
            result.FailedPointIds = DetermineFailedPointIds(result);
            result.FailedPointCount = result.FailedPointIds.Count;
            result.SyncStatus = (result.AppliedUpsertCount > 0 || result.AppliedDeleteCount > 0)
                ? "sync_partial"
                : fullResyncRequired
                    ? "sync_stale"
                    : "sync_failed";
        }

        result.Evidence =
        [
            Evidence("sync_execution_version", SyncExecutionVersion, "Stage 0.8.2 performs bounded live Qdrant sync for active workspace chunks."),
            Evidence("workspace_id", workspaceId, "All synced points are partitioned by the active workspace id."),
            Evidence("collection_name", collectionName, "Workspace chunk sync uses a dedicated retrieval collection derived from the configured base collection."),
            Evidence("full_resync", result.FullResyncApplied ? "true" : "false", result.FullResyncApplied
                ? "Current workspace chunks were fully re-upserted because prior sync evidence was missing or stale for this truth fingerprint."
                : "Sync used only the incremental retrieval delta for this workspace."),
            Evidence("sync_status", result.SyncStatus, string.IsNullOrWhiteSpace(result.FailureSummary)
                ? "Live sync completed without Qdrant mutation failures."
                : result.FailureSummary)
        ];

        return result;
    }

    public static string ResolveWorkspaceCollectionName(string configuredCollectionName)
    {
        var normalized = FirstNonEmpty(configuredCollectionName, "ram");
        return normalized.EndsWith("_workspace", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"{normalized}_workspace";
    }

    private static bool IsConfigured(AppSettings settings)
    {
        return string.Equals(settings.EmbedderBackend, "qdrant", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(settings.Endpoint)
            && !string.IsNullOrWhiteSpace(settings.EmbedderModel)
            && !string.IsNullOrWhiteSpace(settings.QdrantEndpoint)
            && !string.IsNullOrWhiteSpace(settings.QdrantCollection);
    }

    private static bool RequiresFullResync(
        WorkspaceRetrievalSyncResultRecord? priorSyncResult,
        string truthFingerprint,
        string collectionName)
    {
        if (priorSyncResult is null)
            return true;

        if (!string.Equals(priorSyncResult.SyncStatus, "sync_current", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(priorSyncResult.TruthFingerprint, truthFingerprint, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(priorSyncResult.CollectionName, collectionName, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static List<WorkspaceRetrievalChunkRecord> ResolveUpsertChunks(
        WorkspaceRetrievalCatalogRecord catalog,
        WorkspaceRetrievalDeltaRecord delta,
        bool fullResyncRequired)
    {
        if (fullResyncRequired)
        {
            return catalog.Chunks
                .OrderBy(chunk => chunk.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(chunk => chunk.ChunkOrder)
                .ToList();
        }

        var upsertChunkKeys = new HashSet<string>(
            delta.Items
                .Where(item =>
                    string.Equals(item.DeltaState, "added", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.DeltaState, "changed", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.ChunkKey),
            StringComparer.OrdinalIgnoreCase);

        return catalog.Chunks
            .Where(chunk => upsertChunkKeys.Contains(chunk.ChunkKey))
            .OrderBy(chunk => chunk.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(chunk => chunk.ChunkOrder)
            .ToList();
    }

    private static QdrantPointRecord BuildPoint(
        string workspaceRoot,
        WorkspaceRetrievalCatalogRecord catalog,
        WorkspaceRetrievalChunkRecord chunk,
        IReadOnlyList<float> vector)
    {
        return new QdrantPointRecord
        {
            Id = chunk.WorkspacePointId,
            Vector = vector.ToList(),
            Payload = new Dictionary<string, object?>
            {
                ["workspace_id"] = catalog.WorkspaceId,
                ["workspace_root"] = workspaceRoot,
                ["source_kind"] = "workspace_chunk",
                ["retrieval_scope"] = "workspace_preparation",
                ["snapshot_id"] = catalog.SnapshotId,
                ["truth_fingerprint"] = catalog.TruthFingerprint,
                ["relative_path"] = chunk.RelativePath,
                ["project_path"] = chunk.ProjectPath,
                ["project_name"] = chunk.ProjectName,
                ["solution_paths"] = chunk.SolutionPaths,
                ["chunk_key"] = chunk.ChunkKey,
                ["chunk_type"] = chunk.ChunkType,
                ["file_kind"] = chunk.FileKind,
                ["role"] = chunk.Role,
                ["language"] = chunk.LanguageHint,
                ["pattern_tags"] = chunk.PatternTags,
                ["content_sha256"] = chunk.ContentSha256,
                ["chunk_order"] = chunk.ChunkOrder,
                ["start_line"] = chunk.StartLine,
                ["end_line"] = chunk.EndLine,
                ["text"] = BuildEmbeddingText(workspaceRoot, catalog, chunk)
            }
        };
    }

    private static string BuildEmbeddingText(
        string workspaceRoot,
        WorkspaceRetrievalCatalogRecord catalog,
        WorkspaceRetrievalChunkRecord chunk)
    {
        var fullPath = Path.Combine(
            workspaceRoot,
            chunk.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var fileText = File.Exists(fullPath)
            ? File.ReadAllText(fullPath)
            : "";

        var builder = new StringBuilder();
        builder.AppendLine($"workspace_id={catalog.WorkspaceId}");
        builder.AppendLine($"truth_fingerprint={catalog.TruthFingerprint}");
        builder.AppendLine($"relative_path={chunk.RelativePath}");
        builder.AppendLine($"project_name={chunk.ProjectName}");
        builder.AppendLine($"project_path={chunk.ProjectPath}");
        builder.AppendLine($"chunk_type={chunk.ChunkType}");
        builder.AppendLine($"file_kind={chunk.FileKind}");
        builder.AppendLine($"role={chunk.Role}");
        builder.AppendLine($"language={chunk.LanguageHint}");
        builder.AppendLine($"pattern_tags={string.Join(",", chunk.PatternTags)}");
        builder.AppendLine($"content_sha256={chunk.ContentSha256}");
        builder.AppendLine();
        builder.Append(fileText);
        return builder.ToString();
    }

    private static List<string> DetermineFailedPointIds(WorkspaceRetrievalSyncResultRecord result)
    {
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pointId in result.PlannedPointIds)
        {
            if (!result.AppliedPointIds.Contains(pointId, StringComparer.OrdinalIgnoreCase))
                failed.Add(pointId);
        }

        foreach (var pointId in result.PlannedRemovedPointIds)
        {
            if (!result.DeletedPointIds.Contains(pointId, StringComparer.OrdinalIgnoreCase))
                failed.Add(pointId);
        }

        return failed.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static WorkspaceRetrievalEvidenceRecord Evidence(string code, string value, string detail)
    {
        return new WorkspaceRetrievalEvidenceRecord
        {
            Code = code,
            Value = value,
            Detail = detail
        };
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
    }
}
