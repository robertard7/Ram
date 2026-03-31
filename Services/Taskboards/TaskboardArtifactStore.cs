using System.IO;
using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardArtifactStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public ArtifactRecord SaveRawArtifact(RamDbService ramDbService, string workspaceRoot, string importId, string title, string rawText, string contentHash, string sourceType)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildRawArtifactPath(importId),
            "taskboard_raw",
            $"Taskboard raw import: {title}",
            rawText,
            $"source={sourceType} hash={contentHash[..Math.Min(12, contentHash.Length)]}");
    }

    public ArtifactRecord SaveParsedArtifact(RamDbService ramDbService, string workspaceRoot, string importId, string title, TaskboardParseResult parseResult)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildParsedArtifactPath(importId),
            "taskboard_parsed",
            $"Taskboard parsed tree: {title}",
            JsonSerializer.Serialize(parseResult, JsonOptions),
            $"parser={parseResult.ParserVersion} grammar={parseResult.Document?.GrammarVersion} success={parseResult.Success} lines={parseResult.Document?.LineClassifications.Count ?? 0}");
    }

    public ArtifactRecord SavePlanArtifact(RamDbService ramDbService, string workspaceRoot, string importId, TaskboardDocument document)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildPlanArtifactPath(importId),
            "taskboard_plan",
            $"Taskboard plan: {document.Title}",
            JsonSerializer.Serialize(document, JsonOptions),
            $"batches={document.Batches.Count} objective={(!string.IsNullOrWhiteSpace(document.ObjectiveText)).ToString().ToLowerInvariant()}");
    }

    public ArtifactRecord SaveValidationArtifact(RamDbService ramDbService, string workspaceRoot, string importId, string title, TaskboardValidationReport validationReport)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildValidationArtifactPath(importId),
            "taskboard_validation",
            $"Taskboard validation: {title}",
            JsonSerializer.Serialize(validationReport, JsonOptions),
            $"outcome={validationReport.Outcome.ToString().ToLowerInvariant()} errors={validationReport.Errors.Count} warnings={validationReport.Warnings.Count}");
    }

    public ArtifactRecord SaveImportRecordArtifact(RamDbService ramDbService, string workspaceRoot, TaskboardImportRecord record)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildImportRecordPath(record.ImportId),
            "taskboard_import",
            string.IsNullOrWhiteSpace(record.Title)
                ? $"Taskboard import {record.ImportId}"
                : $"Taskboard import: {record.Title}",
            JsonSerializer.Serialize(record, JsonOptions),
            $"state={record.State.ToString().ToLowerInvariant()} validation={record.ValidationOutcome.ToString().ToLowerInvariant()}");
    }

    public ArtifactRecord SaveRunProjectionArtifact(RamDbService ramDbService, string workspaceRoot, TaskboardRunProjection projection)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildRunProjectionPath(projection.RunId),
            "taskboard_run_projection",
            string.IsNullOrWhiteSpace(projection.BatchTitle)
                ? $"Taskboard run projection {projection.RunId}"
                : $"Taskboard run projection: {projection.BatchTitle}",
            JsonSerializer.Serialize(projection, JsonOptions),
            $"scope={projection.Scope} success={projection.Success.ToString().ToLowerInvariant()} execution_started={projection.ExecutionStarted.ToString().ToLowerInvariant()}");
    }

    public ArtifactRecord SaveRunStateArtifact(RamDbService ramDbService, string workspaceRoot, TaskboardPlanRunStateRecord runState)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildRunStatePath(runState.PlanImportId),
            "taskboard_run_state",
            string.IsNullOrWhiteSpace(runState.PlanTitle)
                ? $"Taskboard run state {runState.PlanImportId}"
                : $"Taskboard run state: {runState.PlanTitle}",
            JsonSerializer.Serialize(runState, JsonOptions),
            $"status={runState.PlanStatus.ToString().ToLowerInvariant()} completed={runState.CompletedWorkItemCount}/{runState.TotalWorkItemCount} runtime={runState.RuntimeStateStatusCode} version={runState.RuntimeStateVersion}");
    }

    public ArtifactRecord SaveRunSummaryArtifact(RamDbService ramDbService, string workspaceRoot, TaskboardRunTerminalSummaryRecord summary)
    {
        var relativePath = string.IsNullOrWhiteSpace(summary.SummaryArtifactRelativePath)
            ? BuildRunSummaryPath(summary.PlanImportId, summary.SummaryId)
            : summary.SummaryArtifactRelativePath;
        summary.SummaryArtifactRelativePath = relativePath;

        var artifact = SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            relativePath,
            "taskboard_run_summary",
            string.IsNullOrWhiteSpace(summary.PlanTitle)
                ? $"Taskboard run summary {summary.SummaryId}"
                : $"Taskboard run summary: {summary.PlanTitle}",
            JsonSerializer.Serialize(summary, JsonOptions),
            $"status={summary.FinalStatus} category={summary.TerminalCategory} progress={summary.CompletedWorkItemCount}/{summary.TotalWorkItemCount}");
        artifact.SourceRunStateId = summary.RunStateId;
        ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    public ArtifactRecord SaveNormalizedRunArtifact(RamDbService ramDbService, string workspaceRoot, TaskboardNormalizedRunRecord record)
    {
        var relativePath = string.IsNullOrWhiteSpace(record.RecordArtifactRelativePath)
            ? BuildNormalizedRunPath(record.PlanImportId, record.RecordId)
            : record.RecordArtifactRelativePath;
        record.RecordArtifactRelativePath = relativePath;

        var artifact = SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            relativePath,
            "taskboard_normalized_run",
            string.IsNullOrWhiteSpace(record.PlanTitle)
                ? $"Taskboard normalized run {record.RecordId}"
                : $"Taskboard normalized run: {record.PlanTitle}",
            JsonSerializer.Serialize(record, JsonOptions),
            $"status={record.FinalStatus} category={record.TerminalCategory} touches={record.FileTouchCount} changed={record.ChangedFileCount}");
        artifact.SourceRunStateId = record.RunStateId;
        ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    public ArtifactRecord SaveIndexExportArtifact(RamDbService ramDbService, string workspaceRoot, RamIndexExportRecord record)
    {
        var relativePath = string.IsNullOrWhiteSpace(record.ArtifactRelativePath)
            ? BuildIndexExportPath(record.PlanImportId, record.ExportId)
            : record.ArtifactRelativePath;
        record.ArtifactRelativePath = relativePath;

        var artifact = SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            relativePath,
            "taskboard_index_export",
            $"Taskboard index export: {record.PlanImportId}",
            JsonSerializer.Serialize(record, JsonOptions),
            $"status={record.FinalStatus} category={record.TerminalCategory} documents={record.Documents.Count}");
        artifact.SourceRunStateId = record.RunStateId;
        ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    public ArtifactRecord SaveCorpusExportArtifact(RamDbService ramDbService, string workspaceRoot, RamCorpusExportRecord record)
    {
        var relativePath = string.IsNullOrWhiteSpace(record.ArtifactRelativePath)
            ? BuildCorpusExportPath(record.PlanImportId, record.ExportId)
            : record.ArtifactRelativePath;
        record.ArtifactRelativePath = relativePath;

        var artifact = SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            relativePath,
            "taskboard_corpus_export",
            $"Taskboard corpus export: {record.PlanImportId}",
            JsonSerializer.Serialize(record, JsonOptions),
            $"status={record.FinalStatus} category={record.TerminalCategory} records={record.Records.Count}");
        artifact.SourceRunStateId = record.RunStateId;
        ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    public ArtifactRecord SaveDecompositionArtifact(RamDbService ramDbService, string workspaceRoot, TaskboardWorkItemDecompositionRecord decomposition)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildDecompositionPath(decomposition.PlanImportId, decomposition.OriginalWorkItemId),
            "taskboard_decomposition",
            $"Taskboard decomposition: {decomposition.OriginalTitle}",
            JsonSerializer.Serialize(decomposition, JsonOptions),
            $"disposition={decomposition.Disposition.ToString().ToLowerInvariant()} stack={decomposition.BuildProfile.StackFamily.ToString().ToLowerInvariant()} sub_items={decomposition.SubItems.Count}");
    }

    public ArtifactRecord SavePhraseFamilyResolutionArtifact(RamDbService ramDbService, string workspaceRoot, string importId, TaskboardPhraseFamilyResolutionRecord resolution)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildPhraseFamilyResolutionPath(importId, resolution.WorkItemId),
            "taskboard_phrase_family_resolution",
            $"Taskboard phrase family: {resolution.WorkItemTitle}",
            JsonSerializer.Serialize(resolution, JsonOptions),
            $"source={resolution.ResolutionSource.ToString().ToLowerInvariant()} phrase={resolution.PhraseFamily} tie_break={resolution.TieBreakRuleId} blocker={resolution.BlockerCode.ToString().ToLowerInvariant()}");
    }

    public ArtifactRecord SaveCommandNormalizationArtifact(RamDbService ramDbService, string workspaceRoot, string importId, CommandCanonicalizationRecord normalization)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildCommandNormalizationPath(importId, normalization.WorkItemId),
            "taskboard_command_normalization",
            $"Taskboard command normalization: {normalization.WorkItemTitle}",
            JsonSerializer.Serialize(normalization, JsonOptions),
            $"operation={normalization.NormalizedOperationKind} target={normalization.NormalizedTargetPath} template={normalization.NormalizedTemplateHint}");
    }

    public ArtifactRecord SaveExecutionGoalArtifact(RamDbService ramDbService, string workspaceRoot, string importId, TaskboardExecutionGoalResolution goalResolution)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildExecutionGoalPath(importId, goalResolution.SourceWorkItemId),
            "taskboard_execution_goal",
            $"Taskboard execution goal: {goalResolution.SourceWorkItemTitle}",
            JsonSerializer.Serialize(goalResolution, JsonOptions),
            $"goal_kind={goalResolution.GoalKind.ToString().ToLowerInvariant()} tool={goalResolution.Goal.SelectedToolId} chain={goalResolution.Goal.SelectedChainTemplateId} blocker={goalResolution.Blocker.Code.ToString().ToLowerInvariant()}");
    }

    public ArtifactRecord SaveExecutionLaneArtifact(RamDbService ramDbService, string workspaceRoot, string importId, TaskboardExecutionLaneResolution laneResolution)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildExecutionLanePath(importId, laneResolution.SourceWorkItemId),
            "taskboard_execution_lane",
            $"Taskboard execution lane: {laneResolution.SourceWorkItemTitle}",
            JsonSerializer.Serialize(laneResolution, JsonOptions),
            $"lane_kind={laneResolution.LaneKind.ToString().ToLowerInvariant()} tool={laneResolution.SelectedToolId} chain={laneResolution.SelectedChainTemplateId} blocker={laneResolution.Blocker.Code.ToString().ToLowerInvariant()}");
    }

    public ArtifactRecord SaveWorkspaceSnapshotArtifact(RamDbService ramDbService, string workspaceRoot, WorkspaceSnapshotRecord snapshot)
    {
        var content = JsonSerializer.Serialize(snapshot, JsonOptions);
        var artifact = SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildWorkspaceSnapshotPath(),
            "workspace_snapshot",
            "Workspace snapshot",
            content,
            $"files={snapshot.FileCount} solutions={snapshot.SolutionCount} projects={snapshot.ProjectCount} scanner={snapshot.ScannerVersion}");
        WriteArtifactFile(workspaceRoot, artifact.RelativePath, content);
        return artifact;
    }

    public ArtifactRecord SaveWorkspaceProjectGraphArtifact(RamDbService ramDbService, string workspaceRoot, WorkspaceProjectGraphRecord graph)
    {
        var content = JsonSerializer.Serialize(graph, JsonOptions);
        var artifact = SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildWorkspaceProjectGraphPath(),
            "workspace_project_graph",
            "Workspace project graph",
            content,
            $"solutions={graph.SolutionCount} projects={graph.ProjectCount} references={graph.ReferenceCount} inventory={graph.InventoryVersion}");
        WriteArtifactFile(workspaceRoot, artifact.RelativePath, content);
        return artifact;
    }

    public ArtifactRecord SaveWorkspacePreparationStateArtifact(RamDbService ramDbService, string workspaceRoot, WorkspacePreparationStateRecord state)
    {
        var content = JsonSerializer.Serialize(state, JsonOptions);
        var artifact = SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildWorkspacePreparationStatePath(),
            "workspace_preparation_state",
            "Workspace preparation state",
            content,
            $"status={state.PreparationStatus} sync={state.SyncStatus} chunks={state.ChunkCount} indexed_files={state.IndexedFileCount}");
        WriteArtifactFile(workspaceRoot, artifact.RelativePath, content);
        return artifact;
    }

    public ArtifactRecord SaveWorkspaceRetrievalCatalogArtifact(RamDbService ramDbService, string workspaceRoot, WorkspaceRetrievalCatalogRecord catalog)
    {
        var content = JsonSerializer.Serialize(catalog, JsonOptions);
        var artifact = SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildWorkspaceRetrievalCatalogPath(),
            "workspace_retrieval_catalog",
            "Workspace retrieval catalog",
            content,
            $"chunks={catalog.ChunkCount} indexed_files={catalog.IndexedFileCount} skipped_files={catalog.SkippedFileCount}");
        WriteArtifactFile(workspaceRoot, artifact.RelativePath, content);
        return artifact;
    }

    public ArtifactRecord SaveWorkspaceRetrievalDeltaArtifact(RamDbService ramDbService, string workspaceRoot, WorkspaceRetrievalDeltaRecord delta)
    {
        var content = JsonSerializer.Serialize(delta, JsonOptions);
        var artifact = SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildWorkspaceRetrievalDeltaPath(),
            "workspace_retrieval_delta",
            "Workspace retrieval delta",
            content,
            $"added={delta.AddedCount} changed={delta.ChangedCount} removed={delta.RemovedCount} unchanged={delta.UnchangedCount}");
        WriteArtifactFile(workspaceRoot, artifact.RelativePath, content);
        return artifact;
    }

    public ArtifactRecord SaveWorkspaceRetrievalSyncResultArtifact(RamDbService ramDbService, string workspaceRoot, WorkspaceRetrievalSyncResultRecord syncResult)
    {
        var content = JsonSerializer.Serialize(syncResult, JsonOptions);
        var artifact = SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildWorkspaceRetrievalSyncResultPath(),
            "workspace_retrieval_sync_result",
            "Workspace retrieval sync result",
            content,
            $"mode={syncResult.ExecutionMode} status={syncResult.SyncStatus} planned_upserts={syncResult.PlannedUpsertCount} applied_upserts={syncResult.AppliedUpsertCount} planned_deletes={syncResult.PlannedDeleteCount} applied_deletes={syncResult.AppliedDeleteCount}");
        WriteArtifactFile(workspaceRoot, artifact.RelativePath, content);
        return artifact;
    }

    public ArtifactRecord SaveLaneCoverageMapArtifact(RamDbService ramDbService, string workspaceRoot, TaskboardLaneCoverageMap coverageMap)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildLaneCoverageMapPath(coverageMap.PlanImportId),
            "taskboard_lane_coverage_map",
            $"Taskboard lane coverage: {coverageMap.PlanTitle}",
            JsonSerializer.Serialize(coverageMap, JsonOptions),
            $"current_family={coverageMap.CurrentWorkFamily} next_family={coverageMap.NextWorkFamily} entries={coverageMap.Entries.Count} runtime={coverageMap.RuntimeStateStatusCode}");
    }

    public ArtifactRecord SavePostChainReconciliationArtifact(RamDbService ramDbService, string workspaceRoot, string importId, TaskboardPostChainReconciliationRecord record)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildPostChainReconciliationPath(importId, record.SuccessfulWorkItemId),
            "taskboard_post_chain_reconciliation",
            $"Taskboard post-chain reconciliation: {record.SuccessfulWorkItemTitle}",
            JsonSerializer.Serialize(record, JsonOptions),
            $"successful_work_item={record.SuccessfulWorkItemId} followup_work_item={record.FollowupWorkItemId} followup_blocker={record.FollowupBlockerCode}");
    }

    public ArtifactRecord SaveFollowUpWorkItemArtifact(RamDbService ramDbService, string workspaceRoot, string importId, TaskboardFollowUpWorkItemSelectionRecord record)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildFollowUpWorkItemPath(importId, record.WorkItemId),
            "taskboard_followup_work_item",
            string.IsNullOrWhiteSpace(record.WorkItemTitle)
                ? "Taskboard follow-up work item"
                : $"Taskboard follow-up work item: {record.WorkItemTitle}",
            JsonSerializer.Serialize(record, JsonOptions),
            $"work_item={record.WorkItemId} batch={record.BatchId} reason={record.SelectionReason}");
    }

    public ArtifactRecord SaveFollowUpResolutionArtifact(RamDbService ramDbService, string workspaceRoot, string importId, TaskboardFollowUpWorkItemResolutionRecord record)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildFollowUpResolutionPath(importId, record.WorkItemId),
            "taskboard_followup_resolution",
            string.IsNullOrWhiteSpace(record.WorkItemTitle)
                ? "Taskboard follow-up resolution"
                : $"Taskboard follow-up resolution: {record.WorkItemTitle}",
            JsonSerializer.Serialize(record, JsonOptions),
            $"work_item={record.WorkItemId} family={record.WorkFamily} phrase={record.PhraseFamily} blocker={record.LaneBlockerCode}");
    }

    public ArtifactRecord SaveFinalBlockerAssignmentArtifact(RamDbService ramDbService, string workspaceRoot, string importId, TaskboardFinalBlockerAssignmentRecord record)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildFinalBlockerAssignmentPath(importId, record.WorkItemId),
            "taskboard_final_blocker_assignment",
            $"Taskboard final blocker: {record.WorkItemTitle}",
            JsonSerializer.Serialize(record, JsonOptions),
            $"work_item={record.WorkItemId} lane_blocker={record.LaneBlockerCode} goal_blocker={record.GoalBlockerCode} origin={record.BlockerOrigin} phase={record.BlockerPhase}");
    }

    public ArtifactRecord SaveLiveRunEntryArtifact(RamDbService ramDbService, string workspaceRoot, TaskboardLiveRunEntryRecord record)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildLiveRunEntryPath(record.EntryId),
            "taskboard_live_run_entry",
            $"Taskboard live run entry: {record.ActionName}",
            JsonSerializer.Serialize(record, JsonOptions),
            $"entry_path={record.EntryPath} selected_state={record.SelectedImportState.ToLowerInvariant()} activation_handoff={record.ActivationHandoffPerformed.ToString().ToLowerInvariant()}");
    }

    public ArtifactRecord SaveActivationHandoffArtifact(RamDbService ramDbService, string workspaceRoot, TaskboardActivationHandoffRecord record)
    {
        return SaveOrUpdateArtifact(
            ramDbService,
            workspaceRoot,
            BuildActivationHandoffPath(record.HandoffId),
            "taskboard_activation_handoff",
            $"Taskboard activation handoff: {record.ActionName}",
            JsonSerializer.Serialize(record, JsonOptions),
            $"status={record.StatusCode} success={record.Success.ToString().ToLowerInvariant()}");
    }

    public List<TaskboardImportRecord> LoadImports(RamDbService ramDbService, string workspaceRoot, int maxCount = 40)
    {
        return ramDbService.LoadArtifactsByType(workspaceRoot, "taskboard_import", maxCount)
            .Select(TryDeserializeImportRecord)
            .Where(record => record is not null)
            .Cast<TaskboardImportRecord>()
            .ToList();
    }

    public TaskboardImportRecord? LoadImportRecord(RamDbService ramDbService, string workspaceRoot, string importId)
    {
        if (string.IsNullOrWhiteSpace(importId))
            return null;

        var artifact = ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, BuildImportRecordPath(importId));
        return TryDeserializeImportRecord(artifact);
    }

    public TaskboardDocument? LoadPlan(RamDbService ramDbService, string workspaceRoot, TaskboardImportRecord record)
    {
        var artifact = record.PlanArtifactId > 0
            ? ramDbService.LoadArtifactById(workspaceRoot, record.PlanArtifactId)
            : ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, BuildPlanArtifactPath(record.ImportId));
        return TryDeserialize<TaskboardDocument>(artifact);
    }

    public TaskboardValidationReport? LoadValidation(RamDbService ramDbService, string workspaceRoot, TaskboardImportRecord record)
    {
        var artifact = record.ValidationArtifactId > 0
            ? ramDbService.LoadArtifactById(workspaceRoot, record.ValidationArtifactId)
            : ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, BuildValidationArtifactPath(record.ImportId));
        return TryDeserialize<TaskboardValidationReport>(artifact);
    }

    public string LoadRawText(RamDbService ramDbService, string workspaceRoot, TaskboardImportRecord record)
    {
        var artifact = record.RawArtifactId > 0
            ? ramDbService.LoadArtifactById(workspaceRoot, record.RawArtifactId)
            : ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, BuildRawArtifactPath(record.ImportId));
        return artifact?.Content ?? "";
    }

    public string LoadParsedJson(RamDbService ramDbService, string workspaceRoot, TaskboardImportRecord record)
    {
        var artifact = record.ParsedArtifactId > 0
            ? ramDbService.LoadArtifactById(workspaceRoot, record.ParsedArtifactId)
            : ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, BuildParsedArtifactPath(record.ImportId));
        return artifact?.Content ?? "";
    }

    public string LoadValidationJson(RamDbService ramDbService, string workspaceRoot, TaskboardImportRecord record)
    {
        var artifact = record.ValidationArtifactId > 0
            ? ramDbService.LoadArtifactById(workspaceRoot, record.ValidationArtifactId)
            : ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, BuildValidationArtifactPath(record.ImportId));
        return artifact?.Content ?? "";
    }

    public TaskboardPlanRunStateRecord? LoadRunState(RamDbService ramDbService, string workspaceRoot, TaskboardImportRecord record)
    {
        var artifact = ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, BuildRunStatePath(record.ImportId));
        return TryDeserialize<TaskboardPlanRunStateRecord>(artifact);
    }

    public TaskboardRunTerminalSummaryRecord? LoadLatestRunSummary(RamDbService ramDbService, string workspaceRoot, TaskboardImportRecord record)
    {
        return ramDbService.LoadArtifactsByType(workspaceRoot, "taskboard_run_summary", 40)
            .Select(TryDeserialize<TaskboardRunTerminalSummaryRecord>)
            .FirstOrDefault(summary =>
                summary is not null
                && string.Equals(summary.PlanImportId, record.ImportId, StringComparison.OrdinalIgnoreCase));
    }

    public TaskboardWorkItemDecompositionRecord? LoadDecomposition(RamDbService ramDbService, string workspaceRoot, string importId, string workItemId)
    {
        var artifact = ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, BuildDecompositionPath(importId, workItemId));
        return TryDeserialize<TaskboardWorkItemDecompositionRecord>(artifact);
    }

    public TaskboardPhraseFamilyResolutionRecord? LoadPhraseFamilyResolution(RamDbService ramDbService, string workspaceRoot, string importId, string workItemId)
    {
        var artifact = ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, BuildPhraseFamilyResolutionPath(importId, workItemId));
        return TryDeserialize<TaskboardPhraseFamilyResolutionRecord>(artifact);
    }

    public CommandCanonicalizationRecord? LoadCommandNormalization(RamDbService ramDbService, string workspaceRoot, string importId, string workItemId)
    {
        var artifact = ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, BuildCommandNormalizationPath(importId, workItemId));
        return TryDeserialize<CommandCanonicalizationRecord>(artifact);
    }

    public TaskboardExecutionGoalResolution? LoadExecutionGoal(RamDbService ramDbService, string workspaceRoot, string importId, string workItemId)
    {
        var artifact = ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, BuildExecutionGoalPath(importId, workItemId));
        return TryDeserialize<TaskboardExecutionGoalResolution>(artifact);
    }

    public TaskboardExecutionLaneResolution? LoadExecutionLane(RamDbService ramDbService, string workspaceRoot, string importId, string workItemId)
    {
        var artifact = ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, BuildExecutionLanePath(importId, workItemId));
        return TryDeserialize<TaskboardExecutionLaneResolution>(artifact);
    }

    public TaskboardLaneCoverageMap? LoadLaneCoverageMap(RamDbService ramDbService, string workspaceRoot, string importId)
    {
        var artifact = ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, BuildLaneCoverageMapPath(importId));
        return TryDeserialize<TaskboardLaneCoverageMap>(artifact);
    }

    public WorkspaceSnapshotRecord? LoadWorkspaceSnapshot(RamDbService ramDbService, string workspaceRoot)
    {
        return LoadWorkspaceArtifactWithFileFallback<WorkspaceSnapshotRecord>(ramDbService, workspaceRoot, BuildWorkspaceSnapshotPath());
    }

    public WorkspaceProjectGraphRecord? LoadWorkspaceProjectGraph(RamDbService ramDbService, string workspaceRoot)
    {
        return LoadWorkspaceArtifactWithFileFallback<WorkspaceProjectGraphRecord>(ramDbService, workspaceRoot, BuildWorkspaceProjectGraphPath());
    }

    public WorkspacePreparationStateRecord? LoadWorkspacePreparationState(RamDbService ramDbService, string workspaceRoot)
    {
        return LoadWorkspaceArtifactWithFileFallback<WorkspacePreparationStateRecord>(ramDbService, workspaceRoot, BuildWorkspacePreparationStatePath());
    }

    public WorkspaceRetrievalCatalogRecord? LoadWorkspaceRetrievalCatalog(RamDbService ramDbService, string workspaceRoot)
    {
        return LoadWorkspaceArtifactWithFileFallback<WorkspaceRetrievalCatalogRecord>(ramDbService, workspaceRoot, BuildWorkspaceRetrievalCatalogPath());
    }

    public WorkspaceRetrievalDeltaRecord? LoadWorkspaceRetrievalDelta(RamDbService ramDbService, string workspaceRoot)
    {
        return LoadWorkspaceArtifactWithFileFallback<WorkspaceRetrievalDeltaRecord>(ramDbService, workspaceRoot, BuildWorkspaceRetrievalDeltaPath());
    }

    public WorkspaceRetrievalSyncResultRecord? LoadWorkspaceRetrievalSyncResult(RamDbService ramDbService, string workspaceRoot)
    {
        return LoadWorkspaceArtifactWithFileFallback<WorkspaceRetrievalSyncResultRecord>(ramDbService, workspaceRoot, BuildWorkspaceRetrievalSyncResultPath());
    }

    private static ArtifactRecord SaveOrUpdateArtifact(
        RamDbService ramDbService,
        string workspaceRoot,
        string relativePath,
        string artifactType,
        string title,
        string content,
        string summary)
    {
        var existing = ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existing ?? new ArtifactRecord();
        artifact.ArtifactType = artifactType;
        artifact.Title = title;
        artifact.RelativePath = relativePath;
        artifact.Content = content ?? "";
        artifact.Summary = summary ?? "";

        if (existing is null)
            return ramDbService.SaveArtifact(workspaceRoot, artifact);

        ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private static void WriteArtifactFile(string workspaceRoot, string relativePath, string content)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(relativePath))
            return;

        var fullPath = Path.Combine(
            workspaceRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(fullPath, content ?? "");
    }

    private static TaskboardImportRecord? TryDeserializeImportRecord(ArtifactRecord? artifact)
    {
        return TryDeserialize<TaskboardImportRecord>(artifact);
    }

    private static T? TryDeserialize<T>(ArtifactRecord? artifact)
    {
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.Content))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(artifact.Content);
        }
        catch
        {
            return default;
        }
    }

    private static T? LoadWorkspaceArtifactWithFileFallback<T>(RamDbService ramDbService, string workspaceRoot, string relativePath)
    {
        try
        {
            var artifact = ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
            var record = TryDeserialize<T>(artifact);
            if (record is not null)
                return record;
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(relativePath))
            return default;

        try
        {
            var fullPath = Path.Combine(
                workspaceRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                return default;

            var content = File.ReadAllText(fullPath);
            return string.IsNullOrWhiteSpace(content)
                ? default
                : JsonSerializer.Deserialize<T>(content, JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    public static string BuildImportRecordPath(string importId) => $".ram/taskboards/imports/{importId}.json";
    public static string BuildRawArtifactPath(string importId) => $".ram/taskboards/{importId}/raw.md";
    public static string BuildParsedArtifactPath(string importId) => $".ram/taskboards/{importId}/parsed.json";
    public static string BuildPlanArtifactPath(string importId) => $".ram/taskboards/{importId}/plan.json";
    public static string BuildValidationArtifactPath(string importId) => $".ram/taskboards/{importId}/validation.json";
    public static string BuildRunStatePath(string importId) => $".ram/taskboards/{importId}/run-state.json";
    public static string BuildRunSummaryPath(string importId, string summaryId) => $".ram/taskboards/{importId}/run-summaries/{summaryId}.json";
    public static string BuildNormalizedRunPath(string importId, string recordId) => $".ram/taskboards/{importId}/run-records/{recordId}.json";
    public static string BuildIndexExportPath(string importId, string exportId) => $".ram/taskboards/{importId}/index/{exportId}.json";
    public static string BuildCorpusExportPath(string importId, string exportId) => $".ram/taskboards/{importId}/corpus/{exportId}.json";
    public static string BuildRunProjectionPath(string runId) => $".ram/taskboards/runs/{runId}.json";
    public static string BuildDecompositionPath(string importId, string workItemId) => $".ram/taskboards/{importId}/decompositions/{workItemId}.json";
    public static string BuildPhraseFamilyResolutionPath(string importId, string workItemId) => $".ram/taskboards/{importId}/phrase-family/{workItemId}.json";
    public static string BuildCommandNormalizationPath(string importId, string workItemId) => $".ram/taskboards/{importId}/command-normalization/{workItemId}.json";
    public static string BuildExecutionGoalPath(string importId, string workItemId) => $".ram/taskboards/{importId}/goals/{workItemId}.json";
    public static string BuildExecutionLanePath(string importId, string workItemId) => $".ram/taskboards/{importId}/lanes/{workItemId}.json";
    public static string BuildLaneCoverageMapPath(string importId) => $".ram/taskboards/{importId}/lane-coverage.json";
    public static string BuildWorkspaceSnapshotPath() => ".ram/workspace/workspace_snapshot.json";
    public static string BuildWorkspaceProjectGraphPath() => ".ram/workspace/workspace_project_graph.json";
    public static string BuildWorkspacePreparationStatePath() => ".ram/workspace/workspace_preparation_state.json";
    public static string BuildWorkspaceRetrievalCatalogPath() => ".ram/workspace/workspace_retrieval_catalog.json";
    public static string BuildWorkspaceRetrievalDeltaPath() => ".ram/workspace/workspace_retrieval_delta.json";
    public static string BuildWorkspaceRetrievalSyncResultPath() => ".ram/workspace/workspace_retrieval_sync_result.json";
    public static string BuildPostChainReconciliationPath(string importId, string workItemId) => $".ram/taskboards/{importId}/post-chain/{workItemId}.json";
    public static string BuildFollowUpWorkItemPath(string importId, string workItemId) => $".ram/taskboards/{importId}/followup/{NormalizeArtifactSegment(workItemId)}-selection.json";
    public static string BuildFollowUpResolutionPath(string importId, string workItemId) => $".ram/taskboards/{importId}/followup/{NormalizeArtifactSegment(workItemId)}-resolution.json";
    public static string BuildFinalBlockerAssignmentPath(string importId, string workItemId) => $".ram/taskboards/{importId}/blockers/{workItemId}.json";
    public static string BuildLiveRunEntryPath(string entryId) => $".ram/taskboards/runs/live-entry-{entryId}.json";
    public static string BuildActivationHandoffPath(string handoffId) => $".ram/taskboards/runs/activation-handoff-{handoffId}.json";

    private static string NormalizeArtifactSegment(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "_none_" : value.Trim();
    }
}
