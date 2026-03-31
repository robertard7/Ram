using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class WorkspacePreparationService
{
    public const string WorkspaceIdentityVersion = "workspace_identity.v1";
    public const string ChunkPlannerVersion = "workspace_chunk_planner.v1";
    public const string SyncPlannerVersion = "workspace_sync_planner.v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly ModelRoleConfigurationService _modelRoleConfigurationService = new();
    private readonly TaskboardArtifactStore _artifactStore = new();
    private readonly WorkspaceRetrievalLiveSyncService _workspaceRetrievalLiveSyncService = new();
    private readonly WorkspaceStructuralTruthService _workspaceStructuralTruthService = new();

    public WorkspacePreparationStateRecord Prepare(
        string workspaceRoot,
        RamDbService ramDbService,
        AppSettings? settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(ramDbService);

        var stopwatch = Stopwatch.StartNew();
        var normalizedWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        var normalizedSettings = settings is null
            ? new AppSettings()
            : CloneSettings(settings);
        _modelRoleConfigurationService.Normalize(normalizedSettings);

        var priorCatalog = _artifactStore.LoadWorkspaceRetrievalCatalog(ramDbService, normalizedWorkspaceRoot);
        var priorSyncResult = _artifactStore.LoadWorkspaceRetrievalSyncResult(ramDbService, normalizedWorkspaceRoot);
        var structuralTruth = LoadStructuralTruth(normalizedWorkspaceRoot, ramDbService);
        var snapshot = structuralTruth.Snapshot;
        var projectGraph = structuralTruth.ProjectGraph;
        var workspaceId = BuildDeterministicId(WorkspaceIdentityVersion, normalizedWorkspaceRoot.ToLowerInvariant());
        var truthFingerprint = BuildTruthFingerprint(snapshot, projectGraph);

        var catalog = BuildCatalog(normalizedWorkspaceRoot, workspaceId, truthFingerprint, snapshot, projectGraph);
        var delta = BuildDelta(normalizedWorkspaceRoot, workspaceId, truthFingerprint, snapshot, priorCatalog, catalog);
        var syncResult = _workspaceRetrievalLiveSyncService.Execute(
            normalizedWorkspaceRoot,
            catalog,
            delta,
            priorSyncResult,
            normalizedSettings);
        var persistenceResults = new List<ArtifactPersistenceResult>
        {
            PersistWorkspaceArtifact(
                normalizedWorkspaceRoot,
                TaskboardArtifactStore.BuildWorkspaceRetrievalCatalogPath(),
                catalog,
                () => _artifactStore.SaveWorkspaceRetrievalCatalogArtifact(ramDbService, normalizedWorkspaceRoot, catalog),
                "workspace_retrieval_catalog"),
            PersistWorkspaceArtifact(
                normalizedWorkspaceRoot,
                TaskboardArtifactStore.BuildWorkspaceRetrievalDeltaPath(),
                delta,
                () => _artifactStore.SaveWorkspaceRetrievalDeltaArtifact(ramDbService, normalizedWorkspaceRoot, delta),
                "workspace_retrieval_delta"),
            PersistWorkspaceArtifact(
                normalizedWorkspaceRoot,
                TaskboardArtifactStore.BuildWorkspaceRetrievalSyncResultPath(),
                syncResult,
                () => _artifactStore.SaveWorkspaceRetrievalSyncResultArtifact(ramDbService, normalizedWorkspaceRoot, syncResult),
                "workspace_retrieval_sync_result")
        };

        var state = BuildState(
            normalizedWorkspaceRoot,
            workspaceId,
            truthFingerprint,
            snapshot,
            catalog,
            delta,
            syncResult,
            normalizedSettings,
            persistenceResults,
            structuralTruth.Evidence,
            structuralTruth.UsedFallback,
            stopwatch.ElapsedMilliseconds);

        var stateFilePersistence = PersistWorkspaceArtifactFileOnly(
            normalizedWorkspaceRoot,
            TaskboardArtifactStore.BuildWorkspacePreparationStatePath(),
            state,
            "workspace_preparation_state");
        persistenceResults.Add(stateFilePersistence);

        if (!stateFilePersistence.FileSucceeded)
        {
            state = BuildState(
                normalizedWorkspaceRoot,
                workspaceId,
                truthFingerprint,
                snapshot,
                catalog,
                delta,
                syncResult,
                normalizedSettings,
                persistenceResults,
                structuralTruth.Evidence,
                structuralTruth.UsedFallback,
                stopwatch.ElapsedMilliseconds);
        }

        var stateDatabasePersistence = PersistWorkspaceArtifactDatabaseOnly(
            () => _artifactStore.SaveWorkspacePreparationStateArtifact(ramDbService, normalizedWorkspaceRoot, state),
            "workspace_preparation_state");

        if (!stateDatabasePersistence.DatabaseSucceeded)
        {
            stateFilePersistence.DatabaseSucceeded = false;
            stateFilePersistence.DatabaseFailureSummary = stateDatabasePersistence.DatabaseFailureSummary;
            state = BuildState(
                normalizedWorkspaceRoot,
                workspaceId,
                truthFingerprint,
                snapshot,
                catalog,
                delta,
                syncResult,
                normalizedSettings,
                persistenceResults,
                structuralTruth.Evidence,
                structuralTruth.UsedFallback,
                stopwatch.ElapsedMilliseconds);
            PersistWorkspaceArtifactFileOnly(
                normalizedWorkspaceRoot,
                TaskboardArtifactStore.BuildWorkspacePreparationStatePath(),
                state,
                "workspace_preparation_state");
        }

        return state;
    }

    private WorkspaceRetrievalCatalogRecord BuildCatalog(
        string workspaceRoot,
        string workspaceId,
        string truthFingerprint,
        WorkspaceSnapshotRecord snapshot,
        WorkspaceProjectGraphRecord projectGraph)
    {
        var projectByPath = projectGraph.Projects.ToDictionary(project => project.RelativePath, StringComparer.OrdinalIgnoreCase);
        var chunks = new List<WorkspaceRetrievalChunkRecord>();
        var skippedFiles = 0;

        foreach (var file in snapshot.Files.OrderBy(current => current.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!ShouldIndexFile(file))
            {
                skippedFiles++;
                continue;
            }

            var projectPath = ResolveProjectPath(file, projectByPath);
            projectByPath.TryGetValue(projectPath, out var projectRecord);
            var chunkType = ResolveChunkType(file);
            var lineCount = ReadLineCount(workspaceRoot, file.RelativePath);
            var chunkKey = BuildDeterministicId(
                "workspace_chunk_key.v1",
                workspaceId,
                file.RelativePath,
                chunkType,
                "0");
            var pointId = BuildDeterministicId("workspace_point_id.v1", workspaceId, chunkKey);

            chunks.Add(new WorkspaceRetrievalChunkRecord
            {
                ChunkKey = chunkKey,
                WorkspaceId = workspaceId,
                WorkspacePointId = pointId,
                SnapshotId = snapshot.SnapshotId,
                RelativePath = file.RelativePath,
                ProjectPath = projectPath,
                ProjectName = FirstNonEmpty(projectRecord?.ProjectName ?? "", file.Identity.ProjectName ?? ""),
                SolutionPaths = projectRecord?.SolutionPaths?.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList() ?? [],
                FileKind = file.FileKind,
                ChunkType = chunkType,
                Role = FirstNonEmpty(file.Identity.Role ?? "", InferRoleFromFileKind(file.FileKind)),
                LanguageHint = file.LanguageHint,
                PatternTags = BuildPatternTags(file),
                ContentSha256 = file.ContentSha256,
                ByteCount = file.SizeBytes,
                LineCount = lineCount,
                ChunkOrder = 0,
                StartLine = lineCount > 0 ? 1 : 0,
                EndLine = lineCount,
                Evidence =
                [
                    Evidence("snapshot_id", snapshot.SnapshotId, "Chunk source was derived from the authoritative Stage 0 workspace snapshot."),
                    Evidence("file_kind", file.FileKind, "Chunk planning used the Stage 0 file-kind classification."),
                    Evidence("project_path", DisplayValue(projectPath), "Chunk ownership came from the Stage 0 project inventory."),
                    Evidence("role", DisplayValue(FirstNonEmpty(file.Identity.Role ?? "", InferRoleFromFileKind(file.FileKind))), file.Identity.IdentityTrace),
                    Evidence("content_sha256", DisplayValue(file.ContentSha256), "Chunk freshness tracks the Stage 0 file hash.")
                ]
            });
        }

        return new WorkspaceRetrievalCatalogRecord
        {
            CatalogId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            WorkspaceRoot = workspaceRoot,
            SnapshotId = snapshot.SnapshotId,
            TruthFingerprint = truthFingerprint,
            ChunkPlannerVersion = ChunkPlannerVersion,
            ChunkCount = chunks.Count,
            IndexedFileCount = chunks.Select(chunk => chunk.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            SkippedFileCount = skippedFiles,
            Chunks = chunks
                .OrderBy(chunk => chunk.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(chunk => chunk.ChunkOrder)
                .ToList(),
            Evidence =
            [
                Evidence("planner_version", ChunkPlannerVersion, "Workspace retrieval catalog used the current deterministic chunk planner."),
                Evidence("chunking_scope", "whole_file", "Stage 0.8.1 indexes `.sln`, `.csproj`, `.cs`, and `.xaml` as bounded whole-file chunks."),
                Evidence("truth_fingerprint", truthFingerprint, "Catalog scope is tied directly to the current Stage 0 truth fingerprint.")
            ]
        };
    }

    private WorkspaceRetrievalDeltaRecord BuildDelta(
        string workspaceRoot,
        string workspaceId,
        string truthFingerprint,
        WorkspaceSnapshotRecord snapshot,
        WorkspaceRetrievalCatalogRecord? priorCatalog,
        WorkspaceRetrievalCatalogRecord currentCatalog)
    {
        var items = new List<WorkspaceRetrievalDeltaItemRecord>();
        var previousChunks = (priorCatalog?.Chunks ?? [])
            .ToDictionary(chunk => chunk.ChunkKey, StringComparer.OrdinalIgnoreCase);
        var currentChunks = currentCatalog.Chunks
            .ToDictionary(chunk => chunk.ChunkKey, StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in currentCatalog.Chunks)
        {
            if (!previousChunks.TryGetValue(chunk.ChunkKey, out var previous))
            {
                items.Add(new WorkspaceRetrievalDeltaItemRecord
                {
                    ChunkKey = chunk.ChunkKey,
                    WorkspacePointId = chunk.WorkspacePointId,
                    RelativePath = chunk.RelativePath,
                    DeltaState = "added",
                    CurrentContentSha256 = chunk.ContentSha256,
                    Reason = "Chunk did not exist in the prior retrieval catalog.",
                    Evidence = [Evidence("delta_state", "added", "The chunk key is new for the active workspace catalog.")]
                });
                continue;
            }

            var unchanged = string.Equals(previous.ContentSha256, chunk.ContentSha256, StringComparison.OrdinalIgnoreCase);
            items.Add(new WorkspaceRetrievalDeltaItemRecord
            {
                ChunkKey = chunk.ChunkKey,
                WorkspacePointId = chunk.WorkspacePointId,
                RelativePath = chunk.RelativePath,
                DeltaState = unchanged ? "unchanged" : "changed",
                PreviousContentSha256 = previous.ContentSha256,
                CurrentContentSha256 = chunk.ContentSha256,
                Reason = unchanged
                    ? "Chunk content hash matches the prior retrieval catalog."
                    : "Chunk content hash changed since the prior retrieval catalog.",
                Evidence =
                [
                    Evidence("delta_state", unchanged ? "unchanged" : "changed", "Chunk comparison used the stable chunk key plus Stage 0 content hash."),
                    Evidence("previous_content_sha256", DisplayValue(previous.ContentSha256), "Prior retrieval catalog content hash."),
                    Evidence("current_content_sha256", DisplayValue(chunk.ContentSha256), "Current retrieval catalog content hash.")
                ]
            });
        }

        foreach (var previous in previousChunks.Values.OrderBy(chunk => chunk.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (currentChunks.ContainsKey(previous.ChunkKey))
                continue;

            items.Add(new WorkspaceRetrievalDeltaItemRecord
            {
                ChunkKey = previous.ChunkKey,
                WorkspacePointId = previous.WorkspacePointId,
                RelativePath = previous.RelativePath,
                DeltaState = "removed",
                PreviousContentSha256 = previous.ContentSha256,
                Reason = "Chunk no longer exists in the current retrieval catalog.",
                Evidence = [Evidence("delta_state", "removed", "The prior chunk key was not present in the current catalog.")]
            });
        }

        var addedCount = items.Count(item => string.Equals(item.DeltaState, "added", StringComparison.OrdinalIgnoreCase));
        var changedCount = items.Count(item => string.Equals(item.DeltaState, "changed", StringComparison.OrdinalIgnoreCase));
        var removedCount = items.Count(item => string.Equals(item.DeltaState, "removed", StringComparison.OrdinalIgnoreCase));
        var unchangedCount = items.Count(item => string.Equals(item.DeltaState, "unchanged", StringComparison.OrdinalIgnoreCase));

        return new WorkspaceRetrievalDeltaRecord
        {
            DeltaId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            WorkspaceRoot = workspaceRoot,
            SnapshotId = snapshot.SnapshotId,
            TruthFingerprint = truthFingerprint,
            PreviousCatalogId = priorCatalog?.CatalogId ?? "",
            CurrentCatalogId = currentCatalog.CatalogId,
            AddedCount = addedCount,
            ChangedCount = changedCount,
            RemovedCount = removedCount,
            UnchangedCount = unchangedCount,
            SkippedCount = currentCatalog.SkippedFileCount,
            Items = items
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.DeltaState, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Evidence =
            [
                Evidence("truth_fingerprint", truthFingerprint, "Delta comparison is anchored to the current Stage 0 truth fingerprint."),
                Evidence("previous_catalog_id", DisplayValue(priorCatalog?.CatalogId ?? ""), "Compared against the last saved retrieval catalog for this workspace."),
                Evidence("current_catalog_id", currentCatalog.CatalogId, "Current retrieval catalog baseline for sync planning.")
            ]
        };
    }

    private WorkspacePreparationStateRecord BuildState(
        string workspaceRoot,
        string workspaceId,
        string truthFingerprint,
        WorkspaceSnapshotRecord snapshot,
        WorkspaceRetrievalCatalogRecord catalog,
        WorkspaceRetrievalDeltaRecord delta,
        WorkspaceRetrievalSyncResultRecord syncResult,
        AppSettings settings,
        IReadOnlyList<ArtifactPersistenceResult> persistenceResults,
        IReadOnlyList<WorkspaceRetrievalEvidenceRecord> structuralTruthEvidence,
        bool usedStructuralTruthFallback,
        long durationMs)
    {
        var filePersistenceStatus = EvaluateFilePersistenceStatus(persistenceResults);
        var databasePersistenceStatus = EvaluateDatabasePersistenceStatus(persistenceResults);
        var persistenceStatus = EvaluateOverallPersistenceStatus(filePersistenceStatus, databasePersistenceStatus);
        var preparationStatus = persistenceStatus switch
        {
            "failed" => "failed",
            "partial" => "prepared_partial",
            _ when usedStructuralTruthFallback => "prepared_stale",
            _ => "prepared_current"
        };
        var failedItemCount = persistenceResults.Count(result => !result.FileSucceeded || !result.DatabaseSucceeded);
        var persistenceEvidence = BuildPersistenceEvidence(persistenceResults);

        return new WorkspacePreparationStateRecord
        {
            PreparationId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            WorkspaceRoot = workspaceRoot,
            SnapshotId = snapshot.SnapshotId,
            TruthFingerprint = truthFingerprint,
            CatalogId = catalog.CatalogId,
            DeltaId = delta.DeltaId,
            SyncResultId = syncResult.SyncResultId,
            LastPreparedUtc = DateTime.UtcNow.ToString("O"),
            LastSuccessfulSyncUtc = syncResult.LastSuccessfulSyncUtc,
            EmbedderBackend = FirstNonEmpty(settings.EmbedderBackend, "qdrant"),
            EmbedderModel = settings.EmbedderModel,
            QdrantEndpoint = settings.QdrantEndpoint,
            QdrantCollection = syncResult.CollectionName,
            PreparationStatus = preparationStatus,
            PersistenceStatus = persistenceStatus,
            DatabasePersistenceStatus = databasePersistenceStatus,
            ArtifactFilePersistenceStatus = filePersistenceStatus,
            SyncMode = syncResult.ExecutionMode,
            SyncStatus = syncResult.SyncStatus,
            PreparationDurationMs = durationMs,
            ChunkCount = catalog.ChunkCount,
            IndexedFileCount = catalog.IndexedFileCount,
            ChangedFileCount = delta.AddedCount + delta.ChangedCount,
            RemovedFileCount = delta.RemovedCount,
            FailedItemCount = failedItemCount,
            Evidence = new[]
            {
                Evidence("snapshot_id", snapshot.SnapshotId, "Preparation state is anchored to the latest authoritative Stage 0 snapshot."),
                Evidence("truth_fingerprint", truthFingerprint, "Preparation state matches the current workspace truth fingerprint."),
                Evidence("sync_mode", syncResult.ExecutionMode, string.Equals(syncResult.ExecutionMode, "live_sync", StringComparison.OrdinalIgnoreCase)
                    ? "Stage 0.8.2 performs bounded live Qdrant sync during workspace preparation."
                    : "Workspace preparation did not execute live sync for this workspace."),
                Evidence("indexed_files", catalog.IndexedFileCount.ToString(), "Only deterministic retrieval-safe file kinds were cataloged."),
                Evidence("persistence_status", persistenceStatus, "Preparation status reflects both file artifacts and database persistence."),
                Evidence("structural_truth_source", usedStructuralTruthFallback ? "artifact_fallback" : "fresh_capture", usedStructuralTruthFallback
                    ? "Preparation used the last saved Stage 0 snapshot/project graph because fresh capture was unavailable."
                    : "Preparation used a fresh Stage 0 capture for the active workspace."),
                Evidence("preparation_duration_ms", durationMs.ToString(), "Preparation duration was recorded for later bounded performance tuning.")
            }
                .Concat(structuralTruthEvidence)
                .Concat(persistenceEvidence)
                .ToList()
        };
    }

    private static string ResolveProjectPath(
        WorkspaceFileRecord file,
        IReadOnlyDictionary<string, WorkspaceProjectRecord> projectByPath)
    {
        if (!string.IsNullOrWhiteSpace(file.OwningProjectPath))
            return file.OwningProjectPath;

        if (string.Equals(file.FileKind, "project", StringComparison.OrdinalIgnoreCase)
            && projectByPath.ContainsKey(file.RelativePath))
        {
            return file.RelativePath;
        }

        return "";
    }

    private static bool ShouldIndexFile(WorkspaceFileRecord file)
    {
        return file.FileKind switch
        {
            "solution" or "project" or "source" or "test" or "xaml" => true,
            _ => false
        };
    }

    private static string ResolveChunkType(WorkspaceFileRecord file)
    {
        return file.FileKind switch
        {
            "solution" => "solution_file",
            "project" => "project_file",
            "xaml" => "xaml_file",
            "source" => "csharp_file",
            "test" => "csharp_test_file",
            _ => "unknown_file"
        };
    }

    private static string InferRoleFromFileKind(string fileKind)
    {
        return fileKind switch
        {
            "solution" => "solution",
            "project" => "project",
            "xaml" => "views",
            "test" => "tests",
            _ => ""
        };
    }

    private static List<string> BuildPatternTags(WorkspaceFileRecord file)
    {
        var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(file.FileKind, "xaml", StringComparison.OrdinalIgnoreCase))
            tags.Add("xaml_ui");
        if (string.Equals(file.FileKind, "test", StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.Identity.Role, "tests", StringComparison.OrdinalIgnoreCase))
            tags.Add("testing");
        if (string.Equals(file.Identity.Role, "repository", StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.Identity.Role, "storage", StringComparison.OrdinalIgnoreCase))
            tags.Add("repository");
        if (string.Equals(file.Identity.Role, "storage", StringComparison.OrdinalIgnoreCase))
            tags.Add("storage");
        if (string.Equals(file.Identity.Role, "contracts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.Identity.Role, "core", StringComparison.OrdinalIgnoreCase))
            tags.Add("contracts");
        if (string.Equals(file.Identity.Role, "state", StringComparison.OrdinalIgnoreCase)
            || file.Identity.NamespaceHint.Contains(".ViewModels", StringComparison.OrdinalIgnoreCase))
            tags.Add("MVVM");
        if (string.Equals(file.FileKind, "project", StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.FileKind, "solution", StringComparison.OrdinalIgnoreCase))
            tags.Add("dependency_injection");

        return tags.ToList();
    }

    private static int ReadLineCount(string workspaceRoot, string relativePath)
    {
        try
        {
            var fullPath = Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                return 0;

            var count = 0;
            using var reader = new StreamReader(fullPath);
            while (reader.ReadLine() is not null)
                count++;
            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static string BuildTruthFingerprint(
        WorkspaceSnapshotRecord snapshot,
        WorkspaceProjectGraphRecord graph)
    {
        var builder = new StringBuilder();
        builder.AppendLine(snapshot.ExclusionPolicyVersion);
        builder.AppendLine(snapshot.ScannerVersion);
        foreach (var solution in snapshot.SolutionPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine($"solution:{solution}");
        foreach (var project in snapshot.ProjectPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine($"project:{project}");
        foreach (var file in snapshot.Files.OrderBy(current => current.RelativePath, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine($"file:{file.RelativePath}|kind:{file.FileKind}|hash:{file.ContentSha256}|owner:{file.OwningProjectPath}");
        foreach (var reference in graph.References
                     .OrderBy(current => current.SourcePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(current => current.TargetPath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(current => current.ReferenceKind, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"reference:{reference.ReferenceKind}|{reference.SourcePath}|{reference.TargetPath}|{reference.Include}|{reference.Version}");
        }

        return BuildDeterministicId("workspace_truth_fingerprint.v1", builder.ToString());
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

    private static string BuildDeterministicId(params string[] parts)
    {
        var payload = string.Join("|", parts.Select(part => part ?? ""));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))[..16]).ToLowerInvariant();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static AppSettings CloneSettings(AppSettings settings)
    {
        return new AppSettings
        {
            Endpoint = settings.Endpoint,
            Model = settings.Model,
            IntakeModel = settings.IntakeModel,
            CoderModel = settings.CoderModel,
            EmbedderModel = settings.EmbedderModel,
            EmbedderBackend = settings.EmbedderBackend,
            QdrantEndpoint = settings.QdrantEndpoint,
            QdrantCollection = settings.QdrantCollection,
            WorkspaceRoot = settings.WorkspaceRoot,
            EnableAdvisoryAgents = settings.EnableAdvisoryAgents,
            EnableSummaryAgent = settings.EnableSummaryAgent,
            EnableSuggestionAgent = settings.EnableSuggestionAgent,
            EnableBuildProfileAgent = settings.EnableBuildProfileAgent,
            EnablePhraseFamilyAgent = settings.EnablePhraseFamilyAgent,
            EnableTemplateSelectorAgent = settings.EnableTemplateSelectorAgent,
            EnableForensicsAgent = settings.EnableForensicsAgent,
            SummaryAgentModel = settings.SummaryAgentModel,
            SuggestionAgentModel = settings.SuggestionAgentModel,
            BuildProfileAgentModel = settings.BuildProfileAgentModel,
            PhraseFamilyAgentModel = settings.PhraseFamilyAgentModel,
            TemplateSelectorAgentModel = settings.TemplateSelectorAgentModel,
            ForensicsAgentModel = settings.ForensicsAgentModel,
            AgentTimeoutSeconds = settings.AgentTimeoutSeconds,
            AutoActivateValidatedTaskboardWhenNoActivePlan = settings.AutoActivateValidatedTaskboardWhenNoActivePlan,
            ConfirmBeforeReplacingActivePlan = settings.ConfirmBeforeReplacingActivePlan,
            ShowArchivedTaskboards = settings.ShowArchivedTaskboards,
            TaskboardActionMessageDedupeWindowSeconds = settings.TaskboardActionMessageDedupeWindowSeconds
        };
    }

    private StructuralTruthLoadResult LoadStructuralTruth(string workspaceRoot, RamDbService ramDbService)
    {
        try
        {
            var (snapshot, projectGraph) = _workspaceStructuralTruthService.CaptureAndPersist(workspaceRoot, ramDbService);
            return new StructuralTruthLoadResult
            {
                Snapshot = snapshot,
                ProjectGraph = projectGraph
            };
        }
        catch (Exception ex)
        {
            var snapshot = _artifactStore.LoadWorkspaceSnapshot(ramDbService, workspaceRoot);
            var projectGraph = _artifactStore.LoadWorkspaceProjectGraph(ramDbService, workspaceRoot);
            if (snapshot is not null && projectGraph is not null)
            {
                return new StructuralTruthLoadResult
                {
                    Snapshot = snapshot,
                    ProjectGraph = projectGraph,
                    UsedFallback = true,
                    Evidence =
                    [
                        Evidence("structural_truth_fallback", "enabled", "Fresh Stage 0 capture failed, so preparation reused the last persisted workspace truth artifacts."),
                        Evidence("structural_truth_fallback_reason", SummarizeException(ex), "Direct-service preparation stayed read-only against the last known deterministic truth.")
                    ]
                };
            }

            throw;
        }
    }

    private ArtifactPersistenceResult PersistWorkspaceArtifact<T>(
        string workspaceRoot,
        string relativePath,
        T value,
        Action databaseSaveAction,
        string artifactType)
    {
        var result = PersistWorkspaceArtifactFileOnly(workspaceRoot, relativePath, value, artifactType);
        var databaseResult = PersistWorkspaceArtifactDatabaseOnly(databaseSaveAction, artifactType);
        result.DatabaseSucceeded = databaseResult.DatabaseSucceeded;
        result.DatabaseFailureSummary = databaseResult.DatabaseFailureSummary;
        return result;
    }

    private ArtifactPersistenceResult PersistWorkspaceArtifactFileOnly<T>(
        string workspaceRoot,
        string relativePath,
        T value,
        string artifactType)
    {
        var result = new ArtifactPersistenceResult
        {
            ArtifactType = artifactType,
            FileSucceeded = true,
            DatabaseSucceeded = true
        };

        try
        {
            WriteArtifactFile(workspaceRoot, relativePath, JsonSerializer.Serialize(value, JsonOptions));
        }
        catch (Exception ex)
        {
            result.FileSucceeded = false;
            result.FileFailureSummary = SummarizeException(ex);
        }

        return result;
    }

    private static ArtifactPersistenceResult PersistWorkspaceArtifactDatabaseOnly(
        Action databaseSaveAction,
        string artifactType)
    {
        var result = new ArtifactPersistenceResult
        {
            ArtifactType = artifactType,
            FileSucceeded = true,
            DatabaseSucceeded = true
        };

        try
        {
            databaseSaveAction();
        }
        catch (Exception ex)
        {
            result.DatabaseSucceeded = false;
            result.DatabaseFailureSummary = SummarizeException(ex);
        }

        return result;
    }

    private static string EvaluateFilePersistenceStatus(IEnumerable<ArtifactPersistenceResult> persistenceResults)
    {
        var results = persistenceResults.ToList();
        if (results.Count == 0)
            return "file_failed";

        if (results.All(result => result.FileSucceeded))
            return "file_persisted";

        if (results.Any(result => result.FileSucceeded))
            return "file_partial";

        return "file_failed";
    }

    private static string EvaluateDatabasePersistenceStatus(IEnumerable<ArtifactPersistenceResult> persistenceResults)
    {
        var results = persistenceResults.ToList();
        if (results.Count == 0)
            return "database_failed";

        if (results.All(result => result.DatabaseSucceeded))
            return "database_persisted";

        if (results.Any(result => result.DatabaseSucceeded))
            return "database_partial";

        return "database_failed";
    }

    private static string EvaluateOverallPersistenceStatus(string filePersistenceStatus, string databasePersistenceStatus)
    {
        if (string.Equals(filePersistenceStatus, "file_failed", StringComparison.OrdinalIgnoreCase))
            return "failed";

        if (!string.Equals(filePersistenceStatus, "file_persisted", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(databasePersistenceStatus, "database_persisted", StringComparison.OrdinalIgnoreCase))
        {
            return "partial";
        }

        return "persisted";
    }

    private static List<WorkspaceRetrievalEvidenceRecord> BuildPersistenceEvidence(IEnumerable<ArtifactPersistenceResult> persistenceResults)
    {
        var evidence = new List<WorkspaceRetrievalEvidenceRecord>();
        foreach (var result in persistenceResults
                     .OrderBy(current => current.ArtifactType, StringComparer.OrdinalIgnoreCase))
        {
            evidence.Add(Evidence(
                $"{result.ArtifactType}_file",
                result.FileSucceeded ? "file_persisted" : "file_failed",
                result.FileSucceeded
                    ? "Preparation artifact file write succeeded."
                    : result.FileFailureSummary));
            evidence.Add(Evidence(
                $"{result.ArtifactType}_db",
                result.DatabaseSucceeded ? "database_persisted" : "database_failed",
                result.DatabaseSucceeded
                    ? "Preparation artifact database persistence succeeded."
                    : result.DatabaseFailureSummary));
        }

        return evidence;
    }

    private static void WriteArtifactFile(string workspaceRoot, string relativePath, string content)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("Workspace artifact file path is required.");

        var fullPath = Path.Combine(
            workspaceRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(fullPath, content ?? "");
    }

    private static string SummarizeException(Exception ex)
    {
        return $"{ex.GetType().Name}: {ex.Message}";
    }

    private sealed class ArtifactPersistenceResult
    {
        public string ArtifactType { get; init; } = "";
        public bool FileSucceeded { get; set; }
        public bool DatabaseSucceeded { get; set; }
        public string FileFailureSummary { get; set; } = "";
        public string DatabaseFailureSummary { get; set; } = "";
    }

    private sealed class StructuralTruthLoadResult
    {
        public WorkspaceSnapshotRecord Snapshot { get; init; } = new();
        public WorkspaceProjectGraphRecord ProjectGraph { get; init; } = new();
        public bool UsedFallback { get; init; }
        public List<WorkspaceRetrievalEvidenceRecord> Evidence { get; init; } = [];
    }
}
