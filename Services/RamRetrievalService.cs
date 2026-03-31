using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class RamRetrievalService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly OllamaClient _ollamaClient;
    private readonly QdrantService _qdrantService;
    private readonly SettingsService _settingsService;
    private readonly RamDbService _ramDbService;
    private readonly ModelRoleConfigurationService _modelRoleConfigurationService;
    private readonly RamDataDisciplineService _dataDisciplineService;
    private readonly RamRetrievalQueryBuilderService _queryBuilderService = new();

    public RamRetrievalService(
        RamDbService ramDbService,
        SettingsService? settingsService = null,
        OllamaClient? ollamaClient = null,
        QdrantService? qdrantService = null,
        ModelRoleConfigurationService? modelRoleConfigurationService = null,
        RamDataDisciplineService? dataDisciplineService = null)
    {
        _ramDbService = ramDbService;
        _settingsService = settingsService ?? new SettingsService();
        _ollamaClient = ollamaClient ?? new OllamaClient();
        _qdrantService = qdrantService ?? new QdrantService();
        _modelRoleConfigurationService = modelRoleConfigurationService ?? new ModelRoleConfigurationService();
        _dataDisciplineService = dataDisciplineService ?? new RamDataDisciplineService();
    }

    public AppSettings LoadSettings()
    {
        var settings = _settingsService.Load();
        _modelRoleConfigurationService.Normalize(settings);
        return settings;
    }

    public bool IsEnabled(AppSettings settings)
    {
        if (settings is null)
            return false;

        _modelRoleConfigurationService.Normalize(settings);
        return string.Equals(settings.EmbedderBackend, "qdrant", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(settings.Endpoint)
            && !string.IsNullOrWhiteSpace(settings.EmbedderModel)
            && !string.IsNullOrWhiteSpace(settings.QdrantEndpoint)
            && !string.IsNullOrWhiteSpace(settings.QdrantCollection);
    }

    public Task<RamRetrievalBackendStatusRecord> TestBackendAsync(CancellationToken cancellationToken = default)
    {
        return TestBackendAsync(LoadSettings(), cancellationToken);
    }

    public async Task<RamRetrievalBackendStatusRecord> TestBackendAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        _modelRoleConfigurationService.Normalize(settings);
        if (!IsEnabled(settings))
        {
            return new RamRetrievalBackendStatusRecord
            {
                BackendType = FirstNonEmpty(settings.EmbedderBackend, "qdrant"),
                Endpoint = settings.QdrantEndpoint,
                CollectionName = settings.QdrantCollection,
                StatusSummary = "Retrieval backend is not fully configured."
            };
        }

        return await _qdrantService.TestConnectionAsync(
            settings.QdrantEndpoint,
            settings.QdrantCollection,
            cancellationToken);
    }

    public RamRetrievalQueryPacketRecord BuildRepairQueryPacket(
        string workspaceRoot,
        RepairPlanInput input,
        ToolRequest request)
    {
        var recentArtifacts = _ramDbService.LoadLatestArtifacts(workspaceRoot, 120);
        return _queryBuilderService.BuildRepairQueryPacket(
            workspaceRoot,
            input,
            request.TaskboardPlanTitle ?? "",
            "dotnet_desktop",
            FirstNonEmpty(GetArgument(request, "active_target"), input.TargetProjectPath),
            recentArtifacts);
    }

    public RamRetrievalQueryPacketRecord BuildFeatureUpdateQueryPacket(
        string workspaceRoot,
        string problemSummary,
        string targetFilePath,
        string targetProjectPath,
        string targetSolutionPath,
        string planTitle)
    {
        return _queryBuilderService.BuildFeatureUpdateQueryPacket(
            workspaceRoot,
            problemSummary,
            targetFilePath,
            targetProjectPath,
            targetSolutionPath,
            planTitle);
    }

    public List<RamRetrievalIndexRecord> BuildIndexRecords(
        string workspaceRoot,
        RamRetrievalQueryPacketRecord queryPacket)
    {
        var recentArtifacts = _ramDbService.LoadLatestArtifacts(workspaceRoot, 160);
        return BuildIndexRecords(workspaceRoot, queryPacket, recentArtifacts);
    }

    public async Task<RamRetrievalPreparationResult> PrepareRepairContextAsync(
        string workspaceRoot,
        RepairPlanInput input,
        ToolRequest request,
        AppSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        var queryPacket = BuildRepairQueryPacket(workspaceRoot, input, request);
        return await PrepareContextPacketAsync(
            workspaceRoot,
            queryPacket,
            request,
            settings,
            cancellationToken);
    }

    public async Task<RamRetrievalPreparationResult> PrepareFeatureUpdateContextAsync(
        string workspaceRoot,
        string problemSummary,
        string targetFilePath,
        string targetProjectPath,
        string targetSolutionPath,
        string planTitle,
        ToolRequest request,
        AppSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        var queryPacket = BuildFeatureUpdateQueryPacket(
            workspaceRoot,
            problemSummary,
            targetFilePath,
            targetProjectPath,
            targetSolutionPath,
            planTitle);
        return await PrepareContextPacketAsync(
            workspaceRoot,
            queryPacket,
            request,
            settings,
            cancellationToken);
    }

    public async Task<RamRetrievalPreparationResult> PrepareContextPacketAsync(
        string workspaceRoot,
        RamRetrievalQueryPacketRecord queryPacket,
        ToolRequest request,
        AppSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= LoadSettings();
        _modelRoleConfigurationService.Normalize(settings);

        if (!IsEnabled(settings))
        {
            return new RamRetrievalPreparationResult
            {
                Enabled = false,
                Success = false,
                StatusCode = "retrieval_disabled",
                Message = "Retrieval backend is not configured for the current workspace.",
                BackendStatus = new RamRetrievalBackendStatusRecord
                {
                    BackendType = FirstNonEmpty(settings.EmbedderBackend, "qdrant"),
                    Endpoint = settings.QdrantEndpoint,
                    CollectionName = settings.QdrantCollection,
                    StatusSummary = "Retrieval backend is not configured."
                }
            };
        }

        var backendStatus = await TestBackendAsync(settings, cancellationToken);
        if (!backendStatus.ConnectionOk)
        {
            return new RamRetrievalPreparationResult
            {
                Enabled = true,
                Success = false,
                StatusCode = "retrieval_backend_unavailable",
                Message = backendStatus.StatusSummary,
                BackendStatus = backendStatus
            };
        }

        try
        {
            var recentArtifacts = _ramDbService.LoadLatestArtifacts(workspaceRoot, 160);
            var indexRecords = BuildIndexRecords(workspaceRoot, queryPacket, recentArtifacts);
            if (indexRecords.Count == 0)
            {
                return new RamRetrievalPreparationResult
                {
                    Enabled = true,
                    Success = false,
                    StatusCode = "retrieval_no_index_records",
                    Message = "No retrieval records were available for this maintenance context.",
                    BackendStatus = backendStatus
                };
            }

            var indexEmbeddings = await _ollamaClient.GenerateEmbeddingsAsync(
                settings.Endpoint,
                settings.EmbedderModel,
                indexRecords.Select(BuildEmbeddingText).ToList(),
                cancellationToken);
            if (indexEmbeddings.Count != indexRecords.Count || indexEmbeddings.Count == 0)
                throw new InvalidOperationException("Embedder returned an unexpected number of index vectors.");

            await _qdrantService.EnsureCollectionAsync(
                settings.QdrantEndpoint,
                settings.QdrantCollection,
                indexEmbeddings[0].Count,
                cancellationToken);
            backendStatus.CollectionReady = true;

            var indexBatch = new RamRetrievalIndexBatchRecord
            {
                BatchId = Guid.NewGuid().ToString("N"),
                WorkspaceRoot = workspaceRoot,
                BackendType = "qdrant",
                EmbedderModel = settings.EmbedderModel,
                CollectionName = settings.QdrantCollection,
                QueryKind = queryPacket.QueryKind,
                RecordCount = indexRecords.Count,
                SourceKinds = indexRecords.Select(current => current.SourceKind).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(current => current, StringComparer.OrdinalIgnoreCase).ToList(),
                Records = indexRecords,
                CreatedUtc = DateTime.UtcNow.ToString("O")
            };
            var indexBatchArtifact = SaveRetrievalArtifact(
                workspaceRoot,
                BuildIndexBatchArtifactPath(queryPacket.QueryKind, indexBatch.BatchId),
                "retrieval_index_batch",
                $"Retrieval index batch: {queryPacket.QueryKind}",
                indexBatch,
                $"backend=qdrant embedder={settings.EmbedderModel} records={indexBatch.RecordCount}",
                request);

            var points = indexRecords.Zip(indexEmbeddings, (record, vector) => BuildPoint(record, vector)).ToList();
            await _qdrantService.UpsertAsync(
                settings.QdrantEndpoint,
                settings.QdrantCollection,
                points,
                cancellationToken);

            var queryArtifact = SaveRetrievalArtifact(
                workspaceRoot,
                BuildQueryArtifactPath(queryPacket.QueryKind, queryPacket.QueryId),
                "retrieval_query_packet",
                $"Retrieval query: {queryPacket.QueryKind}",
                queryPacket,
                $"kind={queryPacket.QueryKind} targets={DisplayList(queryPacket.TargetPaths)}",
                request);

            var queryEmbeddings = await _ollamaClient.GenerateEmbeddingsAsync(
                settings.Endpoint,
                settings.EmbedderModel,
                [BuildQueryEmbeddingText(queryPacket)],
                cancellationToken);
            if (queryEmbeddings.Count != 1 || queryEmbeddings[0].Count == 0)
                throw new InvalidOperationException("Embedder did not return a query vector.");

            var searchHits = await _qdrantService.SearchAsync(
                settings.QdrantEndpoint,
                settings.QdrantCollection,
                queryEmbeddings[0],
                12,
                new Dictionary<string, object?>
                {
                    ["workspace_root"] = workspaceRoot,
                    ["language"] = "csharp"
                },
                cancellationToken);

            var rankedHits = RankHits(queryPacket, searchHits);
            var retrievalResult = new RamRetrievalResultRecord
            {
                ResultId = Guid.NewGuid().ToString("N"),
                QueryId = queryPacket.QueryId,
                WorkspaceRoot = workspaceRoot,
                BackendType = "qdrant",
                EmbedderModel = settings.EmbedderModel,
                CollectionName = settings.QdrantCollection,
                HitCount = rankedHits.Count,
                SourceKinds = rankedHits.Select(current => current.SourceKind).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(current => current, StringComparer.OrdinalIgnoreCase).ToList(),
                Hits = rankedHits,
                QueryArtifactRelativePath = queryArtifact.RelativePath,
                IndexBatchArtifactRelativePath = indexBatchArtifact.RelativePath,
                CreatedUtc = DateTime.UtcNow.ToString("O")
            };
            var retrievalResultArtifact = SaveRetrievalArtifact(
                workspaceRoot,
                BuildResultArtifactPath(queryPacket.QueryKind, retrievalResult.ResultId),
                "retrieval_result",
                $"Retrieval result: {queryPacket.QueryKind}",
                retrievalResult,
                $"hits={retrievalResult.HitCount} sources={DisplayList(retrievalResult.SourceKinds)}",
                request);

            var contextPacket = BuildContextPacket(workspaceRoot, queryPacket, retrievalResult, settings, indexBatchArtifact.RelativePath);
            var contextPacketArtifact = SaveRetrievalArtifact(
                workspaceRoot,
                BuildContextPacketArtifactPath(queryPacket.QueryKind, contextPacket.PacketId),
                "coder_context_packet",
                $"Coder context packet: {queryPacket.QueryKind}",
                contextPacket,
                contextPacket.RetrievalSummary,
                request);

            return new RamRetrievalPreparationResult
            {
                Enabled = true,
                Success = true,
                StatusCode = "retrieval_ready",
                Message = contextPacket.RetrievalSummary,
                BackendStatus = backendStatus,
                IndexBatch = indexBatch,
                IndexBatchArtifact = indexBatchArtifact,
                QueryPacket = queryPacket,
                QueryArtifact = queryArtifact,
                RetrievalResult = retrievalResult,
                RetrievalResultArtifact = retrievalResultArtifact,
                ContextPacket = contextPacket,
                ContextPacketArtifact = contextPacketArtifact
            };
        }
        catch (Exception ex)
        {
            return new RamRetrievalPreparationResult
            {
                Enabled = true,
                Success = false,
                StatusCode = "retrieval_failure",
                Message = ex.Message,
                BackendStatus = backendStatus
            };
        }
    }

    private List<RamRetrievalIndexRecord> BuildIndexRecords(
        string workspaceRoot,
        RamRetrievalQueryPacketRecord queryPacket,
        IReadOnlyList<ArtifactRecord> recentArtifacts)
    {
        var records = new List<RamRetrievalIndexRecord>();
        records.AddRange(BuildSourceFileRecords(workspaceRoot, queryPacket, recentArtifacts));
        records.AddRange(BuildArtifactRecords(workspaceRoot, queryPacket, recentArtifacts));

        return records
            .GroupBy(current => current.RecordId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(current => current.SourceKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(current => current.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IEnumerable<RamRetrievalIndexRecord> BuildSourceFileRecords(
        string workspaceRoot,
        RamRetrievalQueryPacketRecord queryPacket,
        IReadOnlyList<ArtifactRecord> recentArtifacts)
    {
        var sourcePaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in queryPacket.TargetPaths.Concat(queryPacket.ScopePaths))
            AddIfExisting(workspaceRoot, sourcePaths, path);

        foreach (var artifact in recentArtifacts)
        {
            if (!string.Equals(artifact.ArtifactType, "taskboard_normalized_run", StringComparison.OrdinalIgnoreCase))
                continue;

            var normalized = TryDeserialize<TaskboardNormalizedRunRecord>(artifact.Content);
            if (normalized?.FileTouchRollups is null)
                continue;

            foreach (var path in normalized.FileTouchRollups
                         .Where(current => current.RepeatedTouchCount > 0)
                         .OrderByDescending(current => current.RepeatedTouchCount)
                         .ThenBy(current => current.FilePath, StringComparer.OrdinalIgnoreCase)
                         .Take(5)
                         .Select(current => current.FilePath))
            {
                AddIfExisting(workspaceRoot, sourcePaths, path);
            }
        }

        var projectId = ResolveProjectId(queryPacket.TargetPaths);
        foreach (var path in sourcePaths)
        {
            var fullPath = Path.Combine(workspaceRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                continue;

            var extension = Path.GetExtension(path).ToLowerInvariant();
            var sourceKind = extension switch
            {
                ".sln" => "solution_metadata",
                ".csproj" => "project_metadata",
                _ => "source_file"
            };
            var text = ReadBoundedFile(fullPath);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            yield return new RamRetrievalIndexRecord
            {
                RecordId = BuildDeterministicId("source", workspaceRoot, path),
                WorkspaceRoot = workspaceRoot,
                SourceKind = sourceKind,
                Title = $"{sourceKind}: {path}",
                Text = text,
                Summary = $"path={path}",
                SourcePath = path,
                ArtifactType = extension switch
                {
                    ".sln" => "solution_metadata",
                    ".csproj" => "project_metadata",
                    _ => "artifact_reference"
                },
                Language = "csharp",
                ProjectId = projectId,
                TrustLabel = "current_truth",
                RecencyLabel = "current",
                Tags = BuildDistinctStrings("code", "maintenance_context", sourceKind).ToList(),
                TargetPaths = BuildDistinctStrings(path).ToList(),
                CreatedUtc = DateTime.UtcNow.ToString("O"),
                UpdatedUtc = File.GetLastWriteTimeUtc(fullPath).ToString("O")
            };
        }
    }

    private IEnumerable<RamRetrievalIndexRecord> BuildArtifactRecords(
        string workspaceRoot,
        RamRetrievalQueryPacketRecord queryPacket,
        IReadOnlyList<ArtifactRecord> recentArtifacts)
    {
        var projectId = ResolveProjectId(queryPacket.TargetPaths);
        foreach (var artifact in recentArtifacts.Take(80))
        {
            var trustLabel = _dataDisciplineService.ResolveTrustLabel(
                FirstNonEmpty(artifact.LifecycleState, _dataDisciplineService.ResolveArtifactLifecycleState(artifact)));
            var recencyLabel = _dataDisciplineService.ResolveRecencyLabel(ParseUtc(artifact.UpdatedUtc));
            var dataCategory = FirstNonEmpty(artifact.DataCategory, _dataDisciplineService.ResolveArtifactDataCategory(artifact));
            var sourceKind = ResolveArtifactSourceKind(artifact);
            if (string.IsNullOrWhiteSpace(sourceKind))
                continue;

            if (string.Equals(sourceKind, "validator_outcome", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var record in BuildCorpusRecords(workspaceRoot, artifact, projectId, trustLabel, recencyLabel))
                    yield return record;
                continue;
            }

            var text = BuildArtifactText(artifact);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            yield return new RamRetrievalIndexRecord
            {
                RecordId = BuildDeterministicId("artifact", workspaceRoot, artifact.RelativePath, artifact.Id.ToString()),
                WorkspaceRoot = workspaceRoot,
                SourceKind = sourceKind,
                Title = FirstNonEmpty(artifact.Title, artifact.RelativePath),
                Text = text,
                Summary = FirstNonEmpty(artifact.Summary, $"artifact_type={artifact.ArtifactType}"),
                SourcePath = artifact.RelativePath,
                ArtifactType = artifact.ArtifactType,
                SourceArtifactId = artifact.Id,
                SourceRunStateId = artifact.SourceRunStateId,
                Language = "csharp",
                ProjectId = projectId,
                TrustLabel = trustLabel,
                RecencyLabel = recencyLabel,
                Tags = BuildDistinctStrings(sourceKind, dataCategory, artifact.ArtifactType).ToList(),
                TargetPaths = BuildDistinctStrings(queryPacket.TargetPaths.ToArray()).ToList(),
                CreatedUtc = artifact.CreatedUtc,
                UpdatedUtc = artifact.UpdatedUtc
            };

            if (!string.Equals(sourceKind, "normalized_run_record", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var hotspotRecord in BuildHotspotRecords(workspaceRoot, artifact, projectId, trustLabel, recencyLabel))
                yield return hotspotRecord;
        }
    }

    private IEnumerable<RamRetrievalIndexRecord> BuildHotspotRecords(
        string workspaceRoot,
        ArtifactRecord artifact,
        string projectId,
        string trustLabel,
        string recencyLabel)
    {
        var normalized = TryDeserialize<TaskboardNormalizedRunRecord>(artifact.Content);
        if (normalized?.FileTouchRollups is null)
            yield break;

        foreach (var rollup in normalized.FileTouchRollups
                     .Where(current => current.RepeatedTouchCount > 0)
                     .OrderByDescending(current => current.RepeatedTouchCount)
                     .ThenBy(current => current.FilePath, StringComparer.OrdinalIgnoreCase)
                     .Take(6))
        {
            yield return new RamRetrievalIndexRecord
            {
                RecordId = BuildDeterministicId("hotspot", workspaceRoot, artifact.RelativePath, rollup.FilePath),
                WorkspaceRoot = workspaceRoot,
                SourceKind = "file_touch_hotspot",
                Title = $"Hotspot: {rollup.FilePath}",
                Text = $"touches={rollup.TouchCount} repeated={rollup.RepeatedTouchCount} reasons={DisplayList(rollup.ReasonCounts)} operations={DisplayList(rollup.OperationCounts)}",
                Summary = $"Repeated touches for {rollup.FilePath}",
                SourcePath = artifact.RelativePath,
                ArtifactType = artifact.ArtifactType,
                SourceArtifactId = artifact.Id,
                SourceRunStateId = artifact.SourceRunStateId,
                Language = "csharp",
                ProjectId = projectId,
                TrustLabel = trustLabel,
                RecencyLabel = recencyLabel,
                Tags = BuildDistinctStrings("hotspot", "file_touch").ToList(),
                TargetPaths = BuildDistinctStrings(rollup.FilePath).ToList(),
                CreatedUtc = artifact.CreatedUtc,
                UpdatedUtc = artifact.UpdatedUtc
            };
        }
    }

    private IEnumerable<RamRetrievalIndexRecord> BuildCorpusRecords(
        string workspaceRoot,
        ArtifactRecord artifact,
        string projectId,
        string trustLabel,
        string recencyLabel)
    {
        var corpus = TryDeserialize<RamCorpusExportRecord>(artifact.Content);
        if (corpus?.Records is null)
            yield break;

        var index = 0;
        foreach (var record in corpus.Records.Take(8))
        {
            index++;
            yield return new RamRetrievalIndexRecord
            {
                RecordId = BuildDeterministicId("validator", workspaceRoot, artifact.RelativePath, index.ToString()),
                WorkspaceRoot = workspaceRoot,
                SourceKind = "validator_outcome",
                Title = $"Validator outcome {index}: {artifact.Title}",
                Text = $"problem={record.InputProblem} state={record.NormalizedState} actions={DisplayList(record.ActionSequence)} outcome={record.Outcome} validator={record.ValidatorResult} terminal={record.TerminalTruth}",
                Summary = FirstNonEmpty(record.ValidatorResult, record.TerminalTruth),
                SourcePath = artifact.RelativePath,
                ArtifactType = artifact.ArtifactType,
                SourceArtifactId = artifact.Id,
                SourceRunStateId = artifact.SourceRunStateId,
                Language = "csharp",
                ProjectId = projectId,
                TrustLabel = trustLabel,
                RecencyLabel = recencyLabel,
                Tags = BuildDistinctStrings("validator", "corpus", "maintenance_context").ToList(),
                TargetPaths = record.ArtifactReferencePaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Take(6)
                    .ToList(),
                CreatedUtc = artifact.CreatedUtc,
                UpdatedUtc = artifact.UpdatedUtc
            };
        }
    }

    private static QdrantPointRecord BuildPoint(RamRetrievalIndexRecord record, IReadOnlyList<float> vector)
    {
        return new QdrantPointRecord
        {
            Id = record.RecordId,
            Vector = vector.ToList(),
            Payload = new Dictionary<string, object?>
            {
                ["record_id"] = record.RecordId,
                ["workspace_root"] = record.WorkspaceRoot,
                ["source_kind"] = record.SourceKind,
                ["title"] = record.Title,
                ["summary"] = record.Summary,
                ["text"] = record.Text,
                ["source_path"] = record.SourcePath,
                ["artifact_type"] = record.ArtifactType,
                ["source_artifact_id"] = record.SourceArtifactId,
                ["source_run_state_id"] = record.SourceRunStateId,
                ["language"] = record.Language,
                ["project_id"] = record.ProjectId,
                ["trust_label"] = record.TrustLabel,
                ["recency_label"] = record.RecencyLabel,
                ["tags"] = record.Tags,
                ["target_paths"] = record.TargetPaths,
                ["updated_utc"] = record.UpdatedUtc
            }
        };
    }

    private List<RamRetrievalHitRecord> RankHits(
        RamRetrievalQueryPacketRecord queryPacket,
        IReadOnlyList<QdrantSearchHitRecord> searchHits)
    {
        var allowedTrust = queryPacket.TrustFilters.Count == 0
            ? null
            : new HashSet<string>(queryPacket.TrustFilters, StringComparer.OrdinalIgnoreCase);
        var allowedRecency = queryPacket.RecencyFilters.Count == 0
            ? null
            : new HashSet<string>(queryPacket.RecencyFilters, StringComparer.OrdinalIgnoreCase);
        var targetPaths = new HashSet<string>(queryPacket.TargetPaths, StringComparer.OrdinalIgnoreCase);
        var requiredTags = new HashSet<string>(queryPacket.RequiredTags, StringComparer.OrdinalIgnoreCase);

        var results = new List<RamRetrievalHitRecord>();
        foreach (var searchHit in searchHits)
        {
            var hit = BuildHitRecord(searchHit);
            if (allowedTrust is not null && !allowedTrust.Contains(FirstNonEmpty(hit.TrustLabel)))
                continue;
            if (allowedRecency is not null && !allowedRecency.Contains(FirstNonEmpty(hit.RecencyLabel)))
                continue;

            var adjustedScore = hit.Score;
            if (string.Equals(hit.TrustLabel, "current_truth", StringComparison.OrdinalIgnoreCase))
                adjustedScore += 0.30d;
            else if (string.Equals(hit.TrustLabel, "historical_truth", StringComparison.OrdinalIgnoreCase))
                adjustedScore += 0.10d;

            if (string.Equals(hit.RecencyLabel, "current", StringComparison.OrdinalIgnoreCase))
                adjustedScore += 0.20d;
            else if (string.Equals(hit.RecencyLabel, "recent", StringComparison.OrdinalIgnoreCase))
                adjustedScore += 0.10d;

            if (!string.IsNullOrWhiteSpace(hit.SourcePath) && targetPaths.Contains(hit.SourcePath))
                adjustedScore += 0.40d;
            if (hit.TargetPaths.Any(path => targetPaths.Contains(path)))
                adjustedScore += 0.25d;
            if (requiredTags.Count > 0 && hit.Tags.Any(tag => requiredTags.Contains(tag)))
                adjustedScore += 0.15d;

            adjustedScore += hit.SourceKind switch
            {
                "source_file" or "project_metadata" or "solution_metadata" => 0.20d,
                "run_summary" or "normalized_run_record" => 0.15d,
                "repair_context" or "patch_update_artifact" or "verification_outcome" => 0.15d,
                "file_touch_hotspot" => 0.10d,
                _ => 0d
            };

            hit.AdjustedScore = adjustedScore;
            results.Add(hit);
        }

        return results
            .OrderByDescending(current => current.AdjustedScore)
            .ThenBy(current => current.SourceKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(current => current.Title, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private RamRetrievalHitRecord BuildHitRecord(QdrantSearchHitRecord searchHit)
    {
        return new RamRetrievalHitRecord
        {
            RecordId = FirstNonEmpty(ReadPayloadString(searchHit.Payload, "record_id"), searchHit.Id),
            Score = searchHit.Score,
            SourceKind = ReadPayloadString(searchHit.Payload, "source_kind"),
            Title = ReadPayloadString(searchHit.Payload, "title"),
            Summary = ReadPayloadString(searchHit.Payload, "summary"),
            Snippet = LimitText(ReadPayloadString(searchHit.Payload, "text"), 900),
            SourcePath = ReadPayloadString(searchHit.Payload, "source_path"),
            ArtifactType = ReadPayloadString(searchHit.Payload, "artifact_type"),
            SourceArtifactId = ReadPayloadLong(searchHit.Payload, "source_artifact_id"),
            SourceRunStateId = ReadPayloadString(searchHit.Payload, "source_run_state_id"),
            TrustLabel = ReadPayloadString(searchHit.Payload, "trust_label"),
            RecencyLabel = ReadPayloadString(searchHit.Payload, "recency_label"),
            Tags = ReadPayloadStringList(searchHit.Payload, "tags"),
            TargetPaths = ReadPayloadStringList(searchHit.Payload, "target_paths")
        };
    }

    private RamCoderContextPacketRecord BuildContextPacket(
        string workspaceRoot,
        RamRetrievalQueryPacketRecord queryPacket,
        RamRetrievalResultRecord retrievalResult,
        AppSettings settings,
        string indexBatchArtifactPath)
    {
        var hotspotPaths = retrievalResult.Hits
            .Where(current => string.Equals(current.SourceKind, "file_touch_hotspot", StringComparison.OrdinalIgnoreCase))
            .SelectMany(current => current.TargetPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        return new RamCoderContextPacketRecord
        {
            PacketId = Guid.NewGuid().ToString("N"),
            QueryId = queryPacket.QueryId,
            ResultId = retrievalResult.ResultId,
            WorkspaceRoot = workspaceRoot,
            BackendType = "qdrant",
            EmbedderModel = settings.EmbedderModel,
            QueryKind = queryPacket.QueryKind,
            HitCount = retrievalResult.HitCount,
            SourceKinds = retrievalResult.SourceKinds,
            SourcePaths = retrievalResult.Hits
                .Select(current => current.SourcePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList(),
            ScopePaths = queryPacket.ScopePaths,
            TargetPaths = queryPacket.TargetPaths,
            HotspotPaths = hotspotPaths,
            RetrievalSummary = $"backend=qdrant embedder={settings.EmbedderModel} query={queryPacket.QueryKind} hits={retrievalResult.HitCount} sources={DisplayList(retrievalResult.SourceKinds)}",
            ContextText = BuildContextText(queryPacket, retrievalResult, hotspotPaths),
            QueryArtifactRelativePath = retrievalResult.QueryArtifactRelativePath,
            RetrievalResultArtifactRelativePath = BuildResultArtifactPath(queryPacket.QueryKind, retrievalResult.ResultId),
            IndexBatchArtifactRelativePath = indexBatchArtifactPath,
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private static string BuildContextText(
        RamRetrievalQueryPacketRecord queryPacket,
        RamRetrievalResultRecord retrievalResult,
        IReadOnlyList<string> hotspotPaths)
    {
        var lines = new List<string>
        {
            "Maintenance retrieval context:",
            $"Query kind: {DisplayValue(queryPacket.QueryKind)}",
            $"Problem: {DisplayValue(queryPacket.ProblemSummary)}",
            $"Scope: {DisplayValue(DisplayList(queryPacket.ScopePaths))}",
            $"Targets: {DisplayValue(DisplayList(queryPacket.TargetPaths))}",
            $"Top sources: {DisplayValue(DisplayList(retrievalResult.SourceKinds))}"
        };

        if (hotspotPaths.Count > 0)
            lines.Add($"Hotspot files: {string.Join(", ", hotspotPaths)}");

        var index = 0;
        foreach (var hit in retrievalResult.Hits)
        {
            index++;
            lines.Add($"{index}. [{DisplayValue(hit.SourceKind)}] {DisplayValue(hit.Title)} score={hit.AdjustedScore:F3}");
            if (!string.IsNullOrWhiteSpace(hit.SourcePath))
                lines.Add($"   path={hit.SourcePath}");
            lines.Add($"   trust={DisplayValue(hit.TrustLabel)} recency={DisplayValue(hit.RecencyLabel)}");
            if (!string.IsNullOrWhiteSpace(hit.Summary))
                lines.Add($"   summary={hit.Summary}");
            if (!string.IsNullOrWhiteSpace(hit.Snippet))
                lines.Add($"   snippet={CollapseWhitespace(hit.Snippet)}");
        }

        lines.Add("Runtime authority: retrieval informs coder context only; scope and verification remain deterministic.");
        return string.Join(Environment.NewLine, lines);
    }

    private ArtifactRecord SaveRetrievalArtifact(
        string workspaceRoot,
        string relativePath,
        string artifactType,
        string title,
        object payload,
        string summary,
        ToolRequest request)
    {
        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();
        artifact.IntentTitle = "";
        artifact.ArtifactType = artifactType;
        artifact.Title = title;
        artifact.RelativePath = relativePath;
        artifact.Content = JsonSerializer.Serialize(payload, JsonOptions);
        artifact.Summary = summary;
        artifact.SourceRunStateId = request.TaskboardRunStateId ?? "";
        artifact.SourceBatchId = request.TaskboardBatchId ?? "";
        artifact.SourceWorkItemId = request.TaskboardWorkItemId ?? "";

        if (existingArtifact is null)
            return _ramDbService.SaveArtifact(workspaceRoot, artifact);

        _ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private static string ResolveArtifactSourceKind(ArtifactRecord artifact)
    {
        return artifact.ArtifactType switch
        {
            "taskboard_run_summary" => "run_summary",
            "taskboard_normalized_run" => "normalized_run_record",
            "verification_result" or "auto_validation_result" or "build_result" => "verification_outcome",
            "repair_context" or "repair_proposal" => "repair_context",
            "csharp_patch_contract" or "csharp_patch_plan" or "patch_draft" or "patch_apply_result" or "repair_loop_closure" => "patch_update_artifact",
            "taskboard_corpus_export" => "validator_outcome",
            _ => string.Equals(artifact.DataCategory, "artifact_reference", StringComparison.OrdinalIgnoreCase)
                ? "artifact_metadata"
                : ""
        };
    }

    private static string BuildArtifactText(ArtifactRecord artifact)
    {
        switch (artifact.ArtifactType)
        {
            case "taskboard_run_summary":
            {
                var summary = TryDeserialize<TaskboardRunTerminalSummaryRecord>(artifact.Content);
                if (summary is not null)
                {
                    return $"status={summary.FinalStatus} category={summary.TerminalCategory} terminal={FirstNonEmpty(summary.TerminalBatchTitle, "(none)")} / {FirstNonEmpty(summary.TerminalWorkItemTitle, "(none)")} note={FirstNonEmpty(summary.TerminalNote, "(none)")} verification={FirstNonEmpty(summary.LastVerificationOutcome, "(none)")}";
                }

                break;
            }
            case "taskboard_normalized_run":
            {
                var normalized = TryDeserialize<TaskboardNormalizedRunRecord>(artifact.Content);
                if (normalized is not null)
                {
                    return $"family={FirstNonEmpty(normalized.WorkFamily, "(none)")} phrase={FirstNonEmpty(normalized.PhraseFamily, "(none)")} operation={FirstNonEmpty(normalized.OperationKind, "(none)")} stack={FirstNonEmpty(normalized.StackFamily, "(none)")} lane={FirstNonEmpty(normalized.SelectedLaneKind, "(none)")} blocker={FirstNonEmpty(normalized.BlockerReason, "(none)")} patch_family={FirstNonEmpty(normalized.PatchMutationFamily, "(none)")}";
                }

                break;
            }
            case "verification_result":
            {
                var outcome = TryDeserialize<VerificationOutcomeRecord>(artifact.Content);
                if (outcome is not null)
                {
                    return $"classification={FirstNonEmpty(outcome.OutcomeClassification, "(none)")} target={FirstNonEmpty(outcome.ResolvedTarget, "(none)")} warnings={outcome.AfterWarningCount ?? 0} warning_codes={DisplayList(outcome.WarningCodes)} policy={FirstNonEmpty(outcome.WarningPolicyMode, "(none)")}";
                }

                break;
            }
            case "repair_proposal":
            {
                var proposal = TryDeserialize<RepairProposalRecord>(artifact.Content);
                if (proposal is not null)
                {
                    return $"title={FirstNonEmpty(proposal.Title, "(none)")} target={FirstNonEmpty(proposal.TargetFilePath, "(none)")} action={FirstNonEmpty(proposal.ProposedActionType, "(none)")} rationale={FirstNonEmpty(proposal.Rationale, "(none)")}";
                }

                break;
            }
            case "csharp_patch_contract":
            {
                var contract = TryDeserialize<CSharpPatchWorkContractRecord>(artifact.Content);
                if (contract is not null)
                {
                    return $"mutation_family={FirstNonEmpty(contract.MutationFamily, "(none)")} scope={FirstNonEmpty(contract.AllowedEditScope, "(none)")} targets={DisplayList(contract.TargetFiles)} warning_policy={FirstNonEmpty(contract.WarningPolicyMode, "(none)")}";
                }

                break;
            }
            case "csharp_patch_plan":
            {
                var plan = TryDeserialize<CSharpPatchPlanRecord>(artifact.Content);
                if (plan is not null)
                {
                    return $"summary={FirstNonEmpty(plan.Summary, "(none)")} validation_steps={DisplayList(plan.ValidationSteps)} rerun={DisplayList(plan.RerunRequirements)} targets={DisplayList(plan.TargetFiles)}";
                }

                break;
            }
            case "patch_draft":
            {
                var draft = TryDeserialize<PatchDraftRecord>(artifact.Content);
                if (draft is not null)
                {
                    return $"target={FirstNonEmpty(draft.TargetFilePath, "(none)")} kind={FirstNonEmpty(draft.DraftKind, "(none)")} can_apply={draft.CanApplyLocally.ToString().ToLowerInvariant()} rationale={FirstNonEmpty(draft.RationaleSummary, "(none)")}";
                }

                break;
            }
            case "patch_apply_result":
            {
                var apply = TryDeserialize<PatchApplyResultRecord>(artifact.Content);
                if (apply is not null)
                {
                    return $"target={FirstNonEmpty(apply.Draft.TargetFilePath, "(none)")} output={LimitText(FirstNonEmpty(apply.ApplyOutput, "(none)"), 800)}";
                }

                break;
            }
        }

        return LimitText(
            FirstNonEmpty(
                artifact.Summary,
                artifact.Content,
                $"artifact_type={artifact.ArtifactType} path={artifact.RelativePath}"),
            2000);
    }

    private static string ReadBoundedFile(string fullPath)
    {
        try
        {
            return LimitText(File.ReadAllText(fullPath), 6000);
        }
        catch
        {
            return "";
        }
    }

    private static void AddIfExisting(string workspaceRoot, ISet<string> results, string? candidatePath)
    {
        var normalized = NormalizeWorkspaceRelativePath(workspaceRoot, candidatePath);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var fullPath = Path.Combine(workspaceRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath))
            results.Add(normalized);
    }

    private static string NormalizeWorkspaceRelativePath(string workspaceRoot, string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
            return "";

        try
        {
            var fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
            var fullPath = Path.IsPathRooted(candidatePath)
                ? Path.GetFullPath(candidatePath)
                : Path.GetFullPath(Path.Combine(workspaceRoot, candidatePath));
            var workspacePrefix = fullWorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var insideWorkspace = string.Equals(fullPath, fullWorkspaceRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase);
            if (!insideWorkspace)
                return "";

            return Path.GetRelativePath(fullWorkspaceRoot, fullPath).Replace('\\', '/');
        }
        catch
        {
            return "";
        }
    }

    private static string ResolveProjectId(IReadOnlyList<string> targetPaths)
    {
        return targetPaths.FirstOrDefault(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            ?? targetPaths.FirstOrDefault(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            ?? "";
    }

    private static string BuildDeterministicId(params string[] parts)
    {
        var joined = string.Join("|", parts.Select(part => part ?? ""));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes[..16]).ToLowerInvariant();
    }

    private static string BuildIndexBatchArtifactPath(string queryKind, string batchId) => $".ram/retrieval/index/{NormalizeArtifactSegment(queryKind)}-{batchId}.json";
    private static string BuildQueryArtifactPath(string queryKind, string queryId) => $".ram/retrieval/queries/{NormalizeArtifactSegment(queryKind)}-{queryId}.json";
    private static string BuildResultArtifactPath(string queryKind, string resultId) => $".ram/retrieval/results/{NormalizeArtifactSegment(queryKind)}-{resultId}.json";
    private static string BuildContextPacketArtifactPath(string queryKind, string packetId) => $".ram/retrieval/context/{NormalizeArtifactSegment(queryKind)}-{packetId}.json";

    private static string NormalizeArtifactSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');

        return builder.ToString().Trim('-');
    }

    private static IEnumerable<string> BuildDistinctStrings(params string?[] values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildEmbeddingText(RamRetrievalIndexRecord record)
    {
        return LimitText(
            $"title={record.Title}{Environment.NewLine}kind={record.SourceKind}{Environment.NewLine}summary={record.Summary}{Environment.NewLine}{record.Text}",
            6000);
    }

    private static string BuildQueryEmbeddingText(RamRetrievalQueryPacketRecord queryPacket)
    {
        return LimitText(
            $"query_kind={queryPacket.QueryKind}{Environment.NewLine}problem={queryPacket.ProblemSummary}{Environment.NewLine}scope={DisplayList(queryPacket.ScopePaths)}{Environment.NewLine}targets={DisplayList(queryPacket.TargetPaths)}{Environment.NewLine}tags={DisplayList(queryPacket.RequiredTags)}",
            4000);
    }

    private static string ReadPayloadString(Dictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
            return "";

        return value switch
        {
            string current => current,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString() ?? "",
            JsonElement element => element.ToString(),
            _ => value.ToString() ?? ""
        };
    }

    private static long ReadPayloadLong(Dictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
            return 0L;

        return value switch
        {
            long current => current,
            int current => current,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number) => number,
            _ => long.TryParse(value.ToString(), out var parsed) ? parsed : 0L
        };
    }

    private static List<string> ReadPayloadStringList(Dictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
            return [];

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Select(current => current.ToString())
                .Where(current => !string.IsNullOrWhiteSpace(current))
                .ToList();
        }

        if (value is IEnumerable<object?> sequence)
        {
            return sequence
                .Select(current => current?.ToString() ?? "")
                .Where(current => !string.IsNullOrWhiteSpace(current))
                .ToList();
        }

        var text = value.ToString() ?? "";
        return string.IsNullOrWhiteSpace(text) ? [] : [text];
    }

    private static T? TryDeserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }

    private static string GetArgument(ToolRequest request, string key)
    {
        return request.TryGetArgument(key, out var value) ? value : "";
    }

    private static DateTime ParseUtc(string value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.MinValue;
    }

    private static string CollapseWhitespace(string value)
    {
        return string.Join(" ", value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string DisplayList(IEnumerable<string> values)
    {
        var items = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return items.Count == 0 ? "" : string.Join(", ", items);
    }

    private static string LimitText(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxChars)
            return value ?? "";

        return value[..maxChars];
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }
}
