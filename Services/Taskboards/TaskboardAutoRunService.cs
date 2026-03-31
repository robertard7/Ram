using System.IO;
using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardAutoRunService
{
    private readonly TaskboardArtifactStore _artifactStore = new();
    private readonly TaskboardBuildFailureRecoveryService _buildFailureRecoveryService = new();
    private readonly TaskboardExecutionBridgeService _executionBridgeService = new();
    private readonly TaskboardFinalBlockerAssignmentService _finalBlockerAssignmentService = new();
    private readonly TaskboardLaneCoverageMapService _laneCoverageMapService = new();
    private readonly TaskboardMaintenanceBaselineService _maintenanceBaselineService = new();
    private readonly TaskboardPostChainReconciliationService _postChainReconciliationService = new();
    private readonly TaskboardRuntimeStateFingerprintService _runtimeStateFingerprintService = new();
    private readonly TaskboardRunProjectionService _runProjectionService = new();
    private readonly TaskboardRunDataFoundationService _runDataFoundationService = new();
    private readonly TaskboardRunSummaryService _runSummaryService = new();
    private readonly TaskboardStateSatisfactionService _stateSatisfactionService = new();
    private readonly TaskboardWorkItemStateRefreshService _workItemStateRefreshService = new();
    private readonly WorkspaceStructuralTruthService _workspaceStructuralTruthService = new();
    private readonly BuilderWorkItemDecompositionService _builderWorkItemDecompositionService;
    private readonly ForensicsAgentService? _forensicsAgentService;
    private readonly TaskboardWorkFamilyResolutionService _workFamilyResolutionService = new();

    public TaskboardAutoRunService(
        BuilderWorkItemDecompositionService? builderWorkItemDecompositionService = null,
        ForensicsAgentService? forensicsAgentService = null)
    {
        _builderWorkItemDecompositionService = builderWorkItemDecompositionService ?? new BuilderWorkItemDecompositionService();
        _forensicsAgentService = forensicsAgentService;
    }

    public async Task<TaskboardAutoRunResult> RunAsync(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardDocument activeDocument,
        string actionName,
        string selectedBatchId,
        string activeTargetRelativePath,
        RamDbService ramDbService,
        Func<TaskboardExecutionBridgeResult, Task<TaskboardExecutionOutcome>> executor,
        bool allowRetryCurrent = false,
        AppSettings? settings = null,
        string endpoint = "",
        string selectedModel = "",
        TaskboardLiveRunEntryContext? liveRunEntryContext = null,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(
            workspaceRoot,
            activeImport,
            activeDocument,
            actionName,
            selectedBatchId,
            activeTargetRelativePath,
            ramDbService,
            (bridge, _) => executor(bridge),
            allowRetryCurrent,
            settings,
            endpoint,
            selectedModel,
            liveRunEntryContext,
            cancellationToken,
            liveProgressObserver: null);
    }

    public async Task<TaskboardAutoRunResult> RunAsync(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardDocument activeDocument,
        string actionName,
        string selectedBatchId,
        string activeTargetRelativePath,
        RamDbService ramDbService,
        Func<TaskboardExecutionBridgeResult, Func<TaskboardLiveProgressUpdate, Task>?, Task<TaskboardExecutionOutcome>> executor,
        bool allowRetryCurrent = false,
        AppSettings? settings = null,
        string endpoint = "",
        string selectedModel = "",
        TaskboardLiveRunEntryContext? liveRunEntryContext = null,
        CancellationToken cancellationToken = default,
        Func<TaskboardLiveProgressUpdate, Task>? liveProgressObserver = null)
    {
        var result = new TaskboardAutoRunResult
        {
            ActionName = actionName
        };

        var runState = LoadOrCreateRunState(
            workspaceRoot,
            activeImport,
            activeDocument,
            ramDbService,
            forceFreshRuntimeState: liveRunEntryContext is not null || allowRetryCurrent);
        result.RunState = runState;
        var executionState = ramDbService.LoadExecutionState(workspaceRoot);
        var maintenanceBaseline = _maintenanceBaselineService.Resolve(workspaceRoot, activeDocument, executionState);
        ApplyMaintenanceBaselineState(runState, maintenanceBaseline);
        var effectiveActiveTargetRelativePath = maintenanceBaseline.IsMaintenanceMode && maintenanceBaseline.BaselineResolved
            ? FirstNonEmpty(maintenanceBaseline.PrimarySolutionPath, activeTargetRelativePath)
            : activeTargetRelativePath;

        var stateChanged = false;
        var executionOccurred = false;

        TryCaptureWorkspaceStructuralTruth(workspaceRoot, ramDbService);

        async Task SaveAndNotifyAsync(TaskboardLiveProgressUpdate? update = null, bool refreshCoverage = false)
        {
            if (update is not null)
            {
                ApplyLiveProgressUpdate(runState, update);
                if (!string.IsNullOrWhiteSpace(update.EventKind))
                {
                    AddEvent(
                        runState,
                        update.EventKind,
                        FirstNonEmpty(update.BatchId, runState.CurrentBatchId),
                        FirstNonEmpty(update.WorkItemId, runState.CurrentWorkItemId),
                        FirstNonEmpty(update.ActivitySummary, update.PhaseText));
                }
            }

            if (refreshCoverage)
                UpdateCoverageState(workspaceRoot, activeImport, runState, ramDbService);

            SaveRunState(workspaceRoot, runState, ramDbService);

            if (update is not null && liveProgressObserver is not null)
                await liveProgressObserver(update);
        }

        if (liveRunEntryContext is not null)
        {
            ApplyLiveRunEntry(runState, liveRunEntryContext);
            await SaveAndNotifyAsync(
                new TaskboardLiveProgressUpdate
                {
                    PhaseCode = runState.CurrentRunPhaseCode,
                    PhaseText = runState.CurrentRunPhaseText,
                    ActivitySummary = runState.LatestStepSummary,
                    BatchId = liveRunEntryContext.SelectedBatchId
                },
                refreshCoverage: true);
            stateChanged = true;
        }

        if (allowRetryCurrent && ResetRetryableCurrentItem(runState, selectedBatchId))
        {
            stateChanged = true;
            await SaveAndNotifyAsync(
                new TaskboardLiveProgressUpdate
                {
                    PhaseCode = runState.CurrentRunPhaseCode,
                    PhaseText = runState.CurrentRunPhaseText,
                    ActivitySummary = runState.LatestStepSummary
                },
                refreshCoverage: true);
        }

        while (true)
        {
            NormalizeCompletionState(runState);

            if (ApplyStructuralSupportCoverage(runState))
            {
                stateChanged = true;
                await SaveAndNotifyAsync(
                    new TaskboardLiveProgressUpdate
                    {
                        PhaseCode = runState.CurrentRunPhaseCode,
                        PhaseText = runState.CurrentRunPhaseText,
                        ActivitySummary = FirstNonEmpty(runState.LastSupportCoverageSummary, "Covered structural support headings without standalone execution.")
                    },
                    refreshCoverage: true);
                continue;
            }

            if (ApplyContradictionGuardCoverage(workspaceRoot, runState))
            {
                stateChanged = true;
                await SaveAndNotifyAsync(
                    new TaskboardLiveProgressUpdate
                    {
                        PhaseCode = runState.CurrentRunPhaseCode,
                        PhaseText = runState.CurrentRunPhaseText,
                        ActivitySummary = FirstNonEmpty(runState.LastContradictionGuardSummary, "Blocked contradictory standalone work from reintroducing a resolved defect.")
                    },
                    refreshCoverage: true);
                continue;
            }

            if (ApplyRepeatedGenerationRejectionCoverage(runState))
            {
                stateChanged = true;
                await SaveAndNotifyAsync(
                    new TaskboardLiveProgressUpdate
                    {
                        PhaseCode = runState.CurrentRunPhaseCode,
                        PhaseText = runState.CurrentRunPhaseText,
                        ActivitySummary = FirstNonEmpty(runState.LatestStepSummary, "Suppressed repeated thin-generation rejection without a stronger path.")
                    },
                    refreshCoverage: true);
                continue;
            }

            if (RepairInvalidReopenedSetupWorkItems(runState, selectedBatchId))
            {
                stateChanged = true;
                await SaveAndNotifyAsync(
                    new TaskboardLiveProgressUpdate
                    {
                        PhaseCode = runState.CurrentRunPhaseCode,
                        PhaseText = runState.CurrentRunPhaseText,
                        ActivitySummary = FirstNonEmpty(runState.LatestStepSummary, "Restored stale setup state that should not have been reopened.")
                    },
                    refreshCoverage: true);
            }

            if (ReopenUnsatisfiedCompletedSetupWorkItems(workspaceRoot, runState, selectedBatchId, ramDbService))
            {
                stateChanged = true;
                await SaveAndNotifyAsync(
                    new TaskboardLiveProgressUpdate
                    {
                        PhaseCode = runState.CurrentRunPhaseCode,
                        PhaseText = runState.CurrentRunPhaseText,
                        ActivitySummary = FirstNonEmpty(runState.LatestStepSummary, "Reopened a completed setup step because the workspace is still missing required project setup state.")
                    },
                    refreshCoverage: true);
            }

            var selection = TrySelectNextRunnable(runState, selectedBatchId);
            if (!selection.Success)
            {
                result.RunState = runState;
                result.Success = runState.PlanStatus == TaskboardPlanRuntimeStatus.Completed;
                result.WasSkipped = !result.Success;
                result.StateChanged = stateChanged;
                result.ExecutionOccurred = executionOccurred;
                result.CompletedPlan = runState.PlanStatus == TaskboardPlanRuntimeStatus.Completed;
                result.StatusCode = selection.StatusCode;
                result.Message = selection.Message;
                FinalizeTerminalSummary(
                    workspaceRoot,
                    activeImport,
                    runState,
                    ramDbService,
                    actionName,
                    selectedBatchId,
                    result.StatusCode,
                    result.Message);
                return result;
            }

            var batch = activeDocument.Batches.First(current =>
                string.Equals(current.BatchId, selection.Batch.BatchId, StringComparison.OrdinalIgnoreCase));

            RefreshSelectedWorkItem(runState, selection.Batch, selection.WorkItem);
            await SaveAndNotifyAsync(
                new TaskboardLiveProgressUpdate
                {
                    PhaseCode = "selecting_work_item",
                    PhaseText = $"Selecting work item `{selection.WorkItem.Title}`.",
                    ActivitySummary = $"Selected next work item: {selection.WorkItem.Title}",
                    BatchId = selection.Batch.BatchId,
                    WorkItemId = selection.WorkItem.WorkItemId,
                    WorkItemTitle = selection.WorkItem.Title
                });

            if (!selection.WorkItem.IsDecomposedItem)
            {
                await SaveAndNotifyAsync(
                    new TaskboardLiveProgressUpdate
                    {
                        PhaseCode = "resolving_phrase_family",
                        PhaseText = $"Resolving decomposition for `{selection.WorkItem.Title}`.",
                        ActivitySummary = $"Resolving decomposition for {selection.WorkItem.Title}.",
                        BatchId = selection.Batch.BatchId,
                        WorkItemId = selection.WorkItem.WorkItemId,
                        WorkItemTitle = selection.WorkItem.Title
                    });

                var decomposition = await _builderWorkItemDecompositionService.DecomposeAsync(
                    workspaceRoot,
                    activeImport,
                    activeDocument,
                    batch,
                    BuildRunWorkItem(selection.WorkItem),
                    ramDbService,
                    settings,
                    endpoint,
                    selectedModel,
                    cancellationToken,
                    runState);

                if (decomposition.Disposition == TaskboardWorkItemDecompositionDisposition.Decomposed)
                {
                ApplyDecomposition(runState, selection.Batch, selection.WorkItem, decomposition);
                RefreshWorkFamilies(selection.Batch);
                if (!string.IsNullOrWhiteSpace(decomposition.PhraseFamilyResolution.WorkItemId))
                    _artifactStore.SaveCommandNormalizationArtifact(ramDbService, workspaceRoot, activeImport.ImportId, BuildCommandNormalizationRecord(workspaceRoot, activeImport.ImportId, batch.BatchId, decomposition));
                if (!string.IsNullOrWhiteSpace(decomposition.PhraseFamilyResolution.WorkItemId))
                    _artifactStore.SavePhraseFamilyResolutionArtifact(ramDbService, workspaceRoot, activeImport.ImportId, decomposition.PhraseFamilyResolution);
                _artifactStore.SaveDecompositionArtifact(ramDbService, workspaceRoot, decomposition);
                    await SaveAndNotifyAsync(
                        new TaskboardLiveProgressUpdate
                        {
                            PhaseCode = runState.CurrentRunPhaseCode,
                            PhaseText = runState.CurrentRunPhaseText,
                            ActivitySummary = runState.LatestStepSummary,
                            BatchId = selection.Batch.BatchId,
                            WorkItemId = selection.WorkItem.WorkItemId,
                            WorkItemTitle = selection.WorkItem.Title
                        },
                        refreshCoverage: true);
                    result.RunState = runState;
                    result.StateChanged = true;
                    result.StatusCode = "decomposed";
                    result.Message = decomposition.Reason;
                    continue;
                }

                if (decomposition.Disposition == TaskboardWorkItemDecompositionDisposition.Covered)
                {
                    if (!string.IsNullOrWhiteSpace(decomposition.PhraseFamilyResolution.WorkItemId))
                        _artifactStore.SaveCommandNormalizationArtifact(ramDbService, workspaceRoot, activeImport.ImportId, BuildCommandNormalizationRecord(workspaceRoot, activeImport.ImportId, batch.BatchId, decomposition));
                    if (!string.IsNullOrWhiteSpace(decomposition.PhraseFamilyResolution.WorkItemId))
                        _artifactStore.SavePhraseFamilyResolutionArtifact(ramDbService, workspaceRoot, activeImport.ImportId, decomposition.PhraseFamilyResolution);
                    _artifactStore.SaveDecompositionArtifact(ramDbService, workspaceRoot, decomposition);
                    ApplyCoveredStructuralSupport(
                        runState,
                        selection.Batch,
                        selection.WorkItem,
                        TaskboardStructuralHeadingService.Classify(selection.WorkItem.Title),
                        decomposition.Reason);
                    RefreshWorkFamilies(selection.Batch);
                    await SaveAndNotifyAsync(
                        new TaskboardLiveProgressUpdate
                        {
                            PhaseCode = runState.CurrentRunPhaseCode,
                            PhaseText = runState.CurrentRunPhaseText,
                            ActivitySummary = runState.LatestStepSummary,
                            BatchId = selection.Batch.BatchId,
                            WorkItemId = selection.WorkItem.WorkItemId,
                            WorkItemTitle = selection.WorkItem.Title
                        },
                        refreshCoverage: true);
                    result.RunState = runState;
                    result.StateChanged = true;
                    result.StatusCode = "covered_support_heading";
                    result.Message = decomposition.Reason;
                    continue;
                }

                if (decomposition.Disposition == TaskboardWorkItemDecompositionDisposition.Blocked)
                {
                    if (!string.IsNullOrWhiteSpace(decomposition.PhraseFamilyResolution.WorkItemId))
                        _artifactStore.SaveCommandNormalizationArtifact(ramDbService, workspaceRoot, activeImport.ImportId, BuildCommandNormalizationRecord(workspaceRoot, activeImport.ImportId, batch.BatchId, decomposition));
                    if (!string.IsNullOrWhiteSpace(decomposition.PhraseFamilyResolution.WorkItemId))
                        _artifactStore.SavePhraseFamilyResolutionArtifact(ramDbService, workspaceRoot, activeImport.ImportId, decomposition.PhraseFamilyResolution);
                    _artifactStore.SaveDecompositionArtifact(ramDbService, workspaceRoot, decomposition);
                    runState.LastResolvedBuildProfile = decomposition.BuildProfile;
                    runState.LastDecompositionWorkItemId = decomposition.OriginalWorkItemId;
                    runState.LastDecompositionSummary = decomposition.Reason;
                    ApplyPhraseFamilyTrace(runState, decomposition);
                    runState.LastExecutionGoalResolution = new TaskboardExecutionGoalResolution();
                    runState.LastExecutionGoalSummary = "";
                    runState.LastExecutionGoalBlockerCode = "";

                    var decompositionOutcome = new TaskboardExecutionOutcome
                    {
                        ResultKind = TaskboardWorkItemResultKind.Blocked,
                        ResultClassification = "decomposition_blocked",
                        Summary = decomposition.Reason
                    };

                    ApplyExecutionOutcome(runState, selection.Batch, selection.WorkItem, decompositionOutcome);
                    AssignCurrentBlocker(workspaceRoot, activeImport, runState, selection.Batch, selection.WorkItem, "decomposition_blocked", decompositionOutcome.Summary, ramDbService);
                    await SaveAndNotifyAsync(
                        new TaskboardLiveProgressUpdate
                        {
                            PhaseCode = runState.CurrentRunPhaseCode,
                            PhaseText = runState.CurrentRunPhaseText,
                            ActivitySummary = runState.LatestStepSummary,
                            BatchId = selection.Batch.BatchId,
                            WorkItemId = selection.WorkItem.WorkItemId,
                            WorkItemTitle = selection.WorkItem.Title
                        },
                        refreshCoverage: true);

                    result.RunState = runState;
                    result.StateChanged = true;
                    result.StatusCode = decompositionOutcome.ResultClassification;
                    result.Message = BuildTerminalMessage(actionName, runState, decompositionOutcome.Summary, selectedBatchId);
                    FinalizeTerminalSummary(
                        workspaceRoot,
                        activeImport,
                        runState,
                        ramDbService,
                        actionName,
                        selectedBatchId,
                        result.StatusCode,
                        result.Message);
                    return result;
                }
            }

            var persistedRepairContinuation = TryResumeRepairContinuation(
                workspaceRoot,
                runState,
                selection.WorkItem,
                ramDbService);
            if (persistedRepairContinuation.OverrideOutcome is not null)
            {
                ApplyExecutionOutcome(runState, selection.Batch, selection.WorkItem, persistedRepairContinuation.OverrideOutcome);
                ReconcilePostSuccess(workspaceRoot, activeImport, runState, selection.Batch, selection.WorkItem, ramDbService);
                await SaveAndNotifyAsync(
                    new TaskboardLiveProgressUpdate
                    {
                        PhaseCode = persistedRepairContinuation.PhaseCode,
                        PhaseText = persistedRepairContinuation.PhaseText,
                        EventKind = persistedRepairContinuation.EventKind,
                        ActivitySummary = persistedRepairContinuation.Summary,
                        BatchId = selection.Batch.BatchId,
                        WorkItemId = selection.WorkItem.WorkItemId,
                        WorkItemTitle = selection.WorkItem.Title
                    },
                    refreshCoverage: true);

                result.RunState = runState;
                result.StateChanged = true;
                continue;
            }

            if (persistedRepairContinuation.ShouldContinueCurrentItem)
            {
                selection.WorkItem.DirectToolRequest = persistedRepairContinuation.NextToolRequest?.Clone();
                selection.WorkItem.UpdatedUtc = DateTime.UtcNow.ToString("O");
                runState.CurrentRunPhaseCode = persistedRepairContinuation.PhaseCode;
                runState.CurrentRunPhaseText = persistedRepairContinuation.PhaseText;
                runState.LatestStepSummary = persistedRepairContinuation.Summary;
                AddEvent(
                    runState,
                    persistedRepairContinuation.EventKind,
                    selection.Batch.BatchId,
                    selection.WorkItem.WorkItemId,
                    persistedRepairContinuation.Summary);
                await SaveAndNotifyAsync(
                    new TaskboardLiveProgressUpdate
                    {
                        PhaseCode = persistedRepairContinuation.PhaseCode,
                        PhaseText = persistedRepairContinuation.PhaseText,
                        EventKind = persistedRepairContinuation.EventKind,
                        ActivitySummary = persistedRepairContinuation.Summary,
                        BatchId = selection.Batch.BatchId,
                        WorkItemId = selection.WorkItem.WorkItemId,
                        WorkItemTitle = selection.WorkItem.Title,
                        ToolName = persistedRepairContinuation.NextToolRequest?.ToolName ?? "",
                        ChainTemplateId = persistedRepairContinuation.NextToolRequest?.PreferredChainTemplateName ?? ""
                    },
                    refreshCoverage: true);
            }

            var projection = _runProjectionService.PrepareProjectionForBatch(
                workspaceRoot,
                activeImport,
                batch,
                string.IsNullOrWhiteSpace(selectedBatchId) ? "active_plan" : "selected_batch",
                ramDbService,
                executionStarted: true,
                selection.Batch,
                runState);
            result.Projection = projection;

            MarkWorkItemRunning(runState, selection.Batch, selection.WorkItem);
            stateChanged = true;
            result.Started = true;
            await SaveAndNotifyAsync(
                new TaskboardLiveProgressUpdate
                {
                    PhaseCode = runState.CurrentRunPhaseCode,
                    PhaseText = runState.CurrentRunPhaseText,
                    ActivitySummary = runState.LatestStepSummary,
                    BatchId = selection.Batch.BatchId,
                    WorkItemId = selection.WorkItem.WorkItemId,
                    WorkItemTitle = selection.WorkItem.Title
                },
                refreshCoverage: true);

            var bridge = _executionBridgeService.Bridge(
                workspaceRoot,
                BuildRunWorkItem(selection.WorkItem),
                activeDocument.Title,
                effectiveActiveTargetRelativePath);
            if (bridge.Disposition == TaskboardExecutionBridgeDisposition.Executable)
            {
                var maintenanceGuard = _maintenanceBaselineService.EvaluateMutationGuard(
                    workspaceRoot,
                    maintenanceBaseline,
                    bridge.ToolRequest);
                ApplyMaintenanceGuardState(runState, maintenanceGuard);
                _maintenanceBaselineService.StampBaselineContext(bridge.ToolRequest, maintenanceBaseline);
                if (maintenanceGuard.Applies && !maintenanceGuard.Allowed)
                {
                    bridge.Disposition = TaskboardExecutionBridgeDisposition.Blocked;
                    bridge.Reason = maintenanceGuard.Summary;
                    bridge.ResolvedTargetPath = FirstNonEmpty(maintenanceGuard.TargetPath, bridge.ResolvedTargetPath);
                    bridge.ExecutionGoalResolution.ResolutionReason = maintenanceGuard.Summary;
                    bridge.ExecutionGoalResolution.ResolvedTargetPath = FirstNonEmpty(maintenanceGuard.TargetPath, bridge.ExecutionGoalResolution.ResolvedTargetPath);
                    bridge.ExecutionGoalResolution.Blocker.Code = TaskboardExecutionGoalBlockerCode.NoDeterministicLane;
                    bridge.ExecutionGoalResolution.Blocker.Message = maintenanceGuard.Summary;
                    bridge.ExecutionGoalResolution.Blocker.Detail = maintenanceGuard.Summary;
                    bridge.ExecutionGoalResolution.LaneResolution.Blocker.Code = TaskboardExecutionLaneBlockerCode.UnsafeBlocked;
                    bridge.ExecutionGoalResolution.LaneResolution.Blocker.Message = maintenanceGuard.Summary;
                    bridge.ExecutionGoalResolution.LaneResolution.Blocker.Detail = maintenanceGuard.Summary;
                    bridge.ExecutionGoalResolution.GoalKind = TaskboardExecutionGoalKind.BlockedGoal;
                }
            }
            ApplyExecutionGoalResolution(runState, selection.Batch, selection.WorkItem, bridge.ExecutionGoalResolution);
            _artifactStore.SaveExecutionLaneArtifact(ramDbService, workspaceRoot, activeImport.ImportId, bridge.ExecutionGoalResolution.LaneResolution);
            _artifactStore.SaveExecutionGoalArtifact(ramDbService, workspaceRoot, activeImport.ImportId, bridge.ExecutionGoalResolution);
            await SaveAndNotifyAsync(
                new TaskboardLiveProgressUpdate
                {
                    PhaseCode = runState.CurrentRunPhaseCode,
                    PhaseText = runState.CurrentRunPhaseText,
                    ActivitySummary = runState.LatestStepSummary,
                    BatchId = selection.Batch.BatchId,
                    WorkItemId = selection.WorkItem.WorkItemId,
                    WorkItemTitle = selection.WorkItem.Title
                },
                refreshCoverage: true);
            if (bridge.Disposition is TaskboardExecutionBridgeDisposition.ManualOnly or TaskboardExecutionBridgeDisposition.Blocked)
            {
                var forensicsSummary = await BuildForensicsSummaryAsync(
                    workspaceRoot,
                    selection.WorkItem,
                    bridge.ExecutionGoalResolution,
                    settings,
                    endpoint,
                    selectedModel,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(forensicsSummary))
                {
                    bridge.ExecutionGoalResolution.ForensicsExplanation = forensicsSummary;
                    runState.LastForensicsSummary = forensicsSummary;
                }

                var bridgeOutcome = new TaskboardExecutionOutcome
                {
                    ResultKind = bridge.Disposition == TaskboardExecutionBridgeDisposition.ManualOnly
                        ? TaskboardWorkItemResultKind.ManualOnly
                        : TaskboardWorkItemResultKind.Blocked,
                    ResultClassification = bridge.Disposition == TaskboardExecutionBridgeDisposition.ManualOnly
                        ? "manual_only"
                        : "blocked",
                    Summary = FirstNonEmpty(forensicsSummary, bridge.Reason)
                };

                ApplyExecutionOutcome(runState, selection.Batch, selection.WorkItem, bridgeOutcome);
                AssignCurrentBlocker(
                    workspaceRoot,
                    activeImport,
                    runState,
                    selection.Batch,
                    selection.WorkItem,
                    bridgeOutcome.ResultKind == TaskboardWorkItemResultKind.ManualOnly ? "manual_only_boundary" : "fresh_followup",
                    bridgeOutcome.Summary,
                    ramDbService);
                await SaveAndNotifyAsync(
                    new TaskboardLiveProgressUpdate
                    {
                        PhaseCode = runState.CurrentRunPhaseCode,
                        PhaseText = runState.CurrentRunPhaseText,
                        ActivitySummary = runState.LatestStepSummary,
                        BatchId = selection.Batch.BatchId,
                        WorkItemId = selection.WorkItem.WorkItemId,
                        WorkItemTitle = selection.WorkItem.Title
                    },
                    refreshCoverage: true);

                result.RunState = runState;
                result.StateChanged = true;
                result.ExecutionOccurred = executionOccurred;
                result.StatusCode = bridgeOutcome.ResultClassification;
                result.Message = BuildTerminalMessage(actionName, runState, bridgeOutcome.Summary, selectedBatchId);
                FinalizeTerminalSummary(
                    workspaceRoot,
                    activeImport,
                    runState,
                    ramDbService,
                    actionName,
                    selectedBatchId,
                    result.StatusCode,
                    result.Message);
                return result;
            }

            var outcomeBaselineUtc = selection.WorkItem.UpdatedUtc;
            var satisfaction = _stateSatisfactionService.Evaluate(
                workspaceRoot,
                runState,
                selection.Batch,
                selection.WorkItem,
                bridge,
                ramDbService);
            runState.LastSatisfactionCheckSummary = BuildSatisfactionCheckSummary(selection.WorkItem.Title, satisfaction);
            if (satisfaction.Satisfied && satisfaction.SkipAllowed)
            {
                AppendExecutedToolCall(
                    runState,
                    selection.Batch.BatchId,
                    selection.WorkItem.WorkItemId,
                    bridge.ToolRequest?.ToolName ?? "",
                    FirstNonEmpty(bridge.ToolRequest?.PreferredChainTemplateName, bridge.ExecutionGoalResolution.Goal.SelectedChainTemplateId),
                    "skipped",
                    "state_already_satisfied",
                    BuildSatisfactionSkipSummary(selection.WorkItem.Title, satisfaction));
                var skipRecord = BuildSkipDecisionRecord(workspaceRoot, runState, selection.Batch, selection.WorkItem, bridge, satisfaction);
                ramDbService.AddTaskboardSkipRecord(workspaceRoot, skipRecord);
                runState.SatisfactionSkipCount += 1;
                runState.RepeatedFileTouchesAvoidedCount += satisfaction.RepeatedTouchesAvoidedCount;
                runState.LastSatisfactionSkipWorkItemId = selection.WorkItem.WorkItemId;
                runState.LastSatisfactionSkipWorkItemTitle = selection.WorkItem.Title;
                runState.LastSatisfactionSkipReasonCode = satisfaction.ReasonCode;
                runState.LastSatisfactionSkipEvidenceSummary = satisfaction.EvidenceSummary;

                var skipOutcome = new TaskboardExecutionOutcome
                {
                    ResultKind = TaskboardWorkItemResultKind.Passed,
                    ResultClassification = "state_already_satisfied",
                    Summary = BuildSatisfactionSkipSummary(selection.WorkItem.Title, satisfaction),
                    ExecutionAttempted = false
                };
                ApplyExecutionOutcome(runState, selection.Batch, selection.WorkItem, skipOutcome);
                ReconcilePostSuccess(workspaceRoot, activeImport, runState, selection.Batch, selection.WorkItem, ramDbService);
                await SaveAndNotifyAsync(
                    new TaskboardLiveProgressUpdate
                    {
                        PhaseCode = "state_satisfied_skip",
                        PhaseText = "Skipped execution because the required state was already satisfied.",
                        EventKind = "work_item_skipped_satisfied",
                        ActivitySummary = skipOutcome.Summary,
                        BatchId = selection.Batch.BatchId,
                        WorkItemId = selection.WorkItem.WorkItemId,
                        WorkItemTitle = selection.WorkItem.Title,
                        ToolName = bridge.ToolRequest?.ToolName ?? "",
                        ChainTemplateId = bridge.ToolRequest?.PreferredChainTemplateName ?? ""
                    },
                    refreshCoverage: true);

                result.RunState = runState;
                result.StateChanged = true;
                result.ExecutionOccurred = executionOccurred;
                continue;
            }

            StampToolRequestContext(bridge.ToolRequest, activeImport, runState, selection.Batch, selection.WorkItem);
            var outcome = await executor(
                bridge,
                update => SaveAndNotifyAsync(
                    new TaskboardLiveProgressUpdate
                    {
                        PhaseCode = update.PhaseCode,
                        PhaseText = update.PhaseText,
                        EventKind = update.EventKind,
                        ActivitySummary = update.ActivitySummary,
                        BatchId = FirstNonEmpty(update.BatchId, selection.Batch.BatchId),
                        WorkItemId = FirstNonEmpty(update.WorkItemId, selection.WorkItem.WorkItemId),
                        WorkItemTitle = FirstNonEmpty(update.WorkItemTitle, selection.WorkItem.Title),
                        ToolName = update.ToolName,
                        ChainTemplateId = update.ChainTemplateId
                    }));
            AppendExecutedToolCalls(runState, selection.Batch.BatchId, selection.WorkItem.WorkItemId, outcome.ExecutedToolCalls);
            var repairContinuation = TryAdvanceRepairContinuation(
                workspaceRoot,
                runState,
                selection.Batch,
                selection.WorkItem,
                outcome,
                outcomeBaselineUtc,
                ramDbService);
            if (repairContinuation.ShouldContinueCurrentItem)
            {
                StampToolRequestContext(repairContinuation.NextToolRequest, activeImport, runState, selection.Batch, selection.WorkItem);
                selection.WorkItem.DirectToolRequest = repairContinuation.NextToolRequest?.Clone();
                selection.WorkItem.UpdatedUtc = DateTime.UtcNow.ToString("O");
                selection.Batch.Status = TaskboardBatchRuntimeStatus.Running;
                selection.Batch.CurrentWorkItemId = selection.WorkItem.WorkItemId;
                runState.PlanStatus = TaskboardPlanRuntimeStatus.Running;
                runState.CurrentBatchId = selection.Batch.BatchId;
                runState.CurrentWorkItemId = selection.WorkItem.WorkItemId;
                runState.CurrentRunPhaseCode = repairContinuation.PhaseCode;
                runState.CurrentRunPhaseText = repairContinuation.PhaseText;
                runState.LatestStepSummary = repairContinuation.Summary;
                AddEvent(
                    runState,
                    repairContinuation.EventKind,
                    selection.Batch.BatchId,
                    selection.WorkItem.WorkItemId,
                    repairContinuation.Summary);
                await SaveAndNotifyAsync(
                    new TaskboardLiveProgressUpdate
                    {
                        PhaseCode = runState.CurrentRunPhaseCode,
                        PhaseText = runState.CurrentRunPhaseText,
                        EventKind = repairContinuation.EventKind,
                        ActivitySummary = repairContinuation.Summary,
                        BatchId = selection.Batch.BatchId,
                        WorkItemId = selection.WorkItem.WorkItemId,
                        WorkItemTitle = selection.WorkItem.Title,
                        ToolName = repairContinuation.NextToolRequest?.ToolName ?? "",
                        ChainTemplateId = repairContinuation.NextToolRequest?.PreferredChainTemplateName ?? ""
                    },
                    refreshCoverage: true);

                result.RunState = runState;
                result.StateChanged = true;
                result.ExecutionOccurred = executionOccurred | outcome.ExecutionAttempted;
                continue;
            }

            if (repairContinuation.OverrideOutcome is not null)
                outcome = repairContinuation.OverrideOutcome;
            executionOccurred |= outcome.ExecutionAttempted;
            ApplyExecutionOutcome(runState, selection.Batch, selection.WorkItem, outcome);
            if (outcome.ResultKind == TaskboardWorkItemResultKind.Passed)
            {
                ReconcilePostSuccess(workspaceRoot, activeImport, runState, selection.Batch, selection.WorkItem, ramDbService);
            }
            else
            {
                if (TryPromoteBuildFailureRecovery(
                    workspaceRoot,
                    activeImport,
                    runState,
                    selection.Batch,
                    selection.WorkItem,
                    outcome,
                    ramDbService))
                {
                    await SaveAndNotifyAsync(
                        new TaskboardLiveProgressUpdate
                        {
                            PhaseCode = runState.CurrentRunPhaseCode,
                            PhaseText = runState.CurrentRunPhaseText,
                            ActivitySummary = runState.LatestStepSummary,
                            BatchId = selection.Batch.BatchId,
                            WorkItemId = runState.LastFollowupWorkItemId,
                            WorkItemTitle = runState.LastFollowupWorkItemTitle
                        },
                        refreshCoverage: true);

                    result.RunState = runState;
                    result.StateChanged = true;
                    result.ExecutionOccurred = executionOccurred;
                    continue;
                }

                AssignCurrentBlocker(
                    workspaceRoot,
                    activeImport,
                    runState,
                    selection.Batch,
                    selection.WorkItem,
                    "fresh_followup",
                    outcome.Summary,
                    ramDbService);
            }
            await SaveAndNotifyAsync(
                new TaskboardLiveProgressUpdate
                {
                    PhaseCode = runState.CurrentRunPhaseCode,
                    PhaseText = runState.CurrentRunPhaseText,
                    ActivitySummary = runState.LatestStepSummary,
                    BatchId = selection.Batch.BatchId,
                    WorkItemId = selection.WorkItem.WorkItemId,
                    WorkItemTitle = selection.WorkItem.Title
                },
                refreshCoverage: true);

            result.RunState = runState;
            result.StateChanged = true;
            result.ExecutionOccurred = executionOccurred;

            if (outcome.ResultKind != TaskboardWorkItemResultKind.Passed)
            {
                result.StatusCode = NormalizeResultKind(outcome.ResultKind);
                result.Message = BuildTerminalMessage(actionName, runState, outcome.Summary, selectedBatchId);
                FinalizeTerminalSummary(
                    workspaceRoot,
                    activeImport,
                    runState,
                    ramDbService,
                    actionName,
                    selectedBatchId,
                    result.StatusCode,
                    result.Message);
                return result;
            }

            if (!string.IsNullOrWhiteSpace(selectedBatchId)
                && string.Equals(runState.CurrentBatchId, selectedBatchId, StringComparison.OrdinalIgnoreCase))
            {
                var scopedBatch = runState.Batches.FirstOrDefault(current =>
                    string.Equals(current.BatchId, selectedBatchId, StringComparison.OrdinalIgnoreCase));
                if (scopedBatch is not null && scopedBatch.Status == TaskboardBatchRuntimeStatus.Completed)
                {
                    if (runState.PlanStatus != TaskboardPlanRuntimeStatus.Completed)
                        runState.PlanStatus = TaskboardPlanRuntimeStatus.Active;

                    runState.CurrentBatchId = "";
                    runState.CurrentWorkItemId = "";
                    await SaveAndNotifyAsync(
                        new TaskboardLiveProgressUpdate
                        {
                            PhaseCode = "selected_batch_completed",
                            PhaseText = $"Selected batch `{scopedBatch.Title}` completed.",
                            ActivitySummary = $"Selected batch completed: {scopedBatch.Title}.",
                            BatchId = scopedBatch.BatchId
                        });

                    result.RunState = runState;
                    result.Success = true;
                    result.StateChanged = true;
                    result.ExecutionOccurred = executionOccurred;
                    result.StatusCode = "selected_batch_completed";
                    result.Message = $"Run Selected Batch completed: `{scopedBatch.Title}` finished {scopedBatch.CompletedWorkItemCount}/{scopedBatch.TotalWorkItemCount} work item(s).";
                    FinalizeTerminalSummary(
                        workspaceRoot,
                        activeImport,
                        runState,
                        ramDbService,
                        actionName,
                        selectedBatchId,
                        result.StatusCode,
                        result.Message);
                    return result;
                }
            }

            if (runState.PlanStatus == TaskboardPlanRuntimeStatus.Completed)
            {
                result.RunState = runState;
                result.Success = true;
                result.StateChanged = true;
                result.ExecutionOccurred = executionOccurred;
                result.CompletedPlan = true;
                result.StatusCode = "completed";
                result.Message = $"Active plan completed: `{activeImport.Title}` finished {runState.CompletedWorkItemCount}/{runState.TotalWorkItemCount} work item(s).";
                FinalizeTerminalSummary(
                    workspaceRoot,
                    activeImport,
                    runState,
                    ramDbService,
                    actionName,
                    selectedBatchId,
                    result.StatusCode,
                    result.Message);
                return result;
            }
        }
    }

    public TaskboardPlanRunStateRecord LoadOrCreateRunState(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardDocument activeDocument,
        RamDbService ramDbService,
        bool forceFreshRuntimeState = false)
    {
        var existing = _artifactStore.LoadRunState(ramDbService, workspaceRoot, activeImport);
        var assessment = _runtimeStateFingerprintService.Evaluate(existing, activeImport, activeDocument);
        if (existing is not null
            && string.Equals(existing.PlanImportId, activeImport.ImportId, StringComparison.OrdinalIgnoreCase)
            && assessment.IsCompatible)
        {
            RefreshExistingRunState(existing, assessment, forceFreshRuntimeState);
            UpdateCoverageState(workspaceRoot, activeImport, existing, ramDbService);
            SaveRunState(workspaceRoot, existing, ramDbService);
            return existing;
        }

        var runState = CreateRunState(workspaceRoot, activeImport, activeDocument, assessment);
        if (existing is not null && !assessment.IsCompatible)
        {
            AddEvent(
                runState,
                "runtime_state_rebuilt",
                "",
                "",
                $"Stale runtime snapshot invalidated: {FirstNonEmpty(assessment.InvalidationReason, assessment.Summary)}");
        }

        UpdateCoverageState(workspaceRoot, activeImport, runState, ramDbService);
        SaveRunState(workspaceRoot, runState, ramDbService);
        return runState;
    }

    private TaskboardPlanRunStateRecord CreateRunState(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardDocument activeDocument,
        TaskboardRuntimeStateAssessment assessment)
    {
        var now = DateTime.UtcNow.ToString("O");
        var batches = activeDocument.Batches
            .OrderBy(batch => batch.BatchNumber)
            .Select(batch =>
            {
                var workItems = _runProjectionService.CreateWorkItems(batch)
                    .Select(item =>
                    {
                        var workFamily = _workFamilyResolutionService.Resolve(item);
                        return new TaskboardWorkItemRunStateRecord
                        {
                            WorkItemId = item.WorkItemId,
                            Ordinal = item.Ordinal,
                            DisplayOrdinal = item.DisplayOrdinal,
                            Title = item.Title,
                            PromptText = item.PromptText,
                            Summary = item.Summary,
                            IsDecomposedItem = item.IsDecomposedItem,
                            SourceWorkItemId = item.SourceWorkItemId,
                            OperationKind = item.OperationKind,
                            TargetStack = item.TargetStack,
                            WorkFamily = workFamily.FamilyId,
                            ExpectedArtifact = item.ExpectedArtifact,
                            ValidationHint = item.ValidationHint,
                            PhraseFamily = item.PhraseFamily,
                            TemplateId = item.TemplateId,
                            TemplateCandidateIds = [.. item.TemplateCandidateIds],
                            DirectToolRequest = item.DirectToolRequest?.Clone(),
                            Status = TaskboardWorkItemRuntimeStatus.Pending,
                            UpdatedUtc = now
                        };
                    })
                    .ToList();

                return new TaskboardBatchRunStateRecord
                {
                    BatchId = batch.BatchId,
                    BatchNumber = batch.BatchNumber,
                    Title = batch.Title,
                    Status = workItems.Count == 0 ? TaskboardBatchRuntimeStatus.Skipped : TaskboardBatchRuntimeStatus.Pending,
                    CompletedWorkItemCount = 0,
                    TotalWorkItemCount = workItems.Count,
                    WorkItems = workItems
                };
            })
            .ToList();

        var runState = new TaskboardPlanRunStateRecord
        {
            RunStateId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            PlanImportId = activeImport.ImportId,
            PlanTitle = activeImport.Title,
            PlanStatus = batches.All(batch => batch.Status == TaskboardBatchRuntimeStatus.Skipped)
                ? TaskboardPlanRuntimeStatus.Completed
                : TaskboardPlanRuntimeStatus.Active,
            StartedUtc = now,
            UpdatedUtc = now,
            Batches = batches,
            TotalWorkItemCount = batches.Sum(batch => batch.TotalWorkItemCount),
            CompletedWorkItemCount = 0
        };

        ApplyRuntimeSnapshotMetadata(
            runState,
            assessment,
            assessment.HasSnapshot && !assessment.IsCompatible
                ? "rebuilt_from_stale_snapshot"
                : "fresh_runtime_snapshot",
            assessment.HasSnapshot && !assessment.IsCompatible
                ? $"Rebuilt runtime state from the current active plan after invalidating stale snapshot data: {FirstNonEmpty(assessment.InvalidationReason, assessment.Summary)}"
                : "Created a fresh runtime state from the current active plan.");
        return runState;
    }

    private void RefreshExistingRunState(
        TaskboardPlanRunStateRecord runState,
        TaskboardRuntimeStateAssessment assessment,
        bool forceFreshRuntimeState)
    {
        if (forceFreshRuntimeState)
            ResetRuntimeForFreshRun(runState);

        RefreshRunStateDerivedFields(runState);
        if (forceFreshRuntimeState)
            ResetRuntimeSnapshotSurface(runState);

        ApplyRuntimeSnapshotMetadata(
            runState,
            assessment,
            forceFreshRuntimeState ? "fresh_runtime_snapshot" : "reused_compatible_snapshot",
            forceFreshRuntimeState
                ? "Recomputed current runtime state from authoritative taskboard data at run start."
                : "Reused a compatible cached runtime snapshot.");
    }

    public void SaveRunState(string workspaceRoot, TaskboardPlanRunStateRecord runState, RamDbService ramDbService)
    {
        runState.UpdatedUtc = DateTime.UtcNow.ToString("O");
        _artifactStore.SaveRunStateArtifact(ramDbService, workspaceRoot, runState);
    }

    private void FinalizeTerminalSummary(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardPlanRunStateRecord runState,
        RamDbService ramDbService,
        string actionName,
        string selectedBatchId,
        string terminalStatusCode,
        string terminalMessage)
    {
        if (!IsTerminalRunState(runState, terminalStatusCode))
        {
            SaveRunState(workspaceRoot, runState, ramDbService);
            return;
        }

        TryCaptureWorkspaceStructuralTruth(workspaceRoot, ramDbService);

        var existingSummary = ResolveExistingTerminalSummary(workspaceRoot, activeImport, runState, ramDbService);
        var summary = _runSummaryService.Build(
            workspaceRoot,
            activeImport,
            runState,
            ramDbService,
            actionName,
            selectedBatchId,
            terminalStatusCode);
        summary.TerminalNote = FirstNonEmpty(summary.TerminalNote, terminalMessage);
        summary.TerminalFingerprint = _runSummaryService.ComputeTerminalFingerprint(summary);

        if (existingSummary is not null
            && !string.IsNullOrWhiteSpace(existingSummary.SummaryId)
            && string.Equals(existingSummary.TerminalStatusCode, summary.TerminalStatusCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existingSummary.TerminalFingerprint, summary.TerminalFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            runState.LastTerminalStatusCode = existingSummary.TerminalStatusCode;
            runState.LastTerminalSummaryFingerprint = existingSummary.TerminalFingerprint;
            runState.LastTerminalSummary = existingSummary;
            runState.LastTerminalSummaryArtifactPath = existingSummary.SummaryArtifactRelativePath;
            SaveRunState(workspaceRoot, runState, ramDbService);
            return;
        }

        _runDataFoundationService.FinalizeTerminalRunData(workspaceRoot, activeImport, runState, summary, ramDbService);
        summary.TerminalFingerprint = _runSummaryService.ComputeTerminalFingerprint(summary);
        runState.LastTerminalStatusCode = summary.TerminalStatusCode;
        runState.LastTerminalSummaryFingerprint = summary.TerminalFingerprint;
        runState.LastTerminalSummary = summary;
        runState.LastTerminalSummaryArtifactPath = summary.SummaryArtifactRelativePath;
        _artifactStore.SaveRunSummaryArtifact(ramDbService, workspaceRoot, summary);
        SaveRunState(workspaceRoot, runState, ramDbService);
    }

    private void TryCaptureWorkspaceStructuralTruth(string workspaceRoot, RamDbService ramDbService)
    {
        try
        {
            _workspaceStructuralTruthService.CaptureAndPersist(workspaceRoot, ramDbService);
        }
        catch
        {
            // Stage 0.1 intake is read-only and must not block current scaffold execution.
        }
    }

    private TaskboardRunTerminalSummaryRecord? ResolveExistingTerminalSummary(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardPlanRunStateRecord runState,
        RamDbService ramDbService)
    {
        if (runState.LastTerminalSummary is { SummaryId.Length: > 0 })
            return runState.LastTerminalSummary;

        if (!string.IsNullOrWhiteSpace(runState.LastTerminalSummaryArtifactPath))
        {
            var artifact = ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, runState.LastTerminalSummaryArtifactPath);
            if (artifact is not null)
            {
                try
                {
                    var summary = JsonSerializer.Deserialize<TaskboardRunTerminalSummaryRecord>(artifact.Content);
                    if (summary is not null)
                        return summary;
                }
                catch
                {
                }
            }
        }

        return _artifactStore.LoadLatestRunSummary(ramDbService, workspaceRoot, activeImport);
    }

    private void RefreshRunStateDerivedFields(TaskboardPlanRunStateRecord runState)
    {
        foreach (var batch in runState.Batches)
        {
            RefreshWorkFamilies(batch);
            foreach (var item in batch.WorkItems)
                RefreshWorkItemDerivedFields(item);
        }

        runState.CompletedWorkItemCount = runState.Batches.Sum(batch => batch.WorkItems.Count(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed));
        runState.TotalWorkItemCount = runState.Batches.Sum(batch => batch.WorkItems.Count(item => item.Status != TaskboardWorkItemRuntimeStatus.Skipped || item.IsDecomposedItem));
        NormalizeCompletionState(runState);
    }

    private static void RefreshWorkItemDerivedFields(TaskboardWorkItemRunStateRecord item)
    {
        if (IsMissingOrUnknown(item.OperationKind))
        {
            item.OperationKind = FirstMeaningful(
                item.LastExecutionGoalResolution.OperationKind,
                item.LastExecutionGoalResolution.LaneResolution.OperationKind);
        }

        if (IsMissingOrUnknown(item.TargetStack))
        {
            item.TargetStack = FirstMeaningful(
                item.LastExecutionGoalResolution.TargetStack,
                item.LastExecutionGoalResolution.LaneResolution.TargetStack);
        }

        if (IsMissingOrUnknown(item.PhraseFamily))
        {
            item.PhraseFamily = FirstMeaningful(
                item.LastExecutionGoalResolution.PhraseFamily,
                item.LastExecutionGoalResolution.LaneResolution.PhraseFamily);
        }

        if (IsMissingOrUnknown(item.TemplateId))
        {
            item.TemplateId = FirstMeaningful(
                item.LastExecutionGoalResolution.TemplateId,
                item.LastExecutionGoalResolution.LaneResolution.TemplateId);
        }

        if ((item.TemplateCandidateIds is null || item.TemplateCandidateIds.Count == 0)
            && item.LastExecutionGoalResolution.TemplateCandidateIds.Count > 0)
        {
            item.TemplateCandidateIds = [.. item.LastExecutionGoalResolution.TemplateCandidateIds];
        }

        ApplyToolRequestFallback(item);
    }

    private static void ResetRuntimeSnapshotSurface(TaskboardPlanRunStateRecord runState)
    {
        runState.CurrentRunPhaseCode = "";
        runState.CurrentRunPhaseText = "";
        runState.LatestStepSummary = "";
        runState.LastCompletedStepSummary = "";
        runState.LastBlockerReason = "";
        runState.LastBlockerOrigin = "";
        runState.LastBlockerWorkItemId = "";
        runState.LastBlockerWorkItemTitle = "";
        runState.LastBlockerPhase = "";
        runState.LastBlockerWorkFamily = "";
        runState.LastBlockerPhraseFamily = "";
        runState.LastBlockerOperationKind = "";
        runState.LastBlockerStackFamily = "";
        runState.LastBlockerGeneration = 0;
        runState.LastCompletedWorkItemTitle = "";
        runState.LastCompletedWorkFamily = "";
        runState.LastCompletedPhraseFamily = "";
        runState.LastCompletedOperationKind = "";
        runState.LastCompletedStackFamily = "";
        runState.LastResultKind = "";
        runState.LastResultSummary = "";
        runState.LastResolvedBuildProfile = new TaskboardBuildProfileResolutionRecord();
        runState.LastDecompositionWorkItemId = "";
        runState.LastDecompositionSummary = "";
        runState.LastWorkFamily = "";
        runState.LastWorkFamilySource = "";
        runState.LastExecutionDecisionSummary = "";
        runState.LastPlannedToolName = "";
        runState.LastPlannedChainTemplateId = "";
        runState.LastObservedToolName = "";
        runState.LastObservedChainTemplateId = "";
        runState.LastMutationToolName = "";
        runState.LastMutationUtc = "";
        runState.LastMutationTouchedFilePaths = [];
        runState.LastVerificationAfterMutationOutcome = "";
        runState.LastVerificationAfterMutationUtc = "";
        runState.LastRepairTargetPath = "";
        runState.LastRepairDraftKind = "";
        runState.LastRepairLocalPatchAvailable = false;
        runState.LastRepairTargetingStrategy = "";
        runState.LastRepairTargetingSummary = "";
        runState.LastRepairReferencedSymbolName = "";
        runState.LastRepairReferencedMemberName = "";
        runState.LastRepairSymbolRecoveryStatus = "";
        runState.LastRepairSymbolRecoverySummary = "";
        runState.LastRepairSymbolRecoveryCandidatePath = "";
        runState.LastRepairContinuationStatus = "";
        runState.LastRepairContinuationSummary = "";
        runState.LastCanonicalOperationKind = "";
        runState.LastCanonicalTargetPath = "";
        runState.LastCanonicalProjectName = "";
        runState.LastCanonicalTemplateHint = "";
        runState.LastCanonicalRoleHint = "";
        runState.LastCanonicalizationTrace = "";
        runState.LastBehaviorDepthArtifactPath = "";
        runState.LastBehaviorDepthTier = "";
        runState.LastBehaviorDepthCompletionRecommendation = "";
        runState.LastBehaviorDepthFollowUpRecommendation = "";
        runState.LastBehaviorDepthTargetPath = "";
        runState.LastBehaviorDepthProfile = "";
        runState.LastBehaviorDepthNamespace = "";
        runState.LastBehaviorDepthFeatureFamily = "";
        runState.LastBehaviorDepthIntegrationGapKind = "";
        runState.LastBehaviorDepthNextFollowThroughHint = "";
        runState.LastBehaviorDepthCandidateSurfaceHints = [];
        runState.LastProjectAttachTargetPath = "";
        runState.LastProjectAttachProjectExistedAtDecision = false;
        runState.LastProjectAttachContinuationStatus = "";
        runState.LastProjectAttachInsertedStep = "";
        runState.LastProjectAttachSummary = "";
        runState.RecentObservedToolNames = [];
        runState.RecentObservedChainTemplateIds = [];
        runState.ExecutedToolCalls = [];
        runState.LastPhraseFamilyRawPhraseText = "";
        runState.LastPhraseFamilyNormalizedPhraseText = "";
        runState.LastPhraseFamilyClosestKnownFamilyGroup = "";
        runState.LastPhraseFamilyResolutionPathTrace = "";
        runState.LastPhraseFamilyTerminalResolverStage = "";
        runState.LastPhraseFamilyBuilderOperationStatus = "";
        runState.LastPhraseFamilyLaneResolutionStatus = "";
        runState.LastPhraseFamily = "";
        runState.LastPhraseFamilySource = "";
        runState.LastPhraseFamilyResolutionSummary = "";
        runState.LastPhraseFamilyCandidates = [];
        runState.LastPhraseFamilyDeterministicCandidate = "";
        runState.LastPhraseFamilyAdvisoryCandidate = "";
        runState.LastPhraseFamilyBlockerCode = "";
        runState.LastPhraseFamilyTieBreakRuleId = "";
        runState.LastPhraseFamilyTieBreakSummary = "";
        runState.LastTemplateId = "";
        runState.LastTemplateCandidateIds = [];
        runState.LastResolvedTargetFileType = "";
        runState.LastResolvedTargetRole = "";
        runState.LastResolvedTargetProjectName = "";
        runState.LastResolvedTargetNamespaceHint = "";
        runState.LastResolvedTargetIdentityTrace = "";
        runState.LastExecutionGoalResolution = new TaskboardExecutionGoalResolution();
        runState.LastExecutionGoalSummary = "";
        runState.LastExecutionGoalBlockerCode = "";
        runState.LastForensicsSummary = "";
        runState.LastMaintenanceGuardReasonCode = "";
        runState.LastMaintenanceGuardSummary = "";
        runState.LastSupportCoverageWorkItemId = "";
        runState.LastSupportCoverageWorkItemTitle = "";
        runState.LastSupportCoverageSummary = "";
        runState.LastContradictionGuardWorkItemId = "";
        runState.LastContradictionGuardWorkItemTitle = "";
        runState.LastContradictionGuardReasonCode = "";
        runState.LastContradictionGuardSummary = "";
        runState.LastCoverageMapSummary = "";
        runState.LastNextWorkFamily = "";
        runState.LastFollowupBatchId = "";
        runState.LastFollowupBatchTitle = "";
        runState.LastFollowupWorkItemId = "";
        runState.LastFollowupWorkItemTitle = "";
        runState.LastFollowupSelectionReason = "";
        runState.LastFollowupWorkFamily = "";
        runState.LastFollowupPhraseFamily = "";
        runState.LastFollowupOperationKind = "";
        runState.LastFollowupStackFamily = "";
        runState.LastFollowupPhraseFamilyReasonCode = "";
        runState.LastFollowupOperationKindReasonCode = "";
        runState.LastFollowupStackFamilyReasonCode = "";
        runState.LastFollowupResolutionSummary = "";
        runState.LastFailureOutcomeType = "";
        runState.LastFailureFamily = "";
        runState.LastFailureErrorCode = "";
        runState.LastFailureNormalizedSummary = "";
        runState.LastFailureTargetPath = "";
        runState.LastFailureSourcePath = "";
        runState.LastFailureRepairContextPath = "";
        runState.LastCanonicalOperationKind = "";
        runState.LastCanonicalTargetPath = "";
        runState.LastCanonicalProjectName = "";
        runState.LastCanonicalTemplateHint = "";
        runState.LastCanonicalRoleHint = "";
        runState.LastCanonicalizationTrace = "";
        runState.LastLiveRunEntrySummary = "";
        runState.LastPostChainReconciliationSummary = "";
        ClearTerminalSummaryState(runState);
        runState.LastSatisfactionSkipWorkItemId = "";
        runState.LastSatisfactionSkipWorkItemTitle = "";
        runState.LastSatisfactionSkipReasonCode = "";
        runState.LastSatisfactionSkipEvidenceSummary = "";
        runState.LastSatisfactionCheckSummary = "";
        runState.LastExecutionDecisionSummary = "";
        runState.LastPlannedToolName = "";
        runState.LastPlannedChainTemplateId = "";
        runState.LastObservedToolName = "";
        runState.LastObservedChainTemplateId = "";
        runState.LastMutationToolName = "";
        runState.LastMutationUtc = "";
        runState.LastMutationTouchedFilePaths = [];
        runState.LastVerificationAfterMutationOutcome = "";
        runState.LastVerificationAfterMutationUtc = "";
        runState.LastRepairTargetPath = "";
        runState.LastRepairDraftKind = "";
        runState.LastRepairLocalPatchAvailable = false;
        runState.LastRepairTargetingStrategy = "";
        runState.LastRepairTargetingSummary = "";
        runState.LastRepairReferencedSymbolName = "";
        runState.LastRepairReferencedMemberName = "";
        runState.LastRepairSymbolRecoveryStatus = "";
        runState.LastRepairSymbolRecoverySummary = "";
        runState.LastRepairSymbolRecoveryCandidatePath = "";
        runState.LastRepairContinuationStatus = "";
        runState.LastRepairContinuationSummary = "";
        runState.LastBehaviorDepthArtifactPath = "";
        runState.LastBehaviorDepthTier = "";
        runState.LastBehaviorDepthCompletionRecommendation = "";
        runState.LastBehaviorDepthFollowUpRecommendation = "";
        runState.LastBehaviorDepthTargetPath = "";
        runState.LastBehaviorDepthProfile = "";
        runState.LastBehaviorDepthNamespace = "";
        runState.LastBehaviorDepthFeatureFamily = "";
        runState.LastBehaviorDepthIntegrationGapKind = "";
        runState.LastBehaviorDepthNextFollowThroughHint = "";
        runState.LastBehaviorDepthCandidateSurfaceHints = [];
        runState.LastProjectAttachTargetPath = "";
        runState.LastProjectAttachProjectExistedAtDecision = false;
        runState.LastProjectAttachContinuationStatus = "";
        runState.LastProjectAttachInsertedStep = "";
        runState.LastProjectAttachSummary = "";
        runState.RecentObservedToolNames = [];
        runState.RecentObservedChainTemplateIds = [];
        runState.ExecutedToolCalls = [];
        runState.LastResolvedTargetFileType = "";
        runState.LastResolvedTargetRole = "";
        runState.LastResolvedTargetProjectName = "";
        runState.LastResolvedTargetNamespaceHint = "";
        runState.LastResolvedTargetIdentityTrace = "";
    }

    private static void ResetRuntimeForFreshRun(TaskboardPlanRunStateRecord runState)
    {
        var now = DateTime.UtcNow.ToString("O");
        runState.StartedUtc = now;
        runState.PlanStatus = TaskboardPlanRuntimeStatus.Active;
        runState.CurrentBatchId = "";
        runState.CurrentWorkItemId = "";
        runState.CurrentRunPhaseCode = "";
        runState.CurrentRunPhaseText = "";
        runState.LatestStepSummary = "";
        runState.LastCompletedStepSummary = "";
        runState.LastCompletedWorkItemId = runState.Batches
            .SelectMany(batch => batch.WorkItems)
            .Where(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed)
            .Select(item => item.WorkItemId)
            .LastOrDefault() ?? "";
        runState.LastCompletedWorkItemTitle = "";
        runState.LastCompletedWorkFamily = "";
        runState.LastCompletedPhraseFamily = "";
        runState.LastCompletedOperationKind = "";
        runState.LastCompletedStackFamily = "";
        runState.SatisfactionSkipCount = 0;
        runState.RepeatedFileTouchesAvoidedCount = 0;
        runState.LastSatisfactionSkipWorkItemId = "";
        runState.LastSatisfactionSkipWorkItemTitle = "";
        runState.LastSatisfactionSkipReasonCode = "";
        runState.LastSatisfactionSkipEvidenceSummary = "";
        runState.LastSatisfactionCheckSummary = "";

        foreach (var batch in runState.Batches)
        {
            batch.CurrentWorkItemId = "";
            batch.LastExecutionGoalSummary = "";
            if (batch.Status is not TaskboardBatchRuntimeStatus.Completed and not TaskboardBatchRuntimeStatus.Skipped)
            {
                batch.Status = TaskboardBatchRuntimeStatus.Pending;
                batch.LastResultSummary = "";
            }

            foreach (var item in batch.WorkItems)
            {
                if (item.Status is TaskboardWorkItemRuntimeStatus.Passed or TaskboardWorkItemRuntimeStatus.Skipped)
                {
                    RefreshWorkItemDerivedFields(item);
                    continue;
                }

                item.Status = TaskboardWorkItemRuntimeStatus.Pending;
                item.LastResultKind = "";
                item.LastResultSummary = "";
                item.LastExecutionGoalResolution = new TaskboardExecutionGoalResolution();
                item.UpdatedUtc = now;
                RefreshWorkItemDerivedFields(item);
            }

            batch.CompletedWorkItemCount = batch.WorkItems.Count(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed);
            batch.TotalWorkItemCount = batch.WorkItems.Count(item => item.Status != TaskboardWorkItemRuntimeStatus.Skipped || item.IsDecomposedItem);
        }

        runState.CompletedWorkItemCount = runState.Batches.Sum(batch => batch.CompletedWorkItemCount);
        runState.TotalWorkItemCount = runState.Batches.Sum(batch => batch.TotalWorkItemCount);
        var lastCompletedItem = runState.Batches
            .SelectMany(batch => batch.WorkItems)
            .Where(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed)
            .OrderBy(item => item.UpdatedUtc, StringComparer.OrdinalIgnoreCase)
            .LastOrDefault();
        runState.LastCompletedWorkItemTitle = lastCompletedItem?.Title ?? "";
        runState.LastCompletedWorkFamily = lastCompletedItem?.WorkFamily ?? "";
        runState.LastCompletedPhraseFamily = lastCompletedItem?.PhraseFamily ?? "";
        runState.LastCompletedOperationKind = lastCompletedItem?.OperationKind ?? "";
        runState.LastCompletedStackFamily = lastCompletedItem?.TargetStack ?? "";
        runState.LastBlockerWorkFamily = "";
        runState.LastBlockerPhraseFamily = "";
        runState.LastBlockerOperationKind = "";
        runState.LastBlockerStackFamily = "";
        runState.LastSupportCoverageWorkItemId = "";
        runState.LastSupportCoverageWorkItemTitle = "";
        runState.LastSupportCoverageSummary = "";
        runState.LastContradictionGuardWorkItemId = "";
        runState.LastContradictionGuardWorkItemTitle = "";
        runState.LastContradictionGuardReasonCode = "";
        runState.LastContradictionGuardSummary = "";
        runState.LastFollowupBatchId = "";
        runState.LastFollowupBatchTitle = "";
        runState.LastFollowupWorkItemId = "";
        runState.LastFollowupWorkItemTitle = "";
        runState.LastFollowupSelectionReason = "";
        runState.LastFollowupWorkFamily = "";
        runState.LastFollowupPhraseFamily = "";
        runState.LastFollowupOperationKind = "";
        runState.LastFollowupStackFamily = "";
        runState.LastFollowupPhraseFamilyReasonCode = "";
        runState.LastFollowupOperationKindReasonCode = "";
        runState.LastFollowupStackFamilyReasonCode = "";
        runState.LastFollowupResolutionSummary = "";
        runState.LastFailureOutcomeType = "";
        runState.LastFailureFamily = "";
        runState.LastFailureErrorCode = "";
        runState.LastFailureNormalizedSummary = "";
        runState.LastFailureTargetPath = "";
        runState.LastFailureSourcePath = "";
        runState.LastFailureRepairContextPath = "";
        runState.LastPostChainReconciliationSummary = "";
        ClearTerminalSummaryState(runState);
        if (runState.Batches.Count > 0
            && runState.Batches.All(batch => batch.Status is TaskboardBatchRuntimeStatus.Completed or TaskboardBatchRuntimeStatus.Skipped))
        {
            runState.PlanStatus = TaskboardPlanRuntimeStatus.Completed;
        }
    }

    private static void ApplyRuntimeSnapshotMetadata(
        TaskboardPlanRunStateRecord runState,
        TaskboardRuntimeStateAssessment assessment,
        string statusCode,
        string summary)
    {
        runState.RuntimeStateVersion = assessment.CurrentVersion;
        runState.RuntimeStateFingerprint = assessment.CurrentFingerprint;
        runState.RuntimeStateStatusCode = statusCode;
        runState.RuntimeStateSummary = summary;
        runState.RuntimeStateInvalidationReason = assessment.InvalidationReason;
        runState.RuntimeStateComputedUtc = DateTime.UtcNow.ToString("O");
    }

    private static void ApplyPhraseFamilyTrace(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemDecompositionRecord decomposition)
    {
        runState.LastCanonicalOperationKind = decomposition.PhraseFamilyResolution.CanonicalOperationKind;
        runState.LastCanonicalTargetPath = decomposition.PhraseFamilyResolution.CanonicalTargetPath;
        runState.LastCanonicalProjectName = decomposition.PhraseFamilyResolution.CanonicalProjectName;
        runState.LastCanonicalTemplateHint = decomposition.PhraseFamilyResolution.CanonicalTemplateHint;
        runState.LastCanonicalRoleHint = decomposition.PhraseFamilyResolution.CanonicalRoleHint;
        runState.LastCanonicalizationTrace = decomposition.PhraseFamilyResolution.CanonicalizationTrace;
        runState.LastPhraseFamilyRawPhraseText = decomposition.PhraseFamilyResolution.RawPhraseText;
        runState.LastPhraseFamilyNormalizedPhraseText = decomposition.PhraseFamilyResolution.NormalizedPhraseText;
        runState.LastPhraseFamilyClosestKnownFamilyGroup = decomposition.PhraseFamilyResolution.ClosestKnownFamilyGroup;
        runState.LastPhraseFamilyResolutionPathTrace = decomposition.PhraseFamilyResolution.ResolutionPathTrace;
        runState.LastPhraseFamilyTerminalResolverStage = decomposition.PhraseFamilyResolution.TerminalResolverStage;
        runState.LastPhraseFamilyBuilderOperationStatus = decomposition.PhraseFamilyResolution.BuilderOperationResolutionStatus;
        runState.LastPhraseFamilyLaneResolutionStatus = decomposition.PhraseFamilyResolution.LaneResolutionStatus;
        runState.LastPhraseFamily = decomposition.PhraseFamily;
        runState.LastPhraseFamilySource = decomposition.PhraseFamilySource;
        runState.LastPhraseFamilyResolutionSummary = decomposition.PhraseFamilyResolutionSummary;
        runState.LastPhraseFamilyCandidates = [.. decomposition.PhraseFamilyCandidates];
        runState.LastPhraseFamilyDeterministicCandidate = decomposition.PhraseFamilyDeterministicCandidate;
        runState.LastPhraseFamilyAdvisoryCandidate = decomposition.PhraseFamilyAdvisoryCandidate;
        runState.LastPhraseFamilyBlockerCode = decomposition.PhraseFamilyBlockerCode;
        runState.LastPhraseFamilyTieBreakRuleId = decomposition.PhraseFamilyTieBreakRuleId;
        runState.LastPhraseFamilyTieBreakSummary = decomposition.PhraseFamilyTieBreakSummary;
    }

    private static void ApplyMaintenanceBaselineState(
        TaskboardPlanRunStateRecord runState,
        TaskboardMaintenanceBaselineRecord baseline)
    {
        runState.LastMaintenanceBaselineSummary = baseline.Summary;
        runState.LastMaintenanceBaselineSolutionPath = baseline.PrimarySolutionPath;
        runState.LastMaintenanceAllowedRoots = [.. baseline.AllowedMutationRoots];
        runState.LastMaintenanceExcludedRoots = [.. baseline.ExcludedGeneratedRoots];
        if (!baseline.IsMaintenanceMode || baseline.BaselineResolved)
        {
            runState.LastMaintenanceGuardReasonCode = "";
            runState.LastMaintenanceGuardSummary = "";
        }
    }

    private static void ApplyMaintenanceGuardState(
        TaskboardPlanRunStateRecord runState,
        TaskboardMaintenanceMutationGuardResult guard)
    {
        if (!guard.Applies)
            return;

        runState.LastMaintenanceGuardReasonCode = guard.Allowed ? "" : guard.ReasonCode;
        runState.LastMaintenanceGuardSummary = guard.Allowed ? "" : guard.Summary;
        if (!string.IsNullOrWhiteSpace(guard.BaselineSolutionPath))
            runState.LastMaintenanceBaselineSolutionPath = guard.BaselineSolutionPath;
        if (guard.AllowedRoots.Count > 0)
            runState.LastMaintenanceAllowedRoots = [.. guard.AllowedRoots];
        if (guard.ExcludedRoots.Count > 0)
            runState.LastMaintenanceExcludedRoots = [.. guard.ExcludedRoots];
    }

    private void RefreshWorkFamilies(TaskboardBatchRunStateRecord batch)
    {
        foreach (var item in batch.WorkItems)
        {
            RefreshWorkItemDerivedFields(item);

            var family = _workFamilyResolutionService.Resolve(new TaskboardRunWorkItem
            {
                WorkItemId = item.WorkItemId,
                Ordinal = item.Ordinal,
                DisplayOrdinal = item.DisplayOrdinal,
                Title = item.Title,
                Summary = item.Summary,
                PromptText = item.PromptText,
                IsDecomposedItem = item.IsDecomposedItem,
                SourceWorkItemId = item.SourceWorkItemId,
                OperationKind = item.OperationKind,
                TargetStack = item.TargetStack,
                WorkFamily = item.WorkFamily,
                ExpectedArtifact = item.ExpectedArtifact,
                ValidationHint = item.ValidationHint,
                PhraseFamily = item.PhraseFamily,
                TemplateId = item.TemplateId,
                TemplateCandidateIds = [.. item.TemplateCandidateIds],
                DirectToolRequest = item.DirectToolRequest?.Clone()
            });
            if (IsMissingOrUnknown(item.WorkFamily))
            {
                item.WorkFamily = family.FamilyId;
            }
        }
    }

    private void UpdateCoverageState(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardPlanRunStateRecord runState,
        RamDbService ramDbService)
    {
        var map = _laneCoverageMapService.Build(workspaceRoot, activeImport, runState);
        runState.LastCoverageMapSummary = map.Summary;
        runState.LastNextWorkFamily = FirstMeaningful(runState.LastFollowupWorkFamily, map.NextWorkFamily);
        if (!string.IsNullOrWhiteSpace(runState.CurrentWorkItemId))
        {
            runState.LastWorkFamily = FirstMeaningful(
                map.CurrentWorkFamily,
                map.Entries.FirstOrDefault(entry => entry.IsCurrent)?.WorkFamily,
                runState.LastWorkFamily);
        }
        if (map.Entries.FirstOrDefault(entry => entry.IsCurrent)?.WorkFamilySource is { Length: > 0 } currentSource
            && !IsMissingOrUnknown(currentSource))
        {
            runState.LastWorkFamilySource = currentSource;
        }
        _artifactStore.SaveLaneCoverageMapArtifact(ramDbService, workspaceRoot, map);
    }

    private void RefreshSelectedWorkItem(
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord workItem)
    {
        _workItemStateRefreshService.Refresh(runState, batch, workItem);
        if (IsMissingOrUnknown(workItem.WorkFamily))
        {
            var family = _workFamilyResolutionService.Resolve(_workItemStateRefreshService.ToRunWorkItem(workItem));
            workItem.WorkFamily = family.FamilyId;
        }

        var headingPolicy = TaskboardStructuralHeadingService.Classify(workItem.Title);
        RecordHeadingPolicyDecision(
            runState,
            workItem,
            headingPolicy,
            TaskboardStructuralHeadingService.IsNonActionableHeading(headingPolicy)
                ? TaskboardStructuralHeadingService.BuildSupportCoverageReason(headingPolicy)
                : TaskboardStructuralHeadingService.BuildActionableReason(headingPolicy));
    }

    private void ReconcilePostSuccess(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord workItem,
        RamDbService ramDbService)
    {
        PromoteRecoveredSourceWorkItem(runState, workItem);

        var reconciliation = _postChainReconciliationService.Reconcile(
            workspaceRoot,
            activeImport,
            runState,
            batch,
            workItem,
            ramDbService);
        _artifactStore.SavePostChainReconciliationArtifact(ramDbService, workspaceRoot, activeImport.ImportId, reconciliation.Reconciliation);
        _artifactStore.SaveFollowUpWorkItemArtifact(ramDbService, workspaceRoot, activeImport.ImportId, reconciliation.FollowUpSelection);
        _artifactStore.SaveFollowUpResolutionArtifact(ramDbService, workspaceRoot, activeImport.ImportId, reconciliation.FollowUpResolution);
        runState.CurrentRunPhaseCode = "reconciling_followup";
        runState.CurrentRunPhaseText = "Reconciling follow-up work after successful execution.";
        runState.LatestStepSummary = reconciliation.Reconciliation.Summary;
        AddEvent(runState, "post_chain_reconciled", batch.BatchId, workItem.WorkItemId, reconciliation.Reconciliation.Summary);
    }

    private static void PromoteRecoveredSourceWorkItem(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord successfulWorkItem)
    {
        if (!string.Equals(successfulWorkItem.WorkFamily, "build_repair", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(successfulWorkItem.SourceWorkItemId))
        {
            return;
        }

        var hasRepairMutationProof =
            (!string.IsNullOrWhiteSpace(runState.LastMutationUtc)
             && !string.IsNullOrWhiteSpace(runState.LastVerificationAfterMutationOutcome))
            || runState.ExecutedToolCalls.Any(call =>
                string.Equals(call.Stage, "completed", StringComparison.OrdinalIgnoreCase)
                && call.MutationObserved)
               && runState.ExecutedToolCalls.Any(call =>
                   string.Equals(call.Stage, "completed", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(call.ToolName, "verify_patch_draft", StringComparison.OrdinalIgnoreCase));
        var hasVerifiedRepairClosure = HasVerifiedRepairClosureProof(runState);
        if (!hasRepairMutationProof && !hasVerifiedRepairClosure)
            return;

        var sourceBatch = runState.Batches.FirstOrDefault(batch =>
            batch.WorkItems.Any(item => string.Equals(item.WorkItemId, successfulWorkItem.SourceWorkItemId, StringComparison.OrdinalIgnoreCase)));
        var sourceWorkItem = sourceBatch?.WorkItems.FirstOrDefault(item =>
            string.Equals(item.WorkItemId, successfulWorkItem.SourceWorkItemId, StringComparison.OrdinalIgnoreCase));
        if (sourceBatch is null
            || sourceWorkItem is null
            || sourceWorkItem == successfulWorkItem
            || sourceWorkItem.Status == TaskboardWorkItemRuntimeStatus.Passed)
        {
            return;
        }

        sourceWorkItem.Status = TaskboardWorkItemRuntimeStatus.Passed;
        sourceWorkItem.LastResultKind = "recovered_via_build_repair";
        sourceWorkItem.LastResultSummary = hasRepairMutationProof
            ? runState.LastMutationTouchedFilePaths.Count == 0
                ? $"Recovered via `{successfulWorkItem.Title}` after bounded repair verification succeeded."
                : $"Recovered via `{successfulWorkItem.Title}` after bounded repair verification succeeded for {string.Join(", ", runState.LastMutationTouchedFilePaths)}."
            : $"Recovered via `{successfulWorkItem.Title}` after rebuild-first symbol reconciliation verified the bounded repair without a local file edit.";
        sourceWorkItem.UpdatedUtc = DateTime.UtcNow.ToString("O");
        sourceBatch.LastResultSummary = sourceWorkItem.LastResultSummary;
        sourceBatch.CurrentWorkItemId = "";
        sourceBatch.CompletedWorkItemCount = sourceBatch.WorkItems.Count(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed);
        sourceBatch.Status = sourceBatch.WorkItems.All(item => item.Status is TaskboardWorkItemRuntimeStatus.Passed or TaskboardWorkItemRuntimeStatus.Skipped)
            ? TaskboardBatchRuntimeStatus.Completed
            : TaskboardBatchRuntimeStatus.Pending;
        runState.CompletedWorkItemCount = runState.Batches.Sum(current => current.WorkItems.Count(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed));
        if (runState.Batches.All(current => current.Status is TaskboardBatchRuntimeStatus.Completed or TaskboardBatchRuntimeStatus.Skipped))
        {
            runState.PlanStatus = TaskboardPlanRuntimeStatus.Completed;
            runState.CurrentRunPhaseCode = "completed";
            runState.CurrentRunPhaseText = "Active plan completed.";
        }
        AddEvent(
            runState,
            "repair_followup_closed_source",
            sourceBatch.BatchId,
            sourceWorkItem.WorkItemId,
            $"Recovered `{sourceWorkItem.Title}` via `{successfulWorkItem.Title}`.");
    }

    private bool TryPromoteBuildFailureRecovery(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord failedWorkItem,
        TaskboardExecutionOutcome outcome,
        RamDbService ramDbService)
    {
        if (string.Equals(failedWorkItem.WorkFamily, "build_repair", StringComparison.OrdinalIgnoreCase))
            return false;

        var recovery = _buildFailureRecoveryService.TryPromote(
            workspaceRoot,
            activeImport,
            runState,
            batch,
            failedWorkItem,
            ramDbService);
        if (!recovery.Promoted || recovery.FollowUpWorkItem is null)
            return false;

        batch.Status = TaskboardBatchRuntimeStatus.Blocked;
        batch.CurrentWorkItemId = "";
        runState.PlanStatus = TaskboardPlanRuntimeStatus.Blocked;
        runState.CurrentBatchId = "";
        runState.CurrentWorkItemId = "";
        ApplyFailureRecoveryState(runState, recovery, outcome.Summary);

        _artifactStore.SaveFollowUpWorkItemArtifact(ramDbService, workspaceRoot, activeImport.ImportId, recovery.FollowUpSelection);
        _artifactStore.SaveFollowUpResolutionArtifact(ramDbService, workspaceRoot, activeImport.ImportId, recovery.FollowUpResolution);

        var recoveryPhase = string.Equals(recovery.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
            ? "test_failure_recovery"
            : "build_failure_recovery";
        var recoveryPhaseText = string.Equals(recovery.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
            ? "Promoted failing test execution into bounded repair work."
            : "Promoted failed workspace verification into bounded repair work.";
        var recoveryEventKind = string.Equals(recovery.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
            ? "test_failure_recovery_promoted"
            : "build_failure_recovery_promoted";

        AssignCurrentBlocker(
            workspaceRoot,
            activeImport,
            runState,
            batch,
            recovery.FollowUpWorkItem,
            recoveryPhase,
            recovery.Summary,
            ramDbService);

        ReapplyFailureRecoveryFollowupState(runState, recovery);
        runState.CurrentRunPhaseCode = recoveryPhase;
        runState.CurrentRunPhaseText = recoveryPhaseText;
        runState.LatestStepSummary = recovery.Summary;
        AddEvent(runState, recoveryEventKind, batch.BatchId, recovery.FollowUpWorkItem.WorkItemId, recovery.Summary);
        return true;
    }

    private void AssignCurrentBlocker(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord workItem,
        string phase,
        string summary,
        RamDbService ramDbService)
    {
        var assignment = _finalBlockerAssignmentService.Assign(runState, batch, workItem, phase, summary);
        _artifactStore.SaveFinalBlockerAssignmentArtifact(ramDbService, workspaceRoot, activeImport.ImportId, assignment);
        runState.CurrentRunPhaseCode = "blocked";
        runState.CurrentRunPhaseText = "Blocked on the current unresolved work item.";
        runState.LatestStepSummary = summary;
    }

    private TaskboardRepairContinuationDecision TryAdvanceRepairContinuation(
        string workspaceRoot,
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionOutcome outcome,
        string baselineUtc,
        RamDbService ramDbService)
    {
        if (!string.Equals(workItem.WorkFamily, "build_repair", StringComparison.OrdinalIgnoreCase))
        {
            return TaskboardRepairContinuationDecision.None;
        }

        var evidence = LoadRepairContinuationEvidence(workspaceRoot, runState, workItem, ramDbService, ParseUtc(baselineUtc));
        if (evidence.HasVerifiedMutationProof
            && evidence.Verification is not null)
        {
            var verification = evidence.Verification;
            var verificationSummary = evidence.IsReconciliationOnly
                ? FirstNonEmpty(
                    verification.Explanation,
                    verification.AfterSummary,
                    evidence.VerificationArtifact?.Summary,
                    "Repair verification confirmed the bounded symbol reconciliation.")
                : FirstNonEmpty(
                    verification.Explanation,
                    verification.AfterSummary,
                    evidence.VerificationArtifact?.Summary,
                    "Repair verification confirmed the latest bounded fix.");
            ApplyRepairContinuationRuntimeTruth(
                runState,
                evidence,
                evidence.IsReconciliationOnly ? "verified_after_symbol_reconciliation" : "verified_after_mutation",
                verificationSummary);
            return new TaskboardRepairContinuationDecision
            {
                OverrideOutcome = new TaskboardExecutionOutcome
                {
                    ResultKind = TaskboardWorkItemResultKind.Passed,
                    ResultClassification = "verified_fixed",
                    Summary = verificationSummary,
                    ExecutionAttempted = true
                }
            };
        }

        if (evidence.Verification is not null)
        {
            ApplyRepairContinuationRuntimeTruth(
                runState,
                evidence,
                "verification_without_mutation",
                "Repair verification exists, but no fresh patch apply was recorded before it.");
            return TaskboardRepairContinuationDecision.None;
        }

        if (evidence.ApplyResult is not null)
        {
            var continueSummary = evidence.IsReconciliationOnly
                ? $"Repair reconciliation completed for `{workItem.Title}`; continuing with verify_patch_draft."
                : $"Repair apply completed for `{workItem.Title}`; continuing with verify_patch_draft.";
            ApplyRepairContinuationRuntimeTruth(
                runState,
                evidence,
                evidence.IsReconciliationOnly ? "continue_symbol_verify" : "continue_verify",
                continueSummary);
            return new TaskboardRepairContinuationDecision
            {
                ShouldContinueCurrentItem = true,
                NextToolRequest = BuildRepairContinuationToolRequest(
                    "verify_patch_draft",
                    FirstNonEmpty(evidence.ApplyResult.Draft.TargetFilePath, evidence.ApplyResult.Draft.TargetProjectPath),
                    evidence.ApplyResult.Draft.TargetProjectPath,
                    "Continue bounded repair verification after applying the latest patch draft."),
                PhaseCode = "repair_continuation",
                PhaseText = evidence.IsReconciliationOnly
                    ? "Continuing bounded verification after rebuild-first symbol reconciliation."
                    : "Continuing deterministic repair verification after the latest patch apply.",
                EventKind = evidence.IsReconciliationOnly ? "repair_continuation_symbol_verify" : "repair_continuation_verify",
                Summary = continueSummary
            };
        }

        if (evidence.Draft is not null && evidence.Draft.CanApplyLocally)
        {
            ApplyRepairContinuationRuntimeTruth(
                runState,
                evidence,
                "continue_apply",
                $"Repair preview completed for `{workItem.Title}`; continuing with apply_patch_draft.");
            return new TaskboardRepairContinuationDecision
            {
                ShouldContinueCurrentItem = true,
                NextToolRequest = BuildRepairContinuationToolRequest(
                    "apply_patch_draft",
                    FirstNonEmpty(evidence.Draft.TargetFilePath, evidence.Draft.TargetProjectPath),
                    evidence.Draft.TargetProjectPath,
                    "Continue deterministic repair apply after previewing the latest locally safe patch draft."),
                PhaseCode = "repair_continuation",
                PhaseText = "Continuing deterministic repair apply after previewing the latest patch draft.",
                EventKind = "repair_continuation_apply",
                Summary = $"Repair preview completed for `{workItem.Title}`; continuing with apply_patch_draft."
            };
        }

        if (evidence.Draft is not null)
        {
            ApplyRepairContinuationRuntimeTruth(
                runState,
                evidence,
                "stopped_inspect_only",
                $"Repair preview for `{workItem.Title}` stopped after inspect_only classification because no deterministic local patch was available.");
        }

        return TaskboardRepairContinuationDecision.None;
    }

    private TaskboardRepairContinuationDecision TryResumeRepairContinuation(
        string workspaceRoot,
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord workItem,
        RamDbService ramDbService)
    {
        if (!string.Equals(workItem.WorkFamily, "build_repair", StringComparison.OrdinalIgnoreCase))
            return TaskboardRepairContinuationDecision.None;

        var evidence = LoadRepairContinuationEvidence(workspaceRoot, runState, workItem, ramDbService, ParseUtc(runState.StartedUtc));
        if (!evidence.HasAny)
            return TaskboardRepairContinuationDecision.None;

        if (evidence.HasVerifiedMutationProof
            && evidence.Verification is not null)
        {
            var verificationSummary = evidence.IsReconciliationOnly
                ? "Accepted recorded repair verification after rebuild-first symbol reconciliation and resumed the active plan."
                : "Accepted recorded repair verification with fresh mutation proof and resumed the active plan.";
            ApplyRepairContinuationRuntimeTruth(
                runState,
                evidence,
                evidence.IsReconciliationOnly ? "verified_after_symbol_reconciliation" : "verified_after_mutation",
                verificationSummary);
            return new TaskboardRepairContinuationDecision
            {
                OverrideOutcome = new TaskboardExecutionOutcome
                {
                    ResultKind = TaskboardWorkItemResultKind.Passed,
                    ResultClassification = "verified_fixed",
                    Summary = FirstNonEmpty(
                        evidence.Verification.Explanation,
                        evidence.Verification.AfterSummary,
                        evidence.VerificationArtifact?.Summary,
                        "Repair verification confirmed the latest bounded fix."),
                    ExecutionAttempted = true
                },
                PhaseCode = "repair_continuation",
                PhaseText = verificationSummary,
                EventKind = evidence.IsReconciliationOnly ? "repair_resume_symbol_verified" : "repair_resume_verified",
                Summary = evidence.IsReconciliationOnly
                    ? $"Accepted recorded repair verification for `{workItem.Title}` after rebuild-first symbol reconciliation and resumed the active plan."
                    : $"Accepted recorded repair verification for `{workItem.Title}` after a recorded patch apply and resumed the active plan."
            };
        }

        if (evidence.Verification is not null)
        {
            ApplyRepairContinuationRuntimeTruth(
                runState,
                evidence,
                "verification_without_mutation",
                "Recorded repair verification exists, but no fresh patch apply was recorded before it.");
            return TaskboardRepairContinuationDecision.None;
        }

        if (evidence.ApplyResult is not null)
        {
            var continueSummary = evidence.IsReconciliationOnly
                ? $"Found a recorded rebuild-first symbol reconciliation for `{workItem.Title}`; resuming with verify_patch_draft."
                : $"Found a recorded patch apply for `{workItem.Title}`; resuming with verify_patch_draft.";
            ApplyRepairContinuationRuntimeTruth(
                runState,
                evidence,
                evidence.IsReconciliationOnly ? "continue_symbol_verify" : "continue_verify",
                continueSummary);
            return new TaskboardRepairContinuationDecision
            {
                ShouldContinueCurrentItem = true,
                NextToolRequest = BuildRepairContinuationToolRequest(
                    "verify_patch_draft",
                    FirstNonEmpty(evidence.ApplyResult.Draft.TargetFilePath, evidence.ApplyResult.Draft.TargetProjectPath),
                    evidence.ApplyResult.Draft.TargetProjectPath,
                    "Resume deterministic repair verification from the latest recorded patch apply result."),
                PhaseCode = "repair_continuation",
                PhaseText = evidence.IsReconciliationOnly
                    ? "Resuming bounded verification after rebuild-first symbol reconciliation."
                    : "Resuming deterministic repair verification from the latest recorded patch apply result.",
                EventKind = evidence.IsReconciliationOnly ? "repair_resume_symbol_verify" : "repair_resume_verify",
                Summary = continueSummary
            };
        }

        if (evidence.Draft is not null && evidence.Draft.CanApplyLocally)
        {
            ApplyRepairContinuationRuntimeTruth(
                runState,
                evidence,
                "continue_apply",
                $"Found a recorded patch draft for `{workItem.Title}`; resuming with apply_patch_draft.");
            return new TaskboardRepairContinuationDecision
            {
                ShouldContinueCurrentItem = true,
                NextToolRequest = BuildRepairContinuationToolRequest(
                    "apply_patch_draft",
                    FirstNonEmpty(evidence.Draft.TargetFilePath, evidence.Draft.TargetProjectPath),
                    evidence.Draft.TargetProjectPath,
                    "Resume deterministic repair apply from the latest recorded locally safe patch draft."),
                PhaseCode = "repair_continuation",
                PhaseText = "Resuming deterministic repair apply from the latest recorded patch draft.",
                EventKind = "repair_resume_apply",
                Summary = $"Found a recorded patch draft for `{workItem.Title}`; resuming with apply_patch_draft."
            };
        }

        if (evidence.Draft is not null)
        {
            ApplyRepairContinuationRuntimeTruth(
                runState,
                evidence,
                "stopped_inspect_only",
                $"Recorded repair preview for `{workItem.Title}` remains inspect_only because no deterministic local patch was available.");
        }

        return TaskboardRepairContinuationDecision.None;
    }

    private static void ApplyRepairContinuationRuntimeTruth(
        TaskboardPlanRunStateRecord runState,
        TaskboardRepairContinuationEvidence evidence,
        string status,
        string summary)
    {
        runState.LastRepairTargetPath = FirstNonEmpty(
            evidence.ApplyResult?.Draft.TargetFilePath,
            evidence.Draft?.TargetFilePath,
            evidence.Verification?.ResolvedTarget,
            evidence.ApplyResult?.Draft.TargetProjectPath,
            evidence.Draft?.TargetProjectPath);
        runState.LastRepairDraftKind = FirstNonEmpty(evidence.Draft?.DraftKind);
        runState.LastRepairLocalPatchAvailable = evidence.Draft?.CanApplyLocally ?? false;
        runState.LastRepairReferencedSymbolName = FirstNonEmpty(evidence.Draft?.ReferencedSymbolName);
        runState.LastRepairReferencedMemberName = FirstNonEmpty(evidence.Draft?.ReferencedMemberName);
        runState.LastRepairSymbolRecoveryStatus = FirstNonEmpty(evidence.Draft?.SymbolRecoveryStatus);
        runState.LastRepairSymbolRecoverySummary = FirstNonEmpty(evidence.Draft?.SymbolRecoverySummary);
        runState.LastRepairSymbolRecoveryCandidatePath = FirstNonEmpty(evidence.Draft?.SymbolRecoveryCandidatePath);
        runState.LastRepairContinuationStatus = status;
        runState.LastRepairContinuationSummary = summary;
    }

    private static void ApplyFailureRecoveryState(
        TaskboardPlanRunStateRecord runState,
        TaskboardBuildFailureRecoveryResult recovery,
        string fallbackSummary)
    {
        runState.LastFailureOutcomeType = FirstNonEmpty(recovery.FailureKind, "build_failure");
        runState.LastFailureFamily = recovery.FailureFamily;
        runState.LastFailureErrorCode = recovery.FailureErrorCode;
        runState.LastFailureNormalizedSummary = FirstNonEmpty(recovery.FailureNormalizedSummary, fallbackSummary);
        runState.LastFailureTargetPath = recovery.FailureTargetPath;
        runState.LastFailureSourcePath = recovery.FailureSourcePath;
        runState.LastFailureRepairContextPath = recovery.RepairContextPath;
    }

    private static void ReapplyFailureRecoveryFollowupState(
        TaskboardPlanRunStateRecord runState,
        TaskboardBuildFailureRecoveryResult recovery)
    {
        runState.LastFollowupBatchId = recovery.FollowUpSelection.BatchId;
        runState.LastFollowupBatchTitle = recovery.FollowUpSelection.BatchTitle;
        runState.LastFollowupWorkItemId = recovery.FollowUpSelection.WorkItemId;
        runState.LastFollowupWorkItemTitle = recovery.FollowUpSelection.WorkItemTitle;
        runState.LastFollowupSelectionReason = recovery.FollowUpSelection.SelectionReason;
        runState.LastFollowupWorkFamily = recovery.FollowUpResolution.WorkFamily;
        runState.LastFollowupPhraseFamily = recovery.FollowUpResolution.PhraseFamily;
        runState.LastFollowupOperationKind = recovery.FollowUpResolution.OperationKind;
        runState.LastFollowupStackFamily = recovery.FollowUpResolution.StackFamily;
        runState.LastFollowupPhraseFamilyReasonCode = recovery.FollowUpResolution.PhraseFamilyReasonCode;
        runState.LastFollowupOperationKindReasonCode = recovery.FollowUpResolution.OperationKindReasonCode;
        runState.LastFollowupStackFamilyReasonCode = recovery.FollowUpResolution.StackFamilyReasonCode;
        runState.LastFollowupResolutionSummary = recovery.FollowUpResolution.Summary;
        runState.LastNextWorkFamily = FirstMeaningful(recovery.FollowUpResolution.WorkFamily, runState.LastNextWorkFamily);
    }

    private static ToolRequest BuildRepairContinuationToolRequest(
        string toolName,
        string preferredPath,
        string activeTargetPath,
        string reason)
    {
        var request = new ToolRequest
        {
            ToolName = toolName,
            PreferredChainTemplateName = "repair_single_step",
            Reason = reason,
            ExecutionSourceType = ExecutionSourceType.BuildTool,
            ExecutionSourceName = "taskboard_repair_continuation",
            IsAutomaticTrigger = true,
            ExecutionAllowed = true,
            ExecutionPolicyMode = "taskboard_auto_run",
            ExecutionBuildFamily = "repair"
        };

        if (!string.IsNullOrWhiteSpace(preferredPath))
            request.Arguments["path"] = preferredPath;
        if (!string.IsNullOrWhiteSpace(activeTargetPath))
            request.Arguments["active_target"] = activeTargetPath;

        return request;
    }

    public string BuildRuntimeStatusBanner(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is null)
            return "Runtime: no run state recorded.";

        return $"Runtime: {FormatPlanStatus(runState.PlanStatus)}.";
    }

    public string BuildRuntimeProgressBanner(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is null)
            return "Progress: (none)";

        var batch = runState.Batches.FirstOrDefault(current =>
            string.Equals(current.BatchId, runState.CurrentBatchId, StringComparison.OrdinalIgnoreCase));
        var item = batch?.WorkItems.FirstOrDefault(current =>
            string.Equals(current.WorkItemId, runState.CurrentWorkItemId, StringComparison.OrdinalIgnoreCase));
        if (batch is not null && item is not null)
        {
            return $"Progress: Batch {batch.BatchNumber} / Work Item {FirstNonEmpty(item.DisplayOrdinal, item.Ordinal.ToString())} running. Completed {runState.CompletedWorkItemCount}/{runState.TotalWorkItemCount}.";
        }

        return $"Progress: completed {runState.CompletedWorkItemCount}/{runState.TotalWorkItemCount} work item(s).";
    }

    public string BuildRuntimeLastResultBanner(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is null)
            return "Last result: (none)";

        if (!string.IsNullOrWhiteSpace(runState.LastBlockerReason))
            return $"Last result: {FormatResultKind(runState.LastResultKind)} — {runState.LastBlockerReason}";

        if (!string.IsNullOrWhiteSpace(runState.LastResultSummary))
            return $"Last result: {FormatResultKind(runState.LastResultKind)} — {runState.LastResultSummary}";

        return "Last result: (none)";
    }

    private async Task<string> BuildForensicsSummaryAsync(
        string workspaceRoot,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionGoalResolution goalResolution,
        AppSettings? settings,
        string endpoint,
        string selectedModel,
        CancellationToken cancellationToken)
    {
        if (_forensicsAgentService is null
            || settings is null
            || goalResolution is null
            || goalResolution.Blocker.Code == TaskboardExecutionGoalBlockerCode.None)
        {
            return "";
        }

        var request = new ForensicsAgentRequestPayload
        {
            WorkItemTitle = workItem.Title,
            GoalKind = goalResolution.GoalKind.ToString().ToLowerInvariant(),
            BlockerCode = goalResolution.Blocker.Code.ToString().ToLowerInvariant(),
            BlockerMessage = goalResolution.Blocker.Message,
            StackFamily = goalResolution.TargetStack,
            PhraseFamily = FirstNonEmpty(goalResolution.PhraseFamily, workItem.PhraseFamily),
            TemplateId = FirstNonEmpty(goalResolution.TemplateId, workItem.TemplateId),
            CandidateTemplateIds = goalResolution.TemplateCandidateIds.Count > 0
                ? [.. goalResolution.TemplateCandidateIds]
                : [.. workItem.TemplateCandidateIds],
            EvidenceLines =
            [
                .. goalResolution.Evidence.Select(evidence => $"{evidence.Code}:{evidence.Value}")
            ],
            AllowedMissingPieceCategories =
            [
                "tool",
                "chain_template",
                "argument",
                "stack_mapping",
                "phrase_family",
                "template_resolution",
                "safety_boundary"
            ],
            AllowedNextActionCategories =
            [
                "add_tool",
                "add_chain_template",
                "provide_argument",
                "clarify_stack",
                "manual_action",
                "inspect_workspace"
            ]
        };

        var result = await _forensicsAgentService.ExplainAsync(
            endpoint,
            selectedModel,
            settings,
            workspaceRoot,
            request,
            cancellationToken);
        return result.Accepted
            ? FirstNonEmpty(result.Payload.Explanation, goalResolution.Blocker.Message)
            : "";
    }

    private static string BuildTerminalMessage(
        string actionName,
        TaskboardPlanRunStateRecord runState,
        string summary,
        string selectedBatchId)
    {
        var prefix = string.IsNullOrWhiteSpace(selectedBatchId)
            ? "Auto-run"
            : "Run Selected Batch";

        return runState.PlanStatus switch
        {
            TaskboardPlanRuntimeStatus.PausedManualOnly => $"{prefix} paused: {FirstNonEmpty(summary, runState.LastBlockerReason, runState.LastResultSummary)}",
            TaskboardPlanRuntimeStatus.Blocked => $"{prefix} blocked: {FirstNonEmpty(summary, runState.LastBlockerReason, runState.LastResultSummary)}",
            TaskboardPlanRuntimeStatus.Failed => $"{prefix} failed: {FirstNonEmpty(summary, runState.LastBlockerReason, runState.LastResultSummary)}",
            TaskboardPlanRuntimeStatus.Completed => $"{actionName} completed: {runState.CompletedWorkItemCount}/{runState.TotalWorkItemCount} work item(s) passed.",
            _ => $"{actionName} stopped: {FirstNonEmpty(summary, runState.LastResultSummary, runState.LastBlockerReason)}"
        };
    }

    private static void ClearTerminalSummaryState(TaskboardPlanRunStateRecord runState)
    {
        runState.LastTerminalStatusCode = "";
        runState.LastTerminalSummaryFingerprint = "";
        runState.LastTerminalSummaryArtifactPath = "";
        runState.LastTerminalSummary = new TaskboardRunTerminalSummaryRecord();
    }

    private static bool IsTerminalRunState(TaskboardPlanRunStateRecord runState, string terminalStatusCode)
    {
        if (string.Equals(terminalStatusCode, "selected_batch_completed", StringComparison.OrdinalIgnoreCase))
            return true;

        return runState.PlanStatus is TaskboardPlanRuntimeStatus.Completed
            or TaskboardPlanRuntimeStatus.Blocked
            or TaskboardPlanRuntimeStatus.PausedManualOnly
            or TaskboardPlanRuntimeStatus.Failed;
    }

    private static string NormalizeResultKind(TaskboardWorkItemResultKind resultKind)
    {
        return resultKind switch
        {
            TaskboardWorkItemResultKind.ManualOnly => "manual_only",
            TaskboardWorkItemResultKind.Blocked => "blocked",
            TaskboardWorkItemResultKind.Passed => "passed",
            TaskboardWorkItemResultKind.NeedsFollowup => "needs_followup",
            TaskboardWorkItemResultKind.ValidationFailed => "validation_failed",
            _ => "failed"
        };
    }

    private static void NormalizeCompletionState(TaskboardPlanRunStateRecord runState)
    {
        foreach (var batch in runState.Batches)
        {
            batch.CompletedWorkItemCount = batch.WorkItems.Count(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed);
            batch.TotalWorkItemCount = batch.WorkItems.Count(item => item.Status != TaskboardWorkItemRuntimeStatus.Skipped || item.IsDecomposedItem);

            if (batch.TotalWorkItemCount == 0)
                batch.Status = TaskboardBatchRuntimeStatus.Skipped;
            else if (batch.WorkItems.All(item => item.Status is TaskboardWorkItemRuntimeStatus.Passed or TaskboardWorkItemRuntimeStatus.Skipped))
                batch.Status = TaskboardBatchRuntimeStatus.Completed;
        }

        runState.CompletedWorkItemCount = runState.Batches.Sum(batch => batch.CompletedWorkItemCount);
        runState.TotalWorkItemCount = runState.Batches.Sum(batch => batch.TotalWorkItemCount);
        if (runState.Batches.Count > 0
            && runState.Batches.All(batch => batch.Status is TaskboardBatchRuntimeStatus.Completed or TaskboardBatchRuntimeStatus.Skipped))
        {
            runState.PlanStatus = TaskboardPlanRuntimeStatus.Completed;
        }
    }

    private static bool ResetRetryableCurrentItem(TaskboardPlanRunStateRecord runState, string selectedBatchId)
    {
        var batch = !string.IsNullOrWhiteSpace(selectedBatchId)
            ? runState.Batches.FirstOrDefault(current => string.Equals(current.BatchId, selectedBatchId, StringComparison.OrdinalIgnoreCase))
            : runState.Batches.FirstOrDefault(current => string.Equals(current.BatchId, runState.CurrentBatchId, StringComparison.OrdinalIgnoreCase));
        batch ??= runState.Batches.FirstOrDefault(current =>
            current.WorkItems.Any(item => item.Status is TaskboardWorkItemRuntimeStatus.Failed or TaskboardWorkItemRuntimeStatus.Blocked or TaskboardWorkItemRuntimeStatus.ManualOnly));
        if (batch is null)
            return false;

        var workItem = batch.WorkItems.FirstOrDefault(current =>
            string.Equals(current.WorkItemId, batch.CurrentWorkItemId, StringComparison.OrdinalIgnoreCase))
            ?? batch.WorkItems.FirstOrDefault(current =>
                current.Status is TaskboardWorkItemRuntimeStatus.Failed or TaskboardWorkItemRuntimeStatus.Blocked or TaskboardWorkItemRuntimeStatus.ManualOnly);
        if (workItem is null)
            return false;

        workItem.Status = TaskboardWorkItemRuntimeStatus.Pending;
        workItem.LastResultKind = "";
        workItem.LastResultSummary = "";
        workItem.LastExecutionGoalResolution = new TaskboardExecutionGoalResolution();
        workItem.UpdatedUtc = DateTime.UtcNow.ToString("O");
        batch.Status = TaskboardBatchRuntimeStatus.Pending;
        batch.CurrentWorkItemId = "";
        batch.LastResultSummary = "";
        batch.LastExecutionGoalSummary = "";
        runState.PlanStatus = TaskboardPlanRuntimeStatus.Active;
        runState.CurrentBatchId = "";
        runState.CurrentWorkItemId = "";
        runState.LastBlockerReason = "";
        runState.LastBlockerOrigin = "";
        runState.LastBlockerWorkItemId = "";
        runState.LastBlockerWorkItemTitle = "";
        runState.LastBlockerPhase = "";
        runState.LastBlockerWorkFamily = "";
        runState.LastBlockerPhraseFamily = "";
        runState.LastBlockerOperationKind = "";
        runState.LastBlockerStackFamily = "";
        runState.LastResultKind = "";
        runState.LastResultSummary = "Retry requested for the current taskboard work item.";
        runState.LastExecutionGoalResolution = new TaskboardExecutionGoalResolution();
        runState.LastExecutionGoalSummary = "";
        runState.LastExecutionGoalBlockerCode = "";
        runState.LastFollowupBatchId = "";
        runState.LastFollowupBatchTitle = "";
        runState.LastFollowupWorkItemId = "";
        runState.LastFollowupWorkItemTitle = "";
        runState.LastFollowupSelectionReason = "";
        runState.LastFollowupWorkFamily = "";
        runState.LastFollowupPhraseFamily = "";
        runState.LastFollowupOperationKind = "";
        runState.LastFollowupStackFamily = "";
        runState.LastFollowupPhraseFamilyReasonCode = "";
        runState.LastFollowupOperationKindReasonCode = "";
        runState.LastFollowupStackFamilyReasonCode = "";
        runState.LastFollowupResolutionSummary = "";
        runState.LastPostChainReconciliationSummary = "";
        runState.LastContradictionGuardWorkItemId = "";
        runState.LastContradictionGuardWorkItemTitle = "";
        runState.LastContradictionGuardReasonCode = "";
        runState.LastContradictionGuardSummary = "";
        ClearTerminalSummaryState(runState);
        runState.CurrentRunPhaseCode = "selecting_work_item";
        runState.CurrentRunPhaseText = "Retrying the current work item.";
        runState.LatestStepSummary = $"Retry requested for {workItem.Title}.";
        AddEvent(runState, "retry_requested", batch.BatchId, workItem.WorkItemId, $"Retry requested for `{workItem.Title}`.");
        return true;
    }

    private static void MarkWorkItemRunning(
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord workItem)
    {
        runState.PlanStatus = TaskboardPlanRuntimeStatus.Running;
        runState.AutoRunStarted = true;
        runState.CurrentBatchId = batch.BatchId;
        runState.CurrentWorkItemId = workItem.WorkItemId;
        runState.LastBlockerReason = "";
        runState.LastBlockerOrigin = "";
        runState.LastBlockerWorkItemId = "";
        runState.LastBlockerWorkItemTitle = "";
        runState.LastBlockerPhase = "";
        runState.LastBlockerWorkFamily = "";
        runState.LastBlockerPhraseFamily = "";
        runState.LastBlockerOperationKind = "";
        runState.LastBlockerStackFamily = "";
        runState.LastFollowupBatchId = "";
        runState.LastFollowupBatchTitle = "";
        runState.LastFollowupWorkItemId = "";
        runState.LastFollowupWorkItemTitle = "";
        runState.LastFollowupSelectionReason = "";
        runState.LastFollowupWorkFamily = "";
        runState.LastFollowupPhraseFamily = "";
        runState.LastFollowupOperationKind = "";
        runState.LastFollowupStackFamily = "";
        runState.LastFollowupPhraseFamilyReasonCode = "";
        runState.LastFollowupOperationKindReasonCode = "";
        runState.LastFollowupStackFamilyReasonCode = "";
        runState.LastFollowupResolutionSummary = "";
        ClearTerminalSummaryState(runState);
        runState.CurrentRunPhaseCode = "selecting_work_item";
        runState.CurrentRunPhaseText = $"Running `{workItem.Title}`.";
        runState.LatestStepSummary = $"Running work item: {workItem.Title}.";
        batch.Status = TaskboardBatchRuntimeStatus.Running;
        batch.CurrentWorkItemId = workItem.WorkItemId;
        workItem.Status = TaskboardWorkItemRuntimeStatus.Running;
        workItem.UpdatedUtc = DateTime.UtcNow.ToString("O");

        if (!runState.Events.Any(current =>
                string.Equals(current.EventKind, "batch_started", StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.BatchId, batch.BatchId, StringComparison.OrdinalIgnoreCase)))
        {
            AddEvent(runState, "batch_started", batch.BatchId, "", $"Batch {batch.BatchNumber} started: {batch.Title}");
        }

        AddEvent(runState, "work_item_started", batch.BatchId, workItem.WorkItemId, $"Work item {workItem.Ordinal} started: {workItem.Title}");
    }

    private static void StampToolRequestContext(
        ToolRequest? request,
        TaskboardImportRecord activeImport,
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord workItem)
    {
        if (request is null)
            return;

        request.TaskboardRunStateId = runState.RunStateId;
        request.TaskboardPlanImportId = activeImport.ImportId;
        request.TaskboardPlanTitle = activeImport.Title;
        request.TaskboardBatchId = batch.BatchId;
        request.TaskboardBatchTitle = batch.Title;
        request.TaskboardWorkItemId = workItem.WorkItemId;
        request.TaskboardWorkItemTitle = workItem.Title;
    }

    private static void ApplyLiveRunEntry(TaskboardPlanRunStateRecord runState, TaskboardLiveRunEntryContext context)
    {
        runState.LastRunEntryAction = context.ActionName;
        runState.LastRunEntryPath = context.EntryPath;
        runState.LastRunEntrySelectedImportId = context.SelectedImportId;
        runState.LastRunEntrySelectedImportTitle = context.SelectedImportTitle;
        runState.LastRunEntrySelectedState = context.SelectedImportState;
        runState.LastRunEntrySelectedBatchId = context.SelectedBatchId;
        runState.LastLiveRunEntrySummary = context.Message;
        runState.LastRunUsedActivationHandoff = context.ActivationHandoffPerformed;
        runState.LastActivationHandoffSummary = context.ActivationHandoffSummary;
        runState.CurrentRunPhaseCode = "selecting_work_item";
        runState.CurrentRunPhaseText = "Selecting the next runnable work item.";
        runState.LatestStepSummary = context.Message;

        AddEvent(
            runState,
            "live_run_entry",
            "",
            context.SelectedBatchId,
            FirstNonEmpty(
                context.Message,
                $"{context.ActionName} entered via {FirstNonEmpty(context.EntryPath, "active_plan")}."));

        if (context.ActivationHandoffPerformed && !string.IsNullOrWhiteSpace(context.ActivationHandoffSummary))
        {
            AddEvent(runState, "activation_handoff", "", "", context.ActivationHandoffSummary);
        }
    }

    private static void ApplyDecomposition(
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord originalWorkItem,
        TaskboardWorkItemDecompositionRecord decomposition)
    {
        runState.LastResolvedBuildProfile = decomposition.BuildProfile;
        runState.LastDecompositionWorkItemId = decomposition.OriginalWorkItemId;
        runState.LastDecompositionSummary = decomposition.Reason;
        runState.LastWorkFamily = "";
        runState.LastWorkFamilySource = "";
        ApplyPhraseFamilyTrace(runState, decomposition);
        runState.LastTemplateId = decomposition.TemplateId;
        runState.LastTemplateCandidateIds = [.. decomposition.TemplateCandidateIds];
        runState.LastForensicsSummary = "";
        runState.LastResultKind = "decomposed";
        runState.LastResultSummary = decomposition.Reason;
        runState.LastBlockerReason = "";
        runState.LastBlockerOrigin = "";
        runState.LastBlockerWorkItemId = "";
        runState.LastBlockerWorkItemTitle = "";
        runState.LastBlockerPhase = "";
        runState.LastBlockerWorkFamily = "";
        runState.LastBlockerPhraseFamily = "";
        runState.LastBlockerOperationKind = "";
        runState.LastBlockerStackFamily = "";
        runState.LastExecutionGoalResolution = new TaskboardExecutionGoalResolution();
        runState.LastExecutionGoalSummary = "";
        runState.LastExecutionGoalBlockerCode = "";
        runState.LastFollowupBatchId = "";
        runState.LastFollowupBatchTitle = "";
        runState.LastFollowupWorkItemId = "";
        runState.LastFollowupWorkItemTitle = "";
        runState.LastFollowupSelectionReason = "";
        runState.LastFollowupWorkFamily = "";
        runState.LastFollowupPhraseFamily = "";
        runState.LastFollowupOperationKind = "";
        runState.LastFollowupStackFamily = "";
        runState.LastFollowupPhraseFamilyReasonCode = "";
        runState.LastFollowupOperationKindReasonCode = "";
        runState.LastFollowupStackFamilyReasonCode = "";
        runState.LastFollowupResolutionSummary = "";
        runState.LastPostChainReconciliationSummary = "";
        runState.PlanStatus = TaskboardPlanRuntimeStatus.Active;
        runState.CurrentRunPhaseCode = "resolving_phrase_family";
        runState.CurrentRunPhaseText = $"Decomposed `{originalWorkItem.Title}` into bounded sub-items.";
        runState.LatestStepSummary = decomposition.Reason;

        originalWorkItem.Status = TaskboardWorkItemRuntimeStatus.Skipped;
        originalWorkItem.LastResultKind = "decomposed";
        originalWorkItem.LastResultSummary = decomposition.Reason;
        originalWorkItem.LastExecutionGoalResolution = new TaskboardExecutionGoalResolution();
        originalWorkItem.PhraseFamily = FirstNonEmpty(originalWorkItem.PhraseFamily, decomposition.PhraseFamily);
        originalWorkItem.PhraseFamilySource = FirstNonEmpty(originalWorkItem.PhraseFamilySource, decomposition.PhraseFamilySource);
        originalWorkItem.TemplateId = FirstNonEmpty(originalWorkItem.TemplateId, decomposition.TemplateId);
        originalWorkItem.TargetStack = FirstNonEmpty(originalWorkItem.TargetStack, FormatStackFamily(decomposition.BuildProfile.StackFamily));
        originalWorkItem.UpdatedUtc = DateTime.UtcNow.ToString("O");

        var originalIndex = batch.WorkItems.FindIndex(item =>
            string.Equals(item.WorkItemId, originalWorkItem.WorkItemId, StringComparison.OrdinalIgnoreCase));
        var insertionIndex = originalIndex < 0 ? batch.WorkItems.Count : originalIndex + 1;
        foreach (var subItem in decomposition.SubItems.Select((item, index) => new { item, index }))
        {
            batch.WorkItems.Insert(
                insertionIndex + subItem.index,
                new TaskboardWorkItemRunStateRecord
                {
                    WorkItemId = subItem.item.SubItemId,
                    Ordinal = (originalIndex + 1) * 100 + subItem.index + 1,
                    DisplayOrdinal = subItem.item.DisplayOrdinal,
                    Title = subItem.item.Description,
                    PromptText = subItem.item.PromptText,
                    Summary = subItem.item.Summary,
                    IsDecomposedItem = true,
                    SourceWorkItemId = decomposition.OriginalWorkItemId,
                    OperationKind = subItem.item.OperationKind,
                    TargetStack = subItem.item.TargetStack,
                    WorkFamily = subItem.item.WorkFamily,
                    ExpectedArtifact = subItem.item.ExpectedArtifact,
                    ValidationHint = subItem.item.ValidationHint,
                    PhraseFamily = FirstNonEmpty(subItem.item.PhraseFamily, decomposition.PhraseFamily),
                    PhraseFamilySource = decomposition.PhraseFamilySource,
                    TemplateId = FirstNonEmpty(subItem.item.TemplateId, decomposition.TemplateId),
                    TemplateCandidateIds = subItem.item.TemplateCandidateIds.Count > 0
                        ? [.. subItem.item.TemplateCandidateIds]
                        : [.. decomposition.TemplateCandidateIds],
                    DirectToolRequest = subItem.item.ToolRequest?.Clone(),
                    Status = TaskboardWorkItemRuntimeStatus.Pending,
                    UpdatedUtc = DateTime.UtcNow.ToString("O")
                });
        }

        batch.TotalWorkItemCount = batch.WorkItems.Count(item => item.Status != TaskboardWorkItemRuntimeStatus.Skipped || item.IsDecomposedItem);
        runState.TotalWorkItemCount = runState.Batches.Sum(current => current.WorkItems.Count(item => item.Status != TaskboardWorkItemRuntimeStatus.Skipped || item.IsDecomposedItem));
        AddEvent(runState, "work_item_decomposed", batch.BatchId, originalWorkItem.WorkItemId, decomposition.Reason);
    }

    private static bool ApplyStructuralSupportCoverage(TaskboardPlanRunStateRecord runState)
    {
        if (runState.Batches.Count == 0)
            return false;

        var itemLookup = runState.Batches
            .SelectMany(batch => batch.WorkItems)
            .GroupBy(item => item.WorkItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var changed = false;
        foreach (var batch in runState.Batches)
        {
            foreach (var item in batch.WorkItems)
            {
                if (item.Status != TaskboardWorkItemRuntimeStatus.Pending)
                    continue;

                if (TryBuildStructuralSupportCoverageReason(item, itemLookup, out var headingPolicy, out var reason))
                {
                    ApplyCoveredStructuralSupport(runState, batch, item, headingPolicy, reason);
                    changed = true;
                }
            }
        }

        if (changed)
            NormalizeCompletionState(runState);

        return changed;
    }

    private static bool TryBuildStructuralSupportCoverageReason(
        TaskboardWorkItemRunStateRecord item,
        IReadOnlyDictionary<string, TaskboardWorkItemRunStateRecord> itemLookup,
        out TaskboardHeadingPolicyRecord headingPolicy,
        out string reason)
    {
        headingPolicy = TaskboardStructuralHeadingService.Classify(item.Title);
        if (!string.IsNullOrWhiteSpace(TaskboardStructuralHeadingService.ResolveActionableFollowupPhraseFamily(
            item.Title,
            item.Summary,
            item.PromptText)))
        {
            reason = "";
            return false;
        }

        if (TaskboardStructuralHeadingService.IsNonActionableHeading(headingPolicy))
        {
            reason = TaskboardStructuralHeadingService.BuildSupportCoverageReason(headingPolicy);
            return true;
        }

        if (!item.IsDecomposedItem
            || string.IsNullOrWhiteSpace(item.SourceWorkItemId)
            || !itemLookup.TryGetValue(item.SourceWorkItemId, out var sourceItem)
            || !TaskboardStructuralHeadingService.IsNonActionableHeading(sourceItem.Title))
        {
            headingPolicy = new TaskboardHeadingPolicyRecord();
            reason = "";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(TaskboardStructuralHeadingService.ResolveActionableFollowupPhraseFamily(
            sourceItem.Title,
            sourceItem.Summary,
            sourceItem.PromptText)))
        {
            headingPolicy = new TaskboardHeadingPolicyRecord();
            reason = "";
            return false;
        }

        headingPolicy = TaskboardStructuralHeadingService.Classify(sourceItem.Title);
        reason = $"{TaskboardStructuralHeadingService.BuildSupportCoverageReason(headingPolicy)} Removed stale standalone work that had been generated from that structural support heading.";
        return true;
    }

    private static void ApplyCoveredStructuralSupport(
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardHeadingPolicyRecord headingPolicy,
        string summary)
    {
        var now = DateTime.UtcNow.ToString("O");
        workItem.Status = TaskboardWorkItemRuntimeStatus.Skipped;
        workItem.LastResultKind = "covered_support_heading";
        workItem.LastResultSummary = summary;
        workItem.LastExecutionGoalResolution = new TaskboardExecutionGoalResolution();
        workItem.UpdatedUtc = now;

        runState.LastResultKind = "covered_support_heading";
        runState.LastResultSummary = summary;
        runState.LastDecompositionWorkItemId = FirstNonEmpty(workItem.SourceWorkItemId, workItem.WorkItemId);
        runState.LastDecompositionSummary = summary;
        runState.LastSupportCoverageWorkItemId = FirstNonEmpty(workItem.SourceWorkItemId, workItem.WorkItemId);
        runState.LastSupportCoverageWorkItemTitle = workItem.Title;
        runState.LastSupportCoverageSummary = summary;
        RecordHeadingPolicyDecision(runState, workItem, headingPolicy, summary);
        runState.CurrentRunPhaseCode = "support_heading_coverage";
        runState.CurrentRunPhaseText = "Covered structural support headings without standalone execution.";
        runState.LatestStepSummary = summary;

        batch.LastResultSummary = summary;
        batch.CompletedWorkItemCount = batch.WorkItems.Count(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed);
        batch.TotalWorkItemCount = batch.WorkItems.Count(item => item.Status != TaskboardWorkItemRuntimeStatus.Skipped || item.IsDecomposedItem);

        AddEvent(
            runState,
            "support_heading_covered",
            batch.BatchId,
            workItem.WorkItemId,
            summary);
    }

    private static void RecordHeadingPolicyDecision(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardHeadingPolicyRecord headingPolicy,
        string summary)
    {
        runState.LastHeadingPolicyWorkItemId = FirstNonEmpty(workItem.SourceWorkItemId, workItem.WorkItemId);
        runState.LastHeadingPolicyWorkItemTitle = FirstNonEmpty(headingPolicy.OriginalTitle, workItem.Title);
        runState.LastHeadingPolicyNormalizedTitle = headingPolicy.NormalizedTitle;
        runState.LastHeadingPolicyClass = headingPolicy.HeadingClass.ToString();
        runState.LastHeadingPolicyTreatment = headingPolicy.Treatment.ToString();
        runState.LastHeadingPolicyReasonCode = headingPolicy.ReasonCode;
        runState.LastHeadingPolicySummary = summary;
    }

    private static bool ApplyContradictionGuardCoverage(string workspaceRoot, TaskboardPlanRunStateRecord runState)
    {
        if (runState.Batches.Count == 0)
            return false;

        var changed = false;
        foreach (var batch in runState.Batches)
        {
            foreach (var item in batch.WorkItems)
            {
                if (item.Status != TaskboardWorkItemRuntimeStatus.Pending)
                    continue;

                if (!TryBuildContradictionGuardReason(workspaceRoot, runState, item, out var reasonCode, out var summary))
                    continue;

                ApplyContradictionGuard(runState, batch, item, reasonCode, summary);
                changed = true;
            }
        }

        if (changed)
            NormalizeCompletionState(runState);

        return changed;
    }

    private static bool ApplyRepeatedGenerationRejectionCoverage(TaskboardPlanRunStateRecord runState)
    {
        if (runState.Batches.Count == 0 || runState.ExecutedToolCalls.Count == 0)
            return false;

        var changed = false;
        foreach (var batch in runState.Batches)
        {
            foreach (var item in batch.WorkItems)
            {
                if (item.Status != TaskboardWorkItemRuntimeStatus.Pending)
                    continue;

                if (!TryBuildRepeatedGenerationRejectionSummary(runState, item, out var summary))
                    continue;

                var now = DateTime.UtcNow.ToString("O");
                item.Status = TaskboardWorkItemRuntimeStatus.Skipped;
                item.LastResultKind = "suppressed_repeated_generation_rejection";
                item.LastResultSummary = summary;
                item.UpdatedUtc = now;

                runState.LastResultKind = "suppressed_repeated_generation_rejection";
                runState.LastResultSummary = summary;
                runState.CurrentRunPhaseCode = "generation_rejection_reuse";
                runState.CurrentRunPhaseText = "Suppressed repeated helper generation after an unrecoverable thin-output rejection.";
                runState.LatestStepSummary = summary;
                batch.LastResultSummary = summary;

                AddEvent(runState, "generation_rejection_suppressed", batch.BatchId, item.WorkItemId, summary);
                changed = true;
            }
        }

        if (changed)
            NormalizeCompletionState(runState);

        return changed;
    }

    private static bool TryBuildRepeatedGenerationRejectionSummary(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord item,
        out string summary)
    {
        summary = "";
        var targetPath = NormalizePath(item.ExpectedArtifact);
        if (string.IsNullOrWhiteSpace(targetPath))
            return false;

        foreach (var call in runState.ExecutedToolCalls.AsEnumerable().Reverse())
        {
            if (!TryParseGenerationGuardrailRejection(call.StructuredDataJson, out var parsedTargetPath, out var retryStatus, out var escalationSummary, out var reasons))
                continue;

            if (!string.Equals(parsedTargetPath, targetPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.Equals(retryStatus, "no_stronger_generation_path_available", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(retryStatus, "retried_rejected", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            summary = $"suppressed_repeated_generation_rejection: `{FirstNonEmpty(item.Title, item.WorkItemId, "(unknown)")}` reuses prior rejected output for `{targetPath}`. {FirstNonEmpty(escalationSummary, reasons, "No stronger bounded generation path was available in the same run.")}";
            return true;
        }

        return false;
    }

    private static bool TryParseGenerationGuardrailRejection(
        string? json,
        out string targetPath,
        out string retryStatus,
        out string escalationSummary,
        out string reasons)
    {
        targetPath = "";
        retryStatus = "";
        escalationSummary = "";
        reasons = "";
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            targetPath = NormalizePath(TryReadJsonString(document.RootElement, "target_path"));
            if (!document.RootElement.TryGetProperty("generation_guardrail", out var section))
                return false;

            var evaluation = JsonSerializer.Deserialize<CSharpGenerationGuardrailEvaluationRecord>(section.GetRawText());
            if (evaluation is null || evaluation.Accepted)
                return false;

            retryStatus = evaluation.RetryStatus ?? "";
            escalationSummary = evaluation.EscalationSummary ?? "";
            reasons = evaluation.RejectionReasons.Count == 0
                ? evaluation.Summary
                : string.Join(", ", evaluation.RejectionReasons);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseRepairTargeting(
        string? json,
        out string targetPath,
        out string targetingStrategy,
        out string targetingSummary)
    {
        targetPath = "";
        targetingStrategy = "";
        targetingSummary = "";
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("input", out var input))
            {
                targetPath = NormalizePath(TryReadJsonString(input, "TargetFilePath"));
                targetingStrategy = TryReadJsonString(input, "TargetingStrategy");
                targetingSummary = TryReadJsonString(input, "TargetingSummary");
            }

            if (document.RootElement.TryGetProperty("proposal", out var proposal))
            {
                if (string.IsNullOrWhiteSpace(targetPath))
                    targetPath = NormalizePath(TryReadJsonString(proposal, "TargetFilePath"));
                if (string.IsNullOrWhiteSpace(targetingStrategy))
                    targetingStrategy = TryReadJsonString(proposal, "TargetingStrategy");
                if (string.IsNullOrWhiteSpace(targetingSummary))
                    targetingSummary = TryReadJsonString(proposal, "TargetingSummary");
            }

            return !string.IsNullOrWhiteSpace(targetPath)
                || !string.IsNullOrWhiteSpace(targetingStrategy)
                || !string.IsNullOrWhiteSpace(targetingSummary);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseProjectAttachContinuation(
        string? json,
        out string targetProjectPath,
        out bool projectExistedAtDecision,
        out string continuationStatus,
        out string insertedStep,
        out string continuationSummary)
    {
        targetProjectPath = "";
        projectExistedAtDecision = false;
        continuationStatus = "";
        insertedStep = "";
        continuationSummary = "";
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            JsonElement section;
            if (root.TryGetProperty("project_attach", out var directSection)
                && directSection.ValueKind == JsonValueKind.Object)
            {
                section = directSection;
            }
            else if (root.TryGetProperty("parsed", out var parsedSection)
                && parsedSection.ValueKind == JsonValueKind.Object
                && parsedSection.TryGetProperty("project_attach", out var nestedSection)
                && nestedSection.ValueKind == JsonValueKind.Object)
            {
                section = nestedSection;
            }
            else
            {
                return false;
            }

            targetProjectPath = NormalizePath(TryReadJsonString(section, "target_project_path"));
            continuationStatus = TryReadJsonString(section, "continuation_status");
            insertedStep = TryReadJsonString(section, "inserted_step");
            continuationSummary = TryReadJsonString(section, "continuation_summary");
            projectExistedAtDecision = section.TryGetProperty("project_existed_at_decision", out var existedElement)
                && existedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && existedElement.GetBoolean();

            return !string.IsNullOrWhiteSpace(targetProjectPath)
                || !string.IsNullOrWhiteSpace(continuationStatus)
                || !string.IsNullOrWhiteSpace(insertedStep)
                || !string.IsNullOrWhiteSpace(continuationSummary);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseRepairSymbolRecovery(
        string? json,
        out string referencedSymbolName,
        out string referencedMemberName,
        out string recoveryStatus,
        out string recoverySummary,
        out string recoveryCandidatePath)
    {
        referencedSymbolName = "";
        referencedMemberName = "";
        recoveryStatus = "";
        recoverySummary = "";
        recoveryCandidatePath = "";
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("proposal", out var proposal))
            {
                referencedSymbolName = TryReadJsonString(proposal, "ReferencedSymbolName");
                referencedMemberName = TryReadJsonString(proposal, "ReferencedMemberName");
                recoveryStatus = TryReadJsonString(proposal, "SymbolRecoveryStatus");
                recoverySummary = TryReadJsonString(proposal, "SymbolRecoverySummary");
                recoveryCandidatePath = NormalizePath(TryReadJsonString(proposal, "SymbolRecoveryCandidatePath"));
            }

            if (document.RootElement.TryGetProperty("draft", out var draft))
            {
                referencedSymbolName = FirstNonEmpty(referencedSymbolName, TryReadJsonString(draft, "ReferencedSymbolName"));
                referencedMemberName = FirstNonEmpty(referencedMemberName, TryReadJsonString(draft, "ReferencedMemberName"));
                recoveryStatus = FirstNonEmpty(recoveryStatus, TryReadJsonString(draft, "SymbolRecoveryStatus"));
                recoverySummary = FirstNonEmpty(recoverySummary, TryReadJsonString(draft, "SymbolRecoverySummary"));
                if (string.IsNullOrWhiteSpace(recoveryCandidatePath))
                    recoveryCandidatePath = NormalizePath(TryReadJsonString(draft, "SymbolRecoveryCandidatePath"));
            }

            return !string.IsNullOrWhiteSpace(referencedSymbolName)
                || !string.IsNullOrWhiteSpace(referencedMemberName)
                || !string.IsNullOrWhiteSpace(recoveryStatus)
                || !string.IsNullOrWhiteSpace(recoverySummary)
                || !string.IsNullOrWhiteSpace(recoveryCandidatePath);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseBehaviorDepthEvidence(
        string? json,
        out string artifactPath,
        out string behaviorDepthTier,
        out string completionRecommendation,
        out string followUpRecommendation,
        out string targetPath,
        out string profile,
        out string namespaceName,
        out string featureFamily,
        out string integrationGapKind,
        out string nextFollowThroughHint,
        out List<string> candidateSurfaceHints)
    {
        artifactPath = "";
        behaviorDepthTier = "";
        completionRecommendation = "";
        followUpRecommendation = "";
        targetPath = "";
        profile = "";
        namespaceName = "";
        featureFamily = "";
        integrationGapKind = "";
        nextFollowThroughHint = "";
        candidateSurfaceHints = [];
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("behavior_depth_artifact", out var artifactSection))
                artifactPath = NormalizePath(TryReadJsonString(artifactSection, "RelativePath"));

            if (document.RootElement.TryGetProperty("behavior_depth", out var behaviorSection))
            {
                targetPath = NormalizePath(TryReadJsonString(behaviorSection, "TargetPath"));
                profile = TryReadJsonString(behaviorSection, "Profile");
                namespaceName = TryReadJsonString(behaviorSection, "SourceNamespace");
                featureFamily = TryReadJsonString(behaviorSection, "FeatureFamily");
                behaviorDepthTier = TryReadJsonString(behaviorSection, "BehaviorDepthTier");
                completionRecommendation = TryReadJsonString(behaviorSection, "CompletionRecommendation");
                followUpRecommendation = TryReadJsonString(behaviorSection, "FollowUpRecommendation");
                integrationGapKind = TryReadJsonString(behaviorSection, "IntegrationGapKind");
                nextFollowThroughHint = TryReadJsonString(behaviorSection, "NextFollowThroughHint");
                candidateSurfaceHints = TryReadJsonStringList(behaviorSection, "CandidateConsumerSurfaceHints");
            }

            return !string.IsNullOrWhiteSpace(artifactPath)
                || !string.IsNullOrWhiteSpace(targetPath)
                || !string.IsNullOrWhiteSpace(profile)
                || !string.IsNullOrWhiteSpace(namespaceName)
                || !string.IsNullOrWhiteSpace(featureFamily)
                || !string.IsNullOrWhiteSpace(behaviorDepthTier)
                || !string.IsNullOrWhiteSpace(completionRecommendation)
                || !string.IsNullOrWhiteSpace(followUpRecommendation)
                || !string.IsNullOrWhiteSpace(integrationGapKind)
                || !string.IsNullOrWhiteSpace(nextFollowThroughHint)
                || candidateSurfaceHints.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBuildContradictionGuardReason(
        string workspaceRoot,
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord item,
        out string reasonCode,
        out string summary)
    {
        reasonCode = "";
        summary = "";

        var request = item.DirectToolRequest;
        if (!string.Equals(request?.ToolName, "add_dotnet_project_reference", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!request!.TryGetArgument("project_path", out var projectPath)
            || !request.TryGetArgument("reference_path", out var referencePath))
            return false;

        if (!PathsResolveToSameWorkspaceTarget(workspaceRoot, projectPath, referencePath))
            return false;

        var hasVerifiedRepair = !string.IsNullOrWhiteSpace(runState.LastVerificationAfterMutationOutcome)
            || (runState.LastMutationTouchedFilePaths.Count > 0
                && runState.ExecutedToolCalls.Any(call =>
                    string.Equals(call.Stage, "completed", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(call.ToolName, "verify_patch_draft", StringComparison.OrdinalIgnoreCase)));
        reasonCode = hasVerifiedRepair
            ? "contradicts_verified_repair"
            : "blocked_self_project_reference";
        summary = hasVerifiedRepair
            ? $"contradicts_verified_repair: Folded contradictory attach step `{item.Title}` out of the active run because `{projectPath}` would reference itself again and recreate a repair that was already validated in this run."
            : $"blocked_self_project_reference: Folded contradictory attach step `{item.Title}` out of the active run because `{projectPath}` would reference itself.";
        return true;
    }

    private static void ApplyContradictionGuard(
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord workItem,
        string reasonCode,
        string summary)
    {
        var now = DateTime.UtcNow.ToString("O");
        workItem.Status = TaskboardWorkItemRuntimeStatus.Skipped;
        workItem.LastResultKind = reasonCode;
        workItem.LastResultSummary = summary;
        workItem.LastExecutionGoalResolution = new TaskboardExecutionGoalResolution();
        workItem.UpdatedUtc = now;

        runState.LastResultKind = reasonCode;
        runState.LastResultSummary = summary;
        runState.LastContradictionGuardWorkItemId = FirstNonEmpty(workItem.SourceWorkItemId, workItem.WorkItemId);
        runState.LastContradictionGuardWorkItemTitle = workItem.Title;
        runState.LastContradictionGuardReasonCode = reasonCode;
        runState.LastContradictionGuardSummary = summary;
        runState.CurrentRunPhaseCode = "contradiction_guard";
        runState.CurrentRunPhaseText = "Blocked contradictory work that would recreate a resolved defect.";
        runState.LatestStepSummary = summary;

        batch.LastResultSummary = summary;
        batch.CompletedWorkItemCount = batch.WorkItems.Count(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed);
        batch.TotalWorkItemCount = batch.WorkItems.Count(item => item.Status != TaskboardWorkItemRuntimeStatus.Skipped || item.IsDecomposedItem);

        AddEvent(
            runState,
            "contradiction_prevented",
            batch.BatchId,
            workItem.WorkItemId,
            summary);
    }

    private static void ApplyExecutionGoalResolution(
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionGoalResolution goalResolution)
    {
        runState.LastExecutionGoalResolution = goalResolution ?? new TaskboardExecutionGoalResolution();
        runState.LastExecutionGoalSummary = BuildExecutionGoalSummary(goalResolution);
        runState.LastExecutionGoalBlockerCode = goalResolution?.Blocker.Code.ToString().ToLowerInvariant() ?? "";
        runState.LastWorkFamily = FirstMeaningful(goalResolution?.WorkFamily, workItem.WorkFamily, runState.LastWorkFamily);
        runState.LastWorkFamilySource = FirstMeaningful(goalResolution?.WorkFamilySource, runState.LastWorkFamilySource);
        runState.LastPhraseFamily = FirstMeaningful(goalResolution?.PhraseFamily, workItem.PhraseFamily, runState.LastPhraseFamily);
        runState.LastPhraseFamilySource = FirstMeaningful(workItem.PhraseFamilySource, runState.LastPhraseFamilySource);
        runState.LastCanonicalOperationKind = FirstMeaningful(goalResolution?.LaneResolution.CanonicalOperationKind, runState.LastCanonicalOperationKind);
        runState.LastCanonicalTargetPath = FirstMeaningful(goalResolution?.LaneResolution.CanonicalTargetPath, runState.LastCanonicalTargetPath);
        runState.LastCanonicalizationTrace = FirstMeaningful(goalResolution?.LaneResolution.CanonicalizationTrace, runState.LastCanonicalizationTrace);
        runState.LastTemplateId = FirstMeaningful(goalResolution?.TemplateId, workItem.TemplateId, runState.LastTemplateId);
        runState.LastTemplateCandidateIds = goalResolution?.TemplateCandidateIds?.Count > 0
            ? [.. goalResolution.TemplateCandidateIds]
            : [.. workItem.TemplateCandidateIds];
        runState.LastResolvedTargetFileType = goalResolution?.LaneResolution.ResolvedTargetIdentity.FileType ?? "";
        runState.LastResolvedTargetRole = goalResolution?.LaneResolution.ResolvedTargetIdentity.Role ?? "";
        runState.LastResolvedTargetProjectName = goalResolution?.LaneResolution.ResolvedTargetIdentity.ProjectName ?? "";
        runState.LastResolvedTargetNamespaceHint = goalResolution?.LaneResolution.ResolvedTargetIdentity.NamespaceHint ?? "";
        runState.LastResolvedTargetIdentityTrace = goalResolution?.LaneResolution.ResolvedTargetIdentity.IdentityTrace ?? "";
        runState.LastForensicsSummary = goalResolution?.ForensicsExplanation ?? "";
        runState.LastExecutionDecisionSummary = FirstNonEmpty(goalResolution?.ResolutionReason, runState.LastExecutionDecisionSummary);
        runState.LastPlannedToolName = FirstNonEmpty(goalResolution?.Goal.SelectedToolId, runState.LastPlannedToolName);
        runState.LastPlannedChainTemplateId = FirstNonEmpty(goalResolution?.Goal.SelectedChainTemplateId, runState.LastPlannedChainTemplateId);
        runState.CurrentRunPhaseCode = "resolving_lane";
        runState.CurrentRunPhaseText = "Resolving deterministic execution goal and lane.";
        runState.LatestStepSummary = FirstNonEmpty(runState.LastExecutionGoalSummary, "Resolved execution goal.");
        AppendRecentObservedValue(runState.RecentObservedToolNames, goalResolution?.Goal.SelectedToolId ?? "");
        AppendRecentObservedValue(runState.RecentObservedChainTemplateIds, goalResolution?.Goal.SelectedChainTemplateId ?? "");
        AppendExecutedToolCall(
            runState,
            batch.BatchId,
            workItem.WorkItemId,
            goalResolution?.Goal.SelectedToolId ?? "",
            goalResolution?.Goal.SelectedChainTemplateId ?? "",
            "planned",
            goalResolution?.GoalKind.ToString().ToLowerInvariant() ?? "",
            FirstNonEmpty(goalResolution?.ResolutionReason, runState.LastExecutionGoalSummary));
        batch.LastExecutionGoalSummary = runState.LastExecutionGoalSummary;
        workItem.LastExecutionGoalResolution = goalResolution ?? new TaskboardExecutionGoalResolution();
        workItem.OperationKind = FirstMeaningful(goalResolution?.OperationKind, workItem.OperationKind);
        workItem.TargetStack = FirstMeaningful(goalResolution?.TargetStack, workItem.TargetStack);
        workItem.WorkFamily = FirstMeaningful(goalResolution?.WorkFamily, workItem.WorkFamily);
        workItem.PhraseFamily = FirstMeaningful(goalResolution?.PhraseFamily, workItem.PhraseFamily);
        workItem.TemplateId = FirstMeaningful(goalResolution?.TemplateId, workItem.TemplateId);
        if (goalResolution?.TemplateCandidateIds?.Count > 0)
            workItem.TemplateCandidateIds = [.. goalResolution.TemplateCandidateIds];
        workItem.UpdatedUtc = DateTime.UtcNow.ToString("O");

        if (goalResolution is null || goalResolution.GoalKind == TaskboardExecutionGoalKind.Unknown)
            return;

        var goalLabel = FirstNonEmpty(
            goalResolution.Goal.SelectedChainTemplateId,
            goalResolution.Goal.SelectedToolId,
            goalResolution.Blocker.Code == TaskboardExecutionGoalBlockerCode.None
                ? ""
                : goalResolution.Blocker.Code.ToString().ToLowerInvariant());
        AddEvent(
            runState,
            "execution_goal_resolved",
            batch.BatchId,
            workItem.WorkItemId,
            $"Execution goal resolved: {goalResolution.GoalKind.ToString().ToLowerInvariant()} -> {FirstNonEmpty(goalLabel, "(none)")}");
    }

    private static CommandCanonicalizationRecord BuildCommandNormalizationRecord(
        string workspaceRoot,
        string importId,
        string batchId,
        TaskboardWorkItemDecompositionRecord decomposition)
    {
        return new CommandCanonicalizationRecord
        {
            NormalizationId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            PlanImportId = importId,
            BatchId = batchId,
            WorkItemId = decomposition.OriginalWorkItemId,
            WorkItemTitle = decomposition.OriginalTitle,
            RawPhraseText = decomposition.PhraseFamilyResolution.RawPhraseText,
            NormalizedPhraseText = decomposition.PhraseFamilyResolution.NormalizedPhraseText,
            NormalizedOperationKind = decomposition.PhraseFamilyResolution.CanonicalOperationKind,
            NormalizedTargetPath = decomposition.PhraseFamilyResolution.CanonicalTargetPath,
            NormalizedProjectName = decomposition.PhraseFamilyResolution.CanonicalProjectName,
            NormalizedTemplateHint = decomposition.PhraseFamilyResolution.CanonicalTemplateHint,
            TargetRoleHint = decomposition.PhraseFamilyResolution.CanonicalRoleHint,
            NormalizationTrace = decomposition.PhraseFamilyResolution.CanonicalizationTrace,
            Summary = decomposition.PhraseFamilyResolution.ResolutionSummary,
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private static void ApplyExecutionOutcome(
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionOutcome outcome)
    {
        var wasSatisfiedSkip = outcome.ResultKind == TaskboardWorkItemResultKind.Passed
            && string.Equals(outcome.ResultClassification, "state_already_satisfied", StringComparison.OrdinalIgnoreCase);
        runState.LastResultKind = wasSatisfiedSkip
            ? "state_already_satisfied"
            : NormalizeResultKind(outcome.ResultKind);
        runState.LastResultSummary = outcome.Summary;
        batch.LastResultSummary = outcome.Summary;
        workItem.LastResultKind = runState.LastResultKind;
        workItem.LastResultSummary = outcome.Summary;
        workItem.UpdatedUtc = DateTime.UtcNow.ToString("O");

        switch (outcome.ResultKind)
        {
            case TaskboardWorkItemResultKind.Passed:
                workItem.Status = TaskboardWorkItemRuntimeStatus.Passed;
                runState.LastFailureOutcomeType = "";
                runState.LastFailureFamily = "";
                runState.LastFailureErrorCode = "";
                runState.LastFailureNormalizedSummary = "";
                runState.LastFailureTargetPath = "";
                runState.LastFailureSourcePath = "";
                runState.LastFailureRepairContextPath = "";
                runState.LastBlockerReason = "";
                runState.LastBlockerOrigin = "";
                runState.LastBlockerWorkItemId = "";
                runState.LastBlockerWorkItemTitle = "";
                runState.LastBlockerPhase = "";
                runState.LastBlockerWorkFamily = "";
                runState.LastBlockerPhraseFamily = "";
                runState.LastBlockerOperationKind = "";
                runState.LastBlockerStackFamily = "";
                batch.CompletedWorkItemCount = batch.WorkItems.Count(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed);
                runState.CompletedWorkItemCount = runState.Batches.Sum(current => current.WorkItems.Count(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed));
                runState.LastCompletedWorkItemId = workItem.WorkItemId;
                runState.LastCompletedWorkItemTitle = workItem.Title;
                runState.LastCompletedStepSummary = outcome.Summary;
                runState.CurrentRunPhaseCode = wasSatisfiedSkip ? "state_satisfied_skip" : "validating_result";
                runState.CurrentRunPhaseText = wasSatisfiedSkip
                    ? "Skipped execution because the required state was already satisfied."
                    : "Validated the latest execution result.";
                AddEvent(
                    runState,
                    wasSatisfiedSkip ? "work_item_skipped_satisfied" : "work_item_passed",
                    batch.BatchId,
                    workItem.WorkItemId,
                    wasSatisfiedSkip
                        ? $"Work item {workItem.Ordinal} skipped with proof: {workItem.Title}"
                        : $"Work item {workItem.Ordinal} passed: {workItem.Title}");

                if (batch.WorkItems.All(item => item.Status is TaskboardWorkItemRuntimeStatus.Passed or TaskboardWorkItemRuntimeStatus.Skipped))
                {
                    batch.Status = TaskboardBatchRuntimeStatus.Completed;
                    batch.CurrentWorkItemId = "";
                    AddEvent(runState, "batch_completed", batch.BatchId, "", $"Batch {batch.BatchNumber} completed: {batch.Title}");
                }
                else
                {
                    batch.Status = TaskboardBatchRuntimeStatus.Pending;
                    batch.CurrentWorkItemId = "";
                }

                runState.CurrentWorkItemId = "";
                runState.CurrentBatchId = "";
                if (runState.Batches.All(current => current.Status is TaskboardBatchRuntimeStatus.Completed or TaskboardBatchRuntimeStatus.Skipped))
                {
                    runState.PlanStatus = TaskboardPlanRuntimeStatus.Completed;
                    runState.CurrentRunPhaseCode = "completed";
                    runState.CurrentRunPhaseText = "Active plan completed.";
                    AddEvent(runState, "plan_completed", "", "", $"Plan completed: {runState.PlanTitle}");
                }
                else
                {
                    runState.PlanStatus = TaskboardPlanRuntimeStatus.Running;
                }

                return;

            case TaskboardWorkItemResultKind.ManualOnly:
                workItem.Status = TaskboardWorkItemRuntimeStatus.ManualOnly;
                batch.Status = TaskboardBatchRuntimeStatus.ManualOnly;
                runState.PlanStatus = TaskboardPlanRuntimeStatus.PausedManualOnly;
                runState.LastFailureOutcomeType = "";
                runState.LastFailureFamily = "";
                runState.LastFailureErrorCode = "";
                runState.LastFailureNormalizedSummary = "";
                runState.LastFailureTargetPath = "";
                runState.LastFailureSourcePath = "";
                runState.LastFailureRepairContextPath = "";
                runState.LastBlockerReason = outcome.Summary;
                runState.LastBlockerOrigin = ResolveBlockerOrigin(runState);
                runState.CurrentRunPhaseCode = "blocked";
                runState.CurrentRunPhaseText = "Paused at a manual-only boundary.";
                AddEvent(runState, "work_item_manual_only", batch.BatchId, workItem.WorkItemId, outcome.Summary);
                return;

            case TaskboardWorkItemResultKind.Blocked:
                workItem.Status = TaskboardWorkItemRuntimeStatus.Blocked;
                batch.Status = TaskboardBatchRuntimeStatus.Blocked;
                runState.PlanStatus = TaskboardPlanRuntimeStatus.Blocked;
                runState.LastFailureOutcomeType = "";
                runState.LastFailureFamily = "";
                runState.LastFailureErrorCode = "";
                runState.LastFailureNormalizedSummary = "";
                runState.LastFailureTargetPath = "";
                runState.LastFailureSourcePath = "";
                runState.LastFailureRepairContextPath = "";
                runState.LastBlockerReason = outcome.Summary;
                runState.LastBlockerOrigin = ResolveBlockerOrigin(runState);
                runState.CurrentRunPhaseCode = "blocked";
                runState.CurrentRunPhaseText = "Blocked on the current work item.";
                AddEvent(runState, "work_item_blocked", batch.BatchId, workItem.WorkItemId, outcome.Summary);
                return;

            case TaskboardWorkItemResultKind.NeedsFollowup:
            case TaskboardWorkItemResultKind.ValidationFailed:
            case TaskboardWorkItemResultKind.Failed:
            default:
                workItem.Status = TaskboardWorkItemRuntimeStatus.Failed;
                batch.Status = TaskboardBatchRuntimeStatus.Failed;
                runState.PlanStatus = TaskboardPlanRuntimeStatus.Failed;
                runState.LastBlockerReason = outcome.Summary;
                runState.LastBlockerOrigin = ResolveBlockerOrigin(runState);
                runState.CurrentRunPhaseCode = "blocked";
                runState.CurrentRunPhaseText = "Execution failed on the current work item.";
                AddEvent(runState, "work_item_failed", batch.BatchId, workItem.WorkItemId, outcome.Summary);
                return;
        }
    }

    private static TaskboardRunWorkItem BuildRunWorkItem(TaskboardWorkItemRunStateRecord workItem)
    {
        return new TaskboardRunWorkItem
        {
            WorkItemId = workItem.WorkItemId,
            Ordinal = workItem.Ordinal,
            DisplayOrdinal = workItem.DisplayOrdinal,
            Title = workItem.Title,
            PromptText = workItem.PromptText,
            Summary = workItem.Summary,
            IsDecomposedItem = workItem.IsDecomposedItem,
            SourceWorkItemId = workItem.SourceWorkItemId,
            OperationKind = workItem.OperationKind,
            TargetStack = workItem.TargetStack,
            WorkFamily = workItem.WorkFamily,
            ExpectedArtifact = workItem.ExpectedArtifact,
            ValidationHint = workItem.ValidationHint,
            PhraseFamily = workItem.PhraseFamily,
            TemplateId = workItem.TemplateId,
            TemplateCandidateIds = [.. workItem.TemplateCandidateIds],
            DirectToolRequest = workItem.DirectToolRequest?.Clone()
        };
    }

    private static void AddEvent(TaskboardPlanRunStateRecord runState, string eventKind, string batchId, string workItemId, string message)
    {
        runState.Events.Add(new TaskboardRunEventRecord
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventKind = eventKind,
            BatchId = batchId,
            WorkItemId = workItemId,
            Message = message,
            CreatedUtc = DateTime.UtcNow.ToString("O")
        });

        if (runState.Events.Count > 80)
            runState.Events.RemoveRange(0, runState.Events.Count - 80);
    }

    private static void ApplyLiveProgressUpdate(TaskboardPlanRunStateRecord runState, TaskboardLiveProgressUpdate update)
    {
        if (!string.IsNullOrWhiteSpace(update.PhaseCode))
            runState.CurrentRunPhaseCode = update.PhaseCode;
        if (!string.IsNullOrWhiteSpace(update.PhaseText))
            runState.CurrentRunPhaseText = update.PhaseText;
        if (!string.IsNullOrWhiteSpace(update.ActivitySummary))
            runState.LatestStepSummary = update.ActivitySummary;
        if ((string.Equals(update.EventKind, "execution_started", StringComparison.OrdinalIgnoreCase)
                || string.Equals(update.EventKind, "chain_step_started", StringComparison.OrdinalIgnoreCase))
            && (!string.IsNullOrWhiteSpace(update.ToolName) || !string.IsNullOrWhiteSpace(update.ChainTemplateId)))
        {
            AppendExecutedToolCall(
                runState,
                update.BatchId,
                update.WorkItemId,
                update.ToolName,
                update.ChainTemplateId,
                "dispatched",
                update.EventKind,
                update.ActivitySummary);
        }
        if (!string.IsNullOrWhiteSpace(update.ToolName))
        {
            runState.LastObservedToolName = update.ToolName;
            AppendRecentObservedValue(runState.RecentObservedToolNames, update.ToolName);
        }
        if (!string.IsNullOrWhiteSpace(update.ChainTemplateId))
        {
            runState.LastObservedChainTemplateId = update.ChainTemplateId;
            AppendRecentObservedValue(runState.RecentObservedChainTemplateIds, update.ChainTemplateId);
        }
    }

    private static void AppendRecentObservedValue(List<string> items, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        items.Add(value);
        if (items.Count > 12)
            items.RemoveRange(0, items.Count - 12);
    }

    private static void AppendExecutedToolCalls(
        TaskboardPlanRunStateRecord runState,
        string batchId,
        string workItemId,
        List<TaskboardExecutedToolCallRecord>? calls)
    {
        if (calls is null || calls.Count == 0)
            return;

        foreach (var call in calls)
        {
            AppendExecutedToolCall(
                runState,
                FirstNonEmpty(call.BatchId, batchId),
                FirstNonEmpty(call.WorkItemId, workItemId),
                call.ToolName,
                call.ChainTemplateId,
                call.Stage,
                call.ResultClassification,
                call.Summary,
                call.CreatedUtc,
                call.MutationObserved,
                call.TouchedFilePaths,
                call.StructuredDataJson);
        }
    }

    private static void AppendExecutedToolCall(
        TaskboardPlanRunStateRecord runState,
        string batchId,
        string workItemId,
        string toolName,
        string chainTemplateId,
        string stage,
        string resultClassification,
        string summary,
        string? createdUtc = null,
        bool mutationObserved = false,
        IReadOnlyList<string>? touchedFilePaths = null,
        string? structuredDataJson = null)
    {
        if (string.IsNullOrWhiteSpace(toolName) && string.IsNullOrWhiteSpace(chainTemplateId))
            return;

        var effectiveCreatedUtc = FirstNonEmpty(createdUtc, DateTime.UtcNow.ToString("O"));
        var normalizedTouchedFilePaths = (touchedFilePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        runState.ExecutedToolCalls.Add(new TaskboardExecutedToolCallRecord
        {
            BatchId = batchId ?? "",
            WorkItemId = workItemId ?? "",
            ToolName = toolName ?? "",
            ChainTemplateId = chainTemplateId ?? "",
            Stage = stage ?? "",
            ResultClassification = resultClassification ?? "",
            Summary = summary ?? "",
            MutationObserved = mutationObserved,
            TouchedFilePaths = normalizedTouchedFilePaths,
            StructuredDataJson = structuredDataJson ?? "",
            CreatedUtc = effectiveCreatedUtc
        });

        if (string.Equals(toolName, "plan_repair", StringComparison.OrdinalIgnoreCase)
            && TryParseRepairTargeting(structuredDataJson, out var repairTargetPath, out var targetingStrategy, out var targetingSummary))
        {
            if (!string.IsNullOrWhiteSpace(repairTargetPath))
                runState.LastRepairTargetPath = repairTargetPath;
            runState.LastRepairTargetingStrategy = targetingStrategy;
            runState.LastRepairTargetingSummary = targetingSummary;
        }

        if ((string.Equals(toolName, "plan_repair", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "preview_patch_draft", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "apply_patch_draft", StringComparison.OrdinalIgnoreCase))
            && TryParseRepairSymbolRecovery(
                structuredDataJson,
                out var referencedSymbolName,
                out var referencedMemberName,
                out var recoveryStatus,
                out var recoverySummary,
                out var recoveryCandidatePath))
        {
            runState.LastRepairReferencedSymbolName = referencedSymbolName;
            runState.LastRepairReferencedMemberName = referencedMemberName;
            runState.LastRepairSymbolRecoveryStatus = recoveryStatus;
            runState.LastRepairSymbolRecoverySummary = recoverySummary;
            runState.LastRepairSymbolRecoveryCandidatePath = recoveryCandidatePath;
        }

        if ((string.Equals(toolName, "create_dotnet_project", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "add_project_to_solution", StringComparison.OrdinalIgnoreCase))
            && TryParseProjectAttachContinuation(
                structuredDataJson,
                out var attachTargetPath,
                out var projectExistedAtDecision,
                out var continuationStatus,
                out var insertedStep,
                out var continuationSummary))
        {
            runState.LastProjectAttachTargetPath = attachTargetPath;
            runState.LastProjectAttachProjectExistedAtDecision = projectExistedAtDecision;
            runState.LastProjectAttachContinuationStatus = continuationStatus;
            runState.LastProjectAttachInsertedStep = insertedStep;
            runState.LastProjectAttachSummary = continuationSummary;
            if (!string.IsNullOrWhiteSpace(continuationSummary))
                runState.LastExecutionDecisionSummary = continuationSummary;
        }

        if (string.Equals(toolName, "add_dotnet_project_reference", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(summary))
        {
            runState.LastExecutionDecisionSummary = summary;
        }

        if (TryParseBehaviorDepthEvidence(
                structuredDataJson,
                out var behaviorDepthArtifactPath,
                out var behaviorDepthTier,
                out var completionRecommendation,
                out var followUpRecommendation,
                out var behaviorDepthTargetPath,
                out var behaviorDepthProfile,
                out var behaviorDepthNamespace,
                out var behaviorDepthFeatureFamily,
                out var behaviorDepthIntegrationGapKind,
                out var behaviorDepthNextFollowThroughHint,
                out var behaviorDepthCandidateSurfaceHints))
        {
            runState.LastBehaviorDepthArtifactPath = behaviorDepthArtifactPath;
            runState.LastBehaviorDepthTier = behaviorDepthTier;
            runState.LastBehaviorDepthCompletionRecommendation = completionRecommendation;
            runState.LastBehaviorDepthFollowUpRecommendation = followUpRecommendation;
            runState.LastBehaviorDepthTargetPath = behaviorDepthTargetPath;
            runState.LastBehaviorDepthProfile = behaviorDepthProfile;
            runState.LastBehaviorDepthNamespace = behaviorDepthNamespace;
            runState.LastBehaviorDepthFeatureFamily = behaviorDepthFeatureFamily;
            runState.LastBehaviorDepthIntegrationGapKind = behaviorDepthIntegrationGapKind;
            runState.LastBehaviorDepthNextFollowThroughHint = behaviorDepthNextFollowThroughHint;
            runState.LastBehaviorDepthCandidateSurfaceHints = behaviorDepthCandidateSurfaceHints;
        }

        if (mutationObserved
            && string.Equals(stage, "completed", StringComparison.OrdinalIgnoreCase))
        {
            runState.LastMutationToolName = toolName ?? "";
            runState.LastMutationUtc = effectiveCreatedUtc;
            runState.LastMutationTouchedFilePaths = normalizedTouchedFilePaths;
            runState.LastVerificationAfterMutationOutcome = "";
            runState.LastVerificationAfterMutationUtc = "";
        }

        if (!string.IsNullOrWhiteSpace(runState.LastMutationUtc)
            && string.Equals(stage, "completed", StringComparison.OrdinalIgnoreCase)
            && string.Equals(toolName, "verify_patch_draft", StringComparison.OrdinalIgnoreCase)
            && ParseUtc(effectiveCreatedUtc) >= ParseUtc(runState.LastMutationUtc))
        {
            runState.LastVerificationAfterMutationOutcome = resultClassification ?? "";
            runState.LastVerificationAfterMutationUtc = effectiveCreatedUtc;
        }

        if (runState.ExecutedToolCalls.Count > 40)
            runState.ExecutedToolCalls.RemoveRange(0, runState.ExecutedToolCalls.Count - 40);
    }

    private static TaskboardSkipDecisionRecord BuildSkipDecisionRecord(
        string workspaceRoot,
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionBridgeResult bridge,
        TaskboardStateSatisfactionResultRecord satisfaction)
    {
        return new TaskboardSkipDecisionRecord
        {
            WorkspaceRoot = workspaceRoot,
            RunStateId = runState.RunStateId,
            PlanImportId = runState.PlanImportId,
            BatchId = batch.BatchId,
            WorkItemId = workItem.WorkItemId,
            WorkItemTitle = workItem.Title,
            StepId = satisfaction.StepId,
            SkipFamily = satisfaction.CheckFamily,
            ToolName = bridge.ToolRequest?.ToolName ?? "",
            ReasonCode = satisfaction.ReasonCode,
            EvidenceSource = satisfaction.TrustSource,
            EvidenceSummary = satisfaction.EvidenceSummary,
            UsedFileTouchFastPath = satisfaction.UsedFileTouchFastPath,
            RepeatedTouchesAvoidedCount = satisfaction.RepeatedTouchesAvoidedCount,
            LinkedFilePaths = [.. satisfaction.CheckedFilePaths],
            LinkedArtifactIds = [.. satisfaction.LinkedArtifactIds],
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private static string BuildSatisfactionCheckSummary(string workItemTitle, TaskboardStateSatisfactionResultRecord satisfaction)
    {
        var title = FirstNonEmpty(workItemTitle, satisfaction.WorkItemId, "(unknown)");
        return satisfaction.Satisfied && satisfaction.SkipAllowed
            ? $"State already satisfied for `{title}` via {FirstNonEmpty(satisfaction.ReasonCode, "(none)")}: {FirstNonEmpty(satisfaction.EvidenceSummary, "(none)")}"
            : $"State check for `{title}`: {FirstNonEmpty(satisfaction.ReasonCode, "not_satisfied")} — {FirstNonEmpty(satisfaction.EvidenceSummary, "(none)")}";
    }

    private static string BuildSatisfactionSkipSummary(string workItemTitle, TaskboardStateSatisfactionResultRecord satisfaction)
    {
        return $"Skipped `{FirstNonEmpty(workItemTitle, satisfaction.WorkItemId, "(unknown)")}` because state was already satisfied ({FirstNonEmpty(satisfaction.ReasonCode, "(none)")}). {FirstNonEmpty(satisfaction.EvidenceSummary, "(none)")}";
    }

    private static RunnableSelectionResult TrySelectNextRunnable(TaskboardPlanRunStateRecord runState, string selectedBatchId)
    {
        var candidateBatches = string.IsNullOrWhiteSpace(selectedBatchId)
            ? runState.Batches.OrderBy(batch => batch.BatchNumber).ToList()
            : runState.Batches
                .Where(batch => string.Equals(batch.BatchId, selectedBatchId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(batch => batch.BatchNumber)
                .ToList();

        if (!string.IsNullOrWhiteSpace(selectedBatchId) && candidateBatches.Count == 0)
        {
            return RunnableSelectionResult.Fail("selected_batch_not_found", "Run Selected Batch blocked: selected batch does not belong to the active plan.");
        }

        foreach (var batch in candidateBatches)
        {
            if (batch.Status is TaskboardBatchRuntimeStatus.Completed or TaskboardBatchRuntimeStatus.Skipped)
                continue;

            var workItem = TaskboardFollowUpWorkItemSelectionService.SelectPreferredPendingWorkItem(runState, batch);
            if (workItem is not null)
                return RunnableSelectionResult.Succeed(batch, workItem);
        }

        if (runState.PlanStatus == TaskboardPlanRuntimeStatus.Completed)
        {
            return RunnableSelectionResult.Fail("completed", $"Active plan `{runState.PlanTitle}` is already complete.");
        }

        if (runState.PlanStatus == TaskboardPlanRuntimeStatus.PausedManualOnly)
        {
            return RunnableSelectionResult.Fail("manual_only", FirstNonEmpty(runState.LastBlockerReason, "Auto-run paused at a manual-only work item."));
        }

        if (runState.PlanStatus == TaskboardPlanRuntimeStatus.Blocked)
        {
            return RunnableSelectionResult.Fail("blocked", FirstNonEmpty(runState.LastBlockerReason, "Auto-run is blocked."));
        }

        if (runState.PlanStatus == TaskboardPlanRuntimeStatus.Failed)
        {
            return RunnableSelectionResult.Fail("failed", FirstNonEmpty(runState.LastBlockerReason, runState.LastResultSummary, "Auto-run failed."));
        }

        return RunnableSelectionResult.Fail(
            "no_runnable_items",
            string.IsNullOrWhiteSpace(selectedBatchId)
                ? "Run Active Plan skipped: no runnable work items remain."
                : "Run Selected Batch skipped: selected batch has no runnable work items remaining.");
    }

    private static bool RepairInvalidReopenedSetupWorkItems(TaskboardPlanRunStateRecord runState, string selectedBatchId)
    {
        var candidateBatches = string.IsNullOrWhiteSpace(selectedBatchId)
            ? runState.Batches.OrderBy(batch => batch.BatchNumber).ToList()
            : runState.Batches
                .Where(batch => string.Equals(batch.BatchId, selectedBatchId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(batch => batch.BatchNumber)
                .ToList();
        if (candidateBatches.Count == 0)
            return false;

        foreach (var batch in candidateBatches)
        {
            foreach (var item in batch.WorkItems
                         .Where(current => !current.IsDecomposedItem
                                           && current.Status == TaskboardWorkItemRuntimeStatus.Pending
                                           && current.LastResultSummary.Contains("Reopened `", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(current => current.Ordinal <= 0 ? int.MaxValue : current.Ordinal)
                         .ToList())
            {
                if (LooksLikeSecondaryProjectSetupHint(item.Title, item.PromptText, item.Summary, item.ExpectedArtifact))
                    continue;

                batch.WorkItems.RemoveAll(current =>
                    current.IsDecomposedItem
                    && current.Status == TaskboardWorkItemRuntimeStatus.Pending
                    && string.Equals(current.SourceWorkItemId, item.WorkItemId, StringComparison.OrdinalIgnoreCase));

                item.Status = TaskboardWorkItemRuntimeStatus.Skipped;
                item.LastResultKind = "decomposed";
                item.LastResultSummary = $"Restored `{item.Title}` to its prior decomposed state because secondary-project setup reopening does not apply to this work item.";
                item.LastExecutionGoalResolution = new TaskboardExecutionGoalResolution();
                item.UpdatedUtc = DateTime.UtcNow.ToString("O");

                batch.CompletedWorkItemCount = batch.WorkItems.Count(current => current.Status == TaskboardWorkItemRuntimeStatus.Passed);
                batch.TotalWorkItemCount = batch.WorkItems.Count(current => current.Status != TaskboardWorkItemRuntimeStatus.Skipped || current.IsDecomposedItem);
                if (batch.WorkItems.All(current => current.Status is TaskboardWorkItemRuntimeStatus.Passed or TaskboardWorkItemRuntimeStatus.Skipped))
                    batch.Status = TaskboardBatchRuntimeStatus.Completed;

                NormalizeCompletionState(runState);
                runState.PlanStatus = TaskboardPlanRuntimeStatus.Active;
                runState.CurrentBatchId = "";
                runState.CurrentWorkItemId = "";
                runState.LastResultKind = "restored_setup_reopen_state";
                runState.LastResultSummary = item.LastResultSummary;
                runState.CurrentRunPhaseCode = "repairing_reopen_state";
                runState.CurrentRunPhaseText = $"Restoring `{item.Title}` to its prior decomposed state.";
                runState.LatestStepSummary = item.LastResultSummary;
                ClearTerminalSummaryState(runState);
                AddEvent(runState, "restored_invalid_setup_reopen_state", batch.BatchId, item.WorkItemId, item.LastResultSummary);
                return true;
            }
        }

        return false;
    }

    private bool ReopenUnsatisfiedCompletedSetupWorkItems(
        string workspaceRoot,
        TaskboardPlanRunStateRecord runState,
        string selectedBatchId,
        RamDbService ramDbService)
    {
        var candidateBatches = string.IsNullOrWhiteSpace(selectedBatchId)
            ? runState.Batches.OrderBy(batch => batch.BatchNumber).ToList()
            : runState.Batches
                .Where(batch => string.Equals(batch.BatchId, selectedBatchId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(batch => batch.BatchNumber)
                .ToList();
        if (candidateBatches.Count == 0)
            return false;

        var builderOperationResolutionService = new TaskboardBuilderOperationResolutionService();
        foreach (var batch in candidateBatches)
        {
            foreach (var item in batch.WorkItems
                         .Where(current => !current.IsDecomposedItem
                                           && current.Status is TaskboardWorkItemRuntimeStatus.Passed or TaskboardWorkItemRuntimeStatus.Skipped)
                         .OrderBy(current => current.Ordinal <= 0 ? int.MaxValue : current.Ordinal)
                         .ToList())
            {
                if (batch.WorkItems.Any(current =>
                        current.IsDecomposedItem
                        && current.Status == TaskboardWorkItemRuntimeStatus.Pending
                        && string.Equals(current.SourceWorkItemId, item.WorkItemId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _workItemStateRefreshService.Refresh(runState, batch, item);

                var runWorkItem = _workItemStateRefreshService.ToRunWorkItem(item);
                var resolution = builderOperationResolutionService.Resolve(workspaceRoot, runWorkItem, runState.PlanTitle);
                if (!resolution.Matched || !IsReopenableProjectSetupWorkItem(item, resolution.ToolName))
                    continue;

                var request = new ToolRequest
                {
                    ToolName = resolution.ToolName,
                    Arguments = new Dictionary<string, string>(resolution.Arguments, StringComparer.OrdinalIgnoreCase),
                    Reason = resolution.Reason,
                    ExecutionAllowed = true
                };
                var satisfaction = _stateSatisfactionService.EvaluatePlannedStep(
                    workspaceRoot,
                    runState,
                    item.WorkItemId,
                    item.Title,
                    FirstNonEmpty(item.WorkFamily, runWorkItem.WorkFamily),
                    request,
                    resolution.ResolvedTargetPath,
                    ramDbService,
                    allowNoSkipReuse: true);
                if (satisfaction.Satisfied && satisfaction.SkipAllowed)
                    continue;

                ReopenCompletedSetupSourceItem(runState, batch, item, request, satisfaction.EvidenceSummary);
                return true;
            }
        }

        return false;
    }

    private static bool IsReopenableProjectSetupWorkItem(TaskboardWorkItemRunStateRecord item, string toolName)
    {
        var hint = FirstNonEmpty(item.Title, item.PromptText, item.Summary, item.ExpectedArtifact);
        var secondaryProjectSetup = LooksLikeSecondaryProjectSetupHint(hint);
        if (!secondaryProjectSetup)
            return false;

        return string.Equals(toolName, "add_project_to_solution", StringComparison.OrdinalIgnoreCase)
               || string.Equals(toolName, "add_dotnet_project_reference", StringComparison.OrdinalIgnoreCase)
               || (string.Equals(toolName, "create_dotnet_project", StringComparison.OrdinalIgnoreCase)
                   && ContainsAny(hint, "classlib", "xunit", "test project", ".core", ".tests", "contracts", "storage", "repository"));
    }

    private static bool LooksLikeSecondaryProjectSetupHint(params string?[] values)
    {
        var hint = FirstNonEmpty(values);
        return ContainsAny(
            hint,
            ".core",
            " core ",
            ".tests",
            " tests ",
            "test project",
            "xunit",
            "classlib",
            "contracts",
            "storage",
            "repository");
    }

    private static void ReopenCompletedSetupSourceItem(
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord sourceItem,
        ToolRequest request,
        string evidenceSummary)
    {
        batch.WorkItems.RemoveAll(current =>
            current.IsDecomposedItem
            && string.Equals(current.SourceWorkItemId, sourceItem.WorkItemId, StringComparison.OrdinalIgnoreCase));

        sourceItem.DirectToolRequest = request.Clone();
        sourceItem.Status = TaskboardWorkItemRuntimeStatus.Pending;
        sourceItem.LastResultKind = "";
        sourceItem.LastResultSummary = FirstNonEmpty(
            $"Reopened `{sourceItem.Title}` because the workspace is still missing required setup state. {evidenceSummary}",
            $"Reopened `{sourceItem.Title}` because the workspace is still missing required setup state.");
        sourceItem.LastExecutionGoalResolution = new TaskboardExecutionGoalResolution();
        sourceItem.UpdatedUtc = DateTime.UtcNow.ToString("O");

        batch.Status = TaskboardBatchRuntimeStatus.Pending;
        batch.CurrentWorkItemId = "";
        batch.LastExecutionGoalSummary = "";
        batch.LastResultSummary = sourceItem.LastResultSummary;
        batch.CompletedWorkItemCount = batch.WorkItems.Count(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed);
        batch.TotalWorkItemCount = batch.WorkItems.Count(item => item.Status != TaskboardWorkItemRuntimeStatus.Skipped || item.IsDecomposedItem);

        runState.PlanStatus = TaskboardPlanRuntimeStatus.Active;
        runState.CurrentBatchId = "";
        runState.CurrentWorkItemId = "";
        runState.LastResultKind = "reopened_unsatisfied_setup";
        runState.LastResultSummary = sourceItem.LastResultSummary;
        runState.LastBlockerReason = "";
        runState.LastBlockerOrigin = "";
        runState.LastBlockerWorkItemId = "";
        runState.LastBlockerWorkItemTitle = "";
        runState.LastBlockerPhase = "";
        runState.LastBlockerWorkFamily = "";
        runState.LastBlockerPhraseFamily = "";
        runState.LastBlockerOperationKind = "";
        runState.LastBlockerStackFamily = "";
        runState.LastExecutionGoalResolution = new TaskboardExecutionGoalResolution();
        runState.LastExecutionGoalSummary = "";
        runState.LastExecutionGoalBlockerCode = "";
        runState.LastFollowupBatchId = "";
        runState.LastFollowupBatchTitle = "";
        runState.LastFollowupWorkItemId = "";
        runState.LastFollowupWorkItemTitle = "";
        runState.LastFollowupSelectionReason = "";
        runState.LastFollowupWorkFamily = "";
        runState.LastFollowupPhraseFamily = "";
        runState.LastFollowupOperationKind = "";
        runState.LastFollowupStackFamily = "";
        runState.LastFollowupPhraseFamilyReasonCode = "";
        runState.LastFollowupOperationKindReasonCode = "";
        runState.LastFollowupStackFamilyReasonCode = "";
        runState.LastFollowupResolutionSummary = "";
        runState.LastPostChainReconciliationSummary = "";
        runState.CurrentRunPhaseCode = "reopening_unsatisfied_setup";
        runState.CurrentRunPhaseText = $"Reopening `{sourceItem.Title}` because required project setup is still missing.";
        runState.LatestStepSummary = sourceItem.LastResultSummary;
        runState.CompletedWorkItemCount = runState.Batches.Sum(current => current.WorkItems.Count(item => item.Status == TaskboardWorkItemRuntimeStatus.Passed));
        runState.TotalWorkItemCount = runState.Batches.Sum(current => current.WorkItems.Count(item => item.Status != TaskboardWorkItemRuntimeStatus.Skipped || item.IsDecomposedItem));
        ClearTerminalSummaryState(runState);
        AddEvent(runState, "reopened_unsatisfied_setup", batch.BatchId, sourceItem.WorkItemId, sourceItem.LastResultSummary);
    }

    private static string FormatPlanStatus(TaskboardPlanRuntimeStatus status)
    {
        return status switch
        {
            TaskboardPlanRuntimeStatus.PausedManualOnly => "paused_manual_only",
            _ => status.ToString().ToLowerInvariant()
        };
    }

    private static string FormatResultKind(string resultKind)
    {
        return string.IsNullOrWhiteSpace(resultKind) ? "(none)" : resultKind;
    }

    private static string BuildExecutionGoalSummary(TaskboardExecutionGoalResolution? goalResolution)
    {
        if (goalResolution is null || goalResolution.GoalKind == TaskboardExecutionGoalKind.Unknown)
            return "";

        var selected = FirstNonEmpty(
            goalResolution.Goal.SelectedChainTemplateId,
            goalResolution.Goal.SelectedToolId,
            goalResolution.Blocker.Code == TaskboardExecutionGoalBlockerCode.None
                ? ""
                : goalResolution.Blocker.Code.ToString().ToLowerInvariant());
        var summary = goalResolution.GoalKind.ToString().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(selected))
            summary += $" -> {selected}";
        if (!string.IsNullOrWhiteSpace(goalResolution.ResolvedTargetPath))
            summary += $" ({goalResolution.ResolvedTargetPath})";

        return summary;
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

    private static string FirstMeaningful(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!IsMissingOrUnknown(value))
                return value!.Trim();
        }

        return "";
    }

    private static T? DeserializeArtifact<T>(ArtifactRecord? artifact)
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

    private static DateTime ParseUtc(string? value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.MinValue;
    }

    private static bool IsMissingOrUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), "unknown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "(none)", StringComparison.OrdinalIgnoreCase);
    }

    private static TaskboardRepairContinuationEvidence LoadRepairContinuationEvidence(
        string workspaceRoot,
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord workItem,
        RamDbService ramDbService,
        DateTime notBeforeUtc)
    {
        var targets = CollectRepairTargetPaths(runState, workItem);
        var sources = CollectRepairSourcePaths(runState, workItem);

        var verificationCandidate = ramDbService.LoadArtifactsByType(workspaceRoot, "verification_result", 40)
            .Select(artifact => new
            {
                Artifact = artifact,
                Payload = DeserializeArtifact<VerificationOutcomeRecord>(artifact),
                Utc = ResolveArtifactUtc(artifact, DeserializeArtifact<VerificationOutcomeRecord>(artifact)?.CreatedUtc)
            })
            .Where(candidate =>
                candidate.Payload is not null
                && IsVerificationRelevant(candidate.Payload, targets, sources)
                && IsArtifactFreshEnough(candidate.Utc, notBeforeUtc))
            .OrderByDescending(candidate => candidate.Utc)
            .ThenByDescending(candidate => candidate.Artifact.Id)
            .FirstOrDefault();
        var verificationArtifact = verificationCandidate?.Artifact;
        var verification = verificationCandidate?.Payload;
        var verificationUtc = verificationCandidate?.Utc ?? DateTime.MinValue;

        var applyCandidate = ramDbService.LoadArtifactsByType(workspaceRoot, "patch_apply_result", 40)
            .Select(artifact => new
            {
                Artifact = artifact,
                Payload = DeserializeArtifact<PatchApplyResultRecord>(artifact),
                Utc = ResolveArtifactUtc(artifact, DeserializeArtifact<PatchApplyResultRecord>(artifact)?.AppliedUtc)
            })
            .Where(candidate =>
                candidate.Payload is not null
                && IsPatchApplyRelevant(candidate.Payload, targets, sources)
                && IsArtifactFreshEnough(candidate.Utc, notBeforeUtc))
            .OrderByDescending(candidate => candidate.Utc)
            .ThenByDescending(candidate => candidate.Artifact.Id)
            .FirstOrDefault();
        var applyArtifact = applyCandidate?.Artifact;
        var apply = applyCandidate?.Payload;
        var applyUtc = applyCandidate?.Utc ?? DateTime.MinValue;

        var draftCandidate = ramDbService.LoadArtifactsByType(workspaceRoot, "patch_draft", 40)
            .Select(artifact => new
            {
                Artifact = artifact,
                Payload = DeserializeArtifact<PatchDraftRecord>(artifact),
                Utc = ResolveArtifactUtc(artifact, DeserializeArtifact<PatchDraftRecord>(artifact)?.CreatedUtc)
            })
            .Where(candidate =>
                candidate.Payload is not null
                && IsPatchDraftRelevant(candidate.Payload, targets, sources)
                && IsArtifactFreshEnough(candidate.Utc, notBeforeUtc))
            .OrderByDescending(candidate => candidate.Utc)
            .ThenByDescending(candidate => candidate.Artifact.Id)
            .FirstOrDefault();
        var draftArtifact = draftCandidate?.Artifact;
        var draft = draftCandidate?.Payload;
        var draftUtc = draftCandidate?.Utc ?? DateTime.MinValue;

        var hasVerifiedMutationProof = verification is not null
            && apply is not null
            && verificationUtc >= applyUtc;

        return new TaskboardRepairContinuationEvidence
        {
            VerificationArtifact = verificationArtifact,
            Verification = verification,
            VerificationUtc = verificationUtc,
            ApplyArtifact = applyArtifact,
            ApplyResult = apply,
            ApplyUtc = applyUtc,
            DraftArtifact = draftArtifact,
            Draft = draft,
            DraftUtc = draftUtc,
            HasVerifiedMutationProof = hasVerifiedMutationProof,
            IsReconciliationOnly = apply is not null
                && string.Equals(apply.Draft.DraftKind, "rebuild_symbol_recovery", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static bool HasVerifiedRepairClosureProof(TaskboardPlanRunStateRecord runState)
    {
        if (runState is null)
            return false;

        if (string.Equals(runState.LastRepairContinuationStatus, "verified_after_symbol_reconciliation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(runState.LastRepairContinuationStatus, "verified_after_mutation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var latestVerification = runState.ExecutedToolCalls
            .Where(call =>
                string.Equals(call.Stage, "completed", StringComparison.OrdinalIgnoreCase)
                && string.Equals(call.ToolName, "verify_patch_draft", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(call => ParseUtc(call.CreatedUtc))
            .FirstOrDefault();

        return latestVerification is not null
            && string.Equals(latestVerification.ResultClassification, "verified_fixed", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime ResolveArtifactUtc(ArtifactRecord? artifact, string? recordUtc)
    {
        return ParseUtc(FirstNonEmpty(recordUtc, artifact?.UpdatedUtc, artifact?.CreatedUtc));
    }

    private static bool IsArtifactFreshEnough(DateTime artifactUtc, DateTime notBeforeUtc)
    {
        if (artifactUtc == DateTime.MinValue)
            return false;

        return notBeforeUtc == DateTime.MinValue || artifactUtc >= notBeforeUtc;
    }

    private static HashSet<string> CollectRepairTargetPaths(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord workItem)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPath(paths, ReadArgument(workItem.DirectToolRequest, "active_target"));
        AddPath(paths, ReadArgument(workItem.DirectToolRequest, "solution_path"));
        AddPath(paths, ReadArgument(workItem.DirectToolRequest, "project_path"));
        AddPath(paths, ReadArgument(workItem.DirectToolRequest, "path"));
        AddPath(paths, workItem.LastExecutionGoalResolution.ResolvedTargetPath);
        AddPath(paths, runState.LastFailureTargetPath);

        var sourceWorkItem = FindWorkItem(runState, workItem.SourceWorkItemId);
        if (sourceWorkItem is not null)
        {
            AddPath(paths, sourceWorkItem.LastExecutionGoalResolution.ResolvedTargetPath);
            AddPath(paths, ReadArgument(sourceWorkItem.DirectToolRequest, "active_target"));
            AddPath(paths, ReadArgument(sourceWorkItem.DirectToolRequest, "solution_path"));
            AddPath(paths, ReadArgument(sourceWorkItem.DirectToolRequest, "project_path"));
            AddPath(paths, ReadArgument(sourceWorkItem.DirectToolRequest, "path"));
            AddPath(paths, sourceWorkItem.ExpectedArtifact);
        }

        return paths;
    }

    private static HashSet<string> CollectRepairSourcePaths(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord workItem)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPath(paths, runState.LastFailureSourcePath);
        AddPath(paths, workItem.ExpectedArtifact);

        var sourceWorkItem = FindWorkItem(runState, workItem.SourceWorkItemId);
        if (sourceWorkItem is not null)
        {
            AddPath(paths, sourceWorkItem.ExpectedArtifact);
            AddPath(paths, ReadArgument(sourceWorkItem.DirectToolRequest, "reference_path"));
            AddPath(paths, ReadArgument(sourceWorkItem.DirectToolRequest, "path"));
        }

        return paths;
    }

    private static TaskboardWorkItemRunStateRecord? FindWorkItem(
        TaskboardPlanRunStateRecord runState,
        string workItemId)
    {
        if (string.IsNullOrWhiteSpace(workItemId))
            return null;

        foreach (var batch in runState.Batches)
        {
            var item = batch.WorkItems.FirstOrDefault(candidate =>
                string.Equals(candidate.WorkItemId, workItemId, StringComparison.OrdinalIgnoreCase));
            if (item is not null)
                return item;
        }

        return null;
    }

    private static bool IsVerificationRelevant(
        VerificationOutcomeRecord verification,
        HashSet<string> targets,
        HashSet<string> sources)
    {
        return PathMatches(verification.ResolvedTarget, targets)
            || PathMatches(verification.ResolvedTarget, sources)
            || (targets.Count == 0 && sources.Count == 0 && !string.IsNullOrWhiteSpace(verification.OutcomeClassification));
    }

    private static bool IsPatchApplyRelevant(
        PatchApplyResultRecord apply,
        HashSet<string> targets,
        HashSet<string> sources)
    {
        return PathMatches(apply.Draft.TargetProjectPath, targets)
            || PathMatches(apply.Draft.TargetFilePath, sources)
            || PathMatches(apply.Draft.TargetFilePath, targets)
            || (targets.Count == 0 && sources.Count == 0 && !string.IsNullOrWhiteSpace(apply.Draft.TargetFilePath));
    }

    private static bool IsPatchDraftRelevant(
        PatchDraftRecord draft,
        HashSet<string> targets,
        HashSet<string> sources)
    {
        return PathMatches(draft.TargetProjectPath, targets)
            || PathMatches(draft.TargetFilePath, sources)
            || PathMatches(draft.TargetFilePath, targets)
            || (targets.Count == 0 && sources.Count == 0 && !string.IsNullOrWhiteSpace(draft.TargetFilePath));
    }

    private static bool PathMatches(string? candidate, HashSet<string> expected)
    {
        var normalizedCandidate = NormalizePath(candidate);
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return false;

        foreach (var value in expected)
        {
            if (string.Equals(value, normalizedCandidate, StringComparison.OrdinalIgnoreCase)
                || normalizedCandidate.EndsWith("/" + value, StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("/" + normalizedCandidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddPath(HashSet<string> paths, string? value)
    {
        var normalized = NormalizePath(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        paths.Add(normalized);
    }

    private static string NormalizePath(string? value)
    {
        return (value ?? "")
            .Replace('\\', '/')
            .Trim()
            .Trim('"');
    }

    private static string TryReadJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
    }

    private static List<string> TryReadJsonStringList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value.Trim());
        }

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ApplyToolRequestFallback(TaskboardWorkItemRunStateRecord item)
    {
        if (item.DirectToolRequest is null)
            return;

        var toolName = NormalizeValue(item.DirectToolRequest.ToolName);
        if (string.IsNullOrWhiteSpace(toolName))
            return;

        var path = FirstMeaningful(
            ReadArgument(item.DirectToolRequest, "path"),
            ReadArgument(item.DirectToolRequest, "project"),
            ReadArgument(item.DirectToolRequest, "project_path"),
            ReadArgument(item.DirectToolRequest, "reference_path"),
            ReadArgument(item.DirectToolRequest, "solution_path"),
            ReadArgument(item.DirectToolRequest, "output_path"),
            ReadArgument(item.DirectToolRequest, "directory"),
            ReadArgument(item.DirectToolRequest, "build_dir"),
            ReadArgument(item.DirectToolRequest, "source_dir"));
        var hint = $"{item.Title} {item.Summary} {item.PromptText} {path}".Trim();

        if (IsMissingOrUnknown(item.TargetStack))
        {
            item.TargetStack = toolName switch
            {
                "create_dotnet_solution" or "create_dotnet_project" or "add_project_to_solution" or "add_dotnet_project_reference" or "create_dotnet_page_view" or "create_dotnet_viewmodel" or "register_navigation" or "register_di_service" or "initialize_sqlite_storage_boundary" or "dotnet_build" or "dotnet_test" => "dotnet_desktop",
                "create_cmake_project" or "create_cpp_source_file" or "create_cpp_header_file" or "create_c_source_file" or "create_c_header_file" or "cmake_configure" or "cmake_build" or "ctest_run" => "native_cpp_desktop",
                _ => IsNativePath(path) ? "native_cpp_desktop" : ""
            };
        }

        if (IsMissingOrUnknown(item.OperationKind))
        {
            item.OperationKind = toolName switch
            {
                "create_dotnet_solution" => "create_solution",
                "create_dotnet_project" => InferDotnetProjectOperation(hint),
                "add_project_to_solution" => LooksLikePlainSiblingProjectScaffold(hint)
                    ? "add_project_to_solution"
                    : ContainsAny(hint, "tests", ".tests")
                    ? "attach_test_project"
                    : ContainsAny(hint, ".core", ".contracts", ".storage", ".services", " core ", "contracts", "models")
                        ? "attach_core_library"
                        : "add_project_to_solution",
                "add_dotnet_project_reference" => LooksLikePlainSiblingProjectReference(hint)
                    ? "add_project_reference"
                    : ContainsAny(hint, ".core", ".contracts", ".storage", ".services", " core ", "contracts", "models")
                    ? "add_domain_reference"
                    : "add_project_reference",
                "create_dotnet_page_view" => "write_page",
                "create_dotnet_viewmodel" => ContainsAny(hint, "appstate", "app state") ? "write_app_state" : "write_shell_viewmodel",
                "register_navigation" => ContainsAny(hint, "shellregistration", "shell registration", "navigationregistry", "navigation registry")
                    ? "write_shell_registration"
                    : "write_navigation_item",
                "register_di_service" => "write_storage_impl",
                "initialize_sqlite_storage_boundary" => "write_storage_contract",
                "dotnet_build" => "build_solution",
                "dotnet_test" => "run_test_project",
                "make_dir" => InferMakeDirOperation(path),
                "create_cmake_project" => "write_cmake_lists",
                "create_cpp_source_file" => InferNativeSourceOperation(path),
                "create_cpp_header_file" => InferNativeHeaderOperation(path),
                "cmake_configure" => "configure_cmake",
                "cmake_build" => "build_native_workspace",
                "write_file" => InferWriteFileOperation(hint),
                _ => ""
            };
        }

        if (IsMissingOrUnknown(item.PhraseFamily))
            item.PhraseFamily = InferPhraseFamily(item, toolName);

        if (IsMissingOrUnknown(item.TemplateId))
            item.TemplateId = InferTemplateId(item);

        NormalizePlainSiblingProjectRouting(item);
        NormalizeDomainContractsRouting(item);
        NormalizeTestSupportWriteRouting(item);

        if (IsMissingOrUnknown(item.WorkFamily))
            item.WorkFamily = InferWorkFamily(item);

        if (IsMissingOrUnknown(item.PhraseFamily))
            item.PhraseFamily = InferPhraseFamily(item, toolName);

        if (IsMissingOrUnknown(item.TemplateId))
            item.TemplateId = InferTemplateId(item);

        NormalizePlainSiblingProjectRouting(item);
        NormalizeDomainContractsRouting(item);
        NormalizeTestSupportWriteRouting(item);
    }

    private static string InferDotnetProjectOperation(string hint)
    {
        if (LooksLikePlainSiblingProjectScaffold(hint))
            return "create_project";

        if (ContainsAny(hint, "tests", ".tests"))
            return "create_test_project";

        if (ContainsAny(hint, ".core", ".contracts", ".storage", ".services", ".repository", "contracts", "models", "repository", "domain"))
            return "create_core_library";

        return "create_project";
    }

    private static string InferMakeDirOperation(string path)
    {
        if (ContainsAny(path, "\\state\\", "/state/"))
            return "make_state_dir";
        if (ContainsAny(path, "\\storage\\", "/storage/"))
            return "make_storage_dir";
        if (ContainsAny(path, "\\contracts\\", "/contracts/"))
            return "make_contracts_dir";
        if (ContainsAny(path, "\\models\\", "/models/"))
            return "make_models_dir";
        if (ContainsAny(path, "\\include\\", "/include/"))
            return "make_include_dir";
        if (ContainsAny(path, "\\src\\", "/src/"))
            return "make_src_dir";

        return "";
    }

    private static string InferNativeSourceOperation(string path)
    {
        if (ContainsAny(path, "main.cpp"))
            return "write_main_cpp";
        if (ContainsAny(path, "storage"))
            return "write_storage_source";

        return "write_app_window_source";
    }

    private static string InferNativeHeaderOperation(string path)
    {
        if (ContainsAny(path, "findings"))
            return "write_findings_panel";
        if (ContainsAny(path, "history"))
            return "write_history_panel";
        if (ContainsAny(path, "settings"))
            return "write_settings_panel";
        if (ContainsAny(path, "dashboard"))
            return "write_dashboard_panel";
        if (ContainsAny(path, "navigation"))
            return "write_navigation_header";
        if (ContainsAny(path, "appstate", "app_state"))
            return "write_app_state_header";
        if (ContainsAny(path, "storage"))
            return "write_storage_header";
        if (ContainsAny(path, "contract"))
            return "write_contract_header";
        if (ContainsAny(path, "model"))
            return "write_domain_model_header";

        return "write_app_window_header";
    }

    private static string InferWriteFileOperation(string hint)
    {
        if (ContainsAny(hint, "checkregistry", "check registry"))
            return "write_check_registry";
        if (ContainsAny(hint, "snapshotbuilder", "snapshot builder"))
            return "write_snapshot_builder";
        if (ContainsAny(hint, "findingsnormalizer", "findings normalizer"))
            return "write_findings_normalizer";
        if (ContainsAny(hint, "repository") && ContainsAny(hint, "interface", "contract"))
            return "write_repository_contract";
        if (ContainsAny(hint, "repository"))
            return "write_repository_impl";
        if (ContainsAny(hint, "contract"))
            return "write_contract_file";
        if (ContainsAny(hint, "model"))
            return "write_domain_model_file";

        return "";
    }

    private static bool PathsResolveToSameWorkspaceTarget(string workspaceRoot, string? leftPath, string? rightPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)
            || string.IsNullOrWhiteSpace(leftPath)
            || string.IsNullOrWhiteSpace(rightPath))
        {
            return false;
        }

        try
        {
            var leftFullPath = Path.GetFullPath(Path.Combine(workspaceRoot, leftPath.Trim()));
            var rightFullPath = Path.GetFullPath(Path.Combine(workspaceRoot, rightPath.Trim()));
            return string.Equals(leftFullPath, rightFullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string InferPhraseFamily(TaskboardWorkItemRunStateRecord item, string toolName)
    {
        if (!IsMissingOrUnknown(item.WorkFamily))
        {
            return item.WorkFamily switch
            {
                "solution_scaffold" => "solution_scaffold",
                "ui_shell_sections" => "ui_shell_sections",
                "ui_wiring" or "app_state_wiring" => "add_navigation_app_state",
                "viewmodel_scaffold" => "ui_shell_sections",
                "storage_bootstrap" => "setup_storage_layer",
                "core_domain_models_contracts" => "core_domain_models_contracts",
                "repository_scaffold" => "repository_scaffold",
                "check_runner" => "check_runner",
                "findings_pipeline" => "findings_pipeline",
                "build_verify" => "build_verify",
                "native_project_bootstrap" => "native_project_bootstrap",
                _ => ""
            };
        }

        return toolName switch
        {
            "create_dotnet_solution" or "create_dotnet_project" or "add_project_to_solution" => "solution_scaffold",
            "create_dotnet_page_view" or "create_dotnet_viewmodel" => "ui_shell_sections",
            "register_navigation" => "add_navigation_app_state",
            "initialize_sqlite_storage_boundary" or "register_di_service" => "setup_storage_layer",
            "dotnet_build" => "build_verify",
            "dotnet_test" => "check_runner",
            "create_cmake_project" => "cmake_bootstrap",
            "cmake_build" or "cmake_configure" => "build_verify",
            _ => ""
        };
    }

    private static string InferTemplateId(TaskboardWorkItemRunStateRecord item)
    {
        var stack = FirstMeaningful(item.TargetStack);
        var phraseFamily = FirstMeaningful(item.PhraseFamily);
        var operationKind = FirstMeaningful(item.OperationKind);
        if (string.IsNullOrWhiteSpace(stack) || string.IsNullOrWhiteSpace(phraseFamily))
            return "";

        if (string.Equals(stack, "dotnet_desktop", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(operationKind, "run_test_project", StringComparison.OrdinalIgnoreCase))
                return "workspace.test_verify.v1";

            return phraseFamily switch
            {
                "solution_scaffold" => "dotnet.solution_scaffold.v1",
                "ui_shell_sections" => "dotnet.shell_page_set_scaffold.v1",
                "add_navigation_app_state" => "dotnet.navigation_wireup.v1",
                "setup_storage_layer" => "dotnet.sqlite_storage_bootstrap.v1",
                "core_domain_models_contracts" => "dotnet.domain_contracts_scaffold.v1",
                "repository_scaffold" => "dotnet.repository_scaffold.v1",
                "check_runner" => "dotnet.check_runner_scaffold.v1",
                "findings_pipeline" => "dotnet.findings_pipeline_bootstrap.v1",
                "build_verify" => "workspace.build_verify.v1",
                _ => ""
            };
        }

        if (string.Equals(stack, "native_cpp_desktop", StringComparison.OrdinalIgnoreCase))
        {
            return phraseFamily switch
            {
                "ui_shell_sections" => "cpp.win32_shell_page_set.v1",
                "native_project_bootstrap" => "cmake.project_bootstrap.v1",
                "build_verify" => "workspace.native_build_verify.v1",
                _ => ""
            };
        }

        return "";
    }

    private static string InferWorkFamily(TaskboardWorkItemRunStateRecord item)
    {
        return FirstMeaningful(item.OperationKind) switch
        {
            "create_solution" or "create_project" or "create_test_project" or "add_project_to_solution" or "attach_test_project" or "add_project_reference" => "solution_scaffold",
            "create_core_library" or "attach_core_library" or "add_domain_reference" or "make_contracts_dir" or "make_models_dir" or "write_contract_file" or "write_domain_model_file" => "core_domain_models_contracts",
            "write_page" => "ui_shell_sections",
            "write_navigation_item" or "write_shell_registration" => "ui_wiring",
            "write_app_state" => "app_state_wiring",
            "write_shell_viewmodel" => "viewmodel_scaffold",
            "write_storage_contract" or "write_storage_impl" or "make_storage_dir" => "storage_bootstrap",
            "write_repository_contract" or "write_repository_impl" or "write_contract_file" or "write_domain_model_file" or "make_contracts_dir" or "make_models_dir" => "repository_scaffold",
            "write_check_registry" or "write_snapshot_builder" or "write_findings_normalizer" => "findings_pipeline",
            "run_test_project" => "check_runner",
            "build_solution" or "build_native_workspace" or "configure_cmake" => "build_verify",
            "make_src_dir" or "make_include_dir" or "write_cmake_lists" => "native_project_bootstrap",
            _ => ""
        };
    }

    private static string ReadArgument(ToolRequest? request, string key)
    {
        if (request is null)
            return "";

        return request.Arguments.TryGetValue(key, out var value)
            ? value ?? ""
            : "";
    }

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNativePath(string? path)
    {
        return ContainsAny(path, ".cpp", ".h", "cmakelists.txt", "\\include\\", "/include/", "\\src\\", "/src/");
    }

    private static void NormalizePlainSiblingProjectRouting(TaskboardWorkItemRunStateRecord item)
    {
        var text = NormalizeValue(string.Join(" ",
            item.Title,
            item.Summary,
            item.PromptText,
            item.ExpectedArtifact,
            ReadArgument(item.DirectToolRequest, "path"),
            ReadArgument(item.DirectToolRequest, "project_path"),
            ReadArgument(item.DirectToolRequest, "reference_path"),
            ReadArgument(item.DirectToolRequest, "solution_path"),
            ReadArgument(item.DirectToolRequest, "output_path"),
            ReadArgument(item.DirectToolRequest, "project_name")));
        if (string.IsNullOrWhiteSpace(text))
            return;

        var siblingScaffold = LooksLikePlainSiblingProjectScaffold(text);
        var siblingReference = LooksLikePlainSiblingProjectReference(text);
        if (!siblingScaffold && !siblingReference)
            return;

        var operation = NormalizeValue(item.OperationKind);
        if (operation == "create_core_library")
            item.OperationKind = "create_project";
        else if (operation == "attach_core_library")
            item.OperationKind = "add_project_to_solution";
        else if (operation == "add_domain_reference")
            item.OperationKind = "add_project_reference";

        item.WorkFamily = "solution_scaffold";
        item.PhraseFamily = "solution_scaffold";
        item.TargetStack = FirstMeaningful(item.TargetStack, "dotnet_desktop");

        var preferredTemplateId = NormalizeValue(item.OperationKind) is "add_project_to_solution" or "add_project_reference"
            || item.DirectToolRequest?.ToolName is "add_project_to_solution" or "add_dotnet_project_reference"
                ? "dotnet.project_attach.v1"
                : "dotnet.solution_scaffold.v1";
        item.TemplateId = preferredTemplateId;
        item.TemplateCandidateIds = item.TemplateCandidateIds
            .Where(id => !string.Equals(FirstMeaningful(id), "dotnet.domain_contracts_scaffold.v1", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(FirstMeaningful(id), "dotnet.repository_scaffold.v1", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!item.TemplateCandidateIds.Any(id => string.Equals(FirstMeaningful(id), preferredTemplateId, StringComparison.OrdinalIgnoreCase)))
            item.TemplateCandidateIds.Add(preferredTemplateId);
    }

    private static bool LooksLikePlainSiblingProjectScaffold(string combinedHint)
    {
        var text = NormalizeValue(combinedHint);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (ContainsAny(text, "check-runner", "check runner", "dotnet_test", "run test project", "test validation"))
            return false;

        var hasSiblingProjectIdentity = ContainsAny(
            text,
            ".core",
            ".storage",
            ".services",
            ".tests",
            ".contracts",
            ".repository",
            ".api",
            ".worker",
            ".console");
        if (!hasSiblingProjectIdentity)
            return false;

        return ContainsAny(
            text,
            "create dotnet project",
            "create class library",
            "create classlib project",
            "create wpf project",
            "create console project",
            "create console app",
            "create worker project",
            "create worker service",
            "create web api",
            "create webapi",
            "create xunit project",
            "create test project",
            "add project ",
            "add test project ",
            " to solution ",
            " into solution ");
    }

    private static bool LooksLikePlainSiblingProjectReference(string combinedHint)
    {
        var text = NormalizeValue(combinedHint);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasSiblingProjectIdentity = ContainsAny(
            text,
            ".core",
            ".storage",
            ".services",
            ".tests",
            ".contracts",
            ".repository",
            ".api",
            ".worker",
            ".console");
        if (!hasSiblingProjectIdentity)
            return false;

        return ContainsAny(text, "add reference from", "add dotnet project reference", "add project reference");
    }

    private static string NormalizeValue(string? value)
    {
        return (value ?? "").Trim();
    }

    private static void NormalizeDomainContractsRouting(TaskboardWorkItemRunStateRecord item)
    {
        if (LooksLikePlainSiblingProjectScaffold(BuildSiblingRoutingHint(item))
            || LooksLikePlainSiblingProjectReference(BuildSiblingRoutingHint(item)))
        {
            return;
        }

        if (!LooksLikeDomainContractsRouting(item))
            return;

        var operation = NormalizeValue(item.OperationKind);
        if (operation == "create_project")
            item.OperationKind = "create_core_library";
        else if (operation == "add_project_to_solution")
            item.OperationKind = "attach_core_library";
        else if (operation == "add_project_reference")
            item.OperationKind = "add_domain_reference";

        item.WorkFamily = "core_domain_models_contracts";
        item.PhraseFamily = "core_domain_models_contracts";
        item.TargetStack = FirstMeaningful(item.TargetStack, "dotnet_desktop");
        item.TemplateId = "dotnet.domain_contracts_scaffold.v1";
        item.TemplateCandidateIds = item.TemplateCandidateIds
            .Where(id => !string.Equals(FirstMeaningful(id), "dotnet.repository_scaffold.v1", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!item.TemplateCandidateIds.Any(id => string.Equals(FirstMeaningful(id), "dotnet.domain_contracts_scaffold.v1", StringComparison.OrdinalIgnoreCase)))
            item.TemplateCandidateIds.Add("dotnet.domain_contracts_scaffold.v1");
    }

    private static bool LooksLikeDomainContractsRouting(TaskboardWorkItemRunStateRecord item)
    {
        var operation = NormalizeValue(item.OperationKind);
        if (operation is "create_core_library" or "attach_core_library" or "add_domain_reference" or "make_contracts_dir" or "make_models_dir" or "write_contract_file" or "write_domain_model_file")
            return true;

        if (string.Equals(NormalizeValue(item.PhraseFamily), "core_domain_models_contracts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeValue(item.TemplateId), "dotnet.domain_contracts_scaffold.v1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var text = BuildSiblingRoutingHint(item);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (LooksLikePlainSiblingProjectScaffold(text) || LooksLikePlainSiblingProjectReference(text))
            return false;

        if (ContainsAny(text, "repository", "snapshot", "sqlite", "storage", "settingsstore", "isnapshotrepository"))
            return false;

        return ContainsAny(text, ".core", "core", "contracts", "models", "checkdefinition", "findingrecord");
    }

    private static string BuildSiblingRoutingHint(TaskboardWorkItemRunStateRecord item)
    {
        return NormalizeValue(string.Join(" ",
            item.Title,
            item.Summary,
            item.PromptText,
            item.ExpectedArtifact,
            ReadArgument(item.DirectToolRequest, "path"),
            ReadArgument(item.DirectToolRequest, "project_path"),
            ReadArgument(item.DirectToolRequest, "reference_path"),
            ReadArgument(item.DirectToolRequest, "solution_path"),
            ReadArgument(item.DirectToolRequest, "output_path"),
            ReadArgument(item.DirectToolRequest, "project_name")));
    }

    private static void NormalizeTestSupportWriteRouting(TaskboardWorkItemRunStateRecord item)
    {
        var operation = FirstMeaningful(item.OperationKind);
        if (!string.Equals(operation, "write_check_registry", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(operation, "write_snapshot_builder", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(operation, "write_findings_normalizer", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        item.WorkFamily = "findings_pipeline";

        if (IsMissingOrUnknown(item.PhraseFamily)
            || string.Equals(FirstMeaningful(item.PhraseFamily), "check_runner", StringComparison.OrdinalIgnoreCase))
        {
            item.PhraseFamily = "findings_pipeline";
        }

        if (IsMissingOrUnknown(item.TemplateId)
            || string.Equals(FirstMeaningful(item.TemplateId), "dotnet.check_runner_scaffold.v1", StringComparison.OrdinalIgnoreCase))
        {
            item.TemplateId = "dotnet.findings_pipeline_bootstrap.v1";
        }

        if (item.TemplateCandidateIds.Count > 0)
        {
            item.TemplateCandidateIds = item.TemplateCandidateIds
                .Where(id => !string.Equals(FirstMeaningful(id), "dotnet.check_runner_scaffold.v1", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (!item.TemplateCandidateIds.Any(id => string.Equals(FirstMeaningful(id), "dotnet.findings_pipeline_bootstrap.v1", StringComparison.OrdinalIgnoreCase)))
        {
            item.TemplateCandidateIds.Add("dotnet.findings_pipeline_bootstrap.v1");
        }
    }

    private static string FormatStackFamily(TaskboardStackFamily stackFamily)
    {
        return stackFamily switch
        {
            TaskboardStackFamily.DotnetDesktop => "dotnet_desktop",
            TaskboardStackFamily.NativeCppDesktop => "native_cpp_desktop",
            TaskboardStackFamily.WebApp => "web_app",
            TaskboardStackFamily.RustApp => "rust_app",
            _ => "unknown"
        };
    }

    private static string ResolveBlockerOrigin(TaskboardPlanRunStateRecord runState)
    {
        return string.Equals(runState.RuntimeStateStatusCode, "rebuilt_from_stale_snapshot", StringComparison.OrdinalIgnoreCase)
            ? "recomputed_runtime_blocker"
            : "fresh_runtime_blocker";
    }

    private sealed class RunnableSelectionResult
    {
        public bool Success { get; init; }
        public string StatusCode { get; init; } = "";
        public string Message { get; init; } = "";
        public TaskboardBatchRunStateRecord Batch { get; init; } = new();
        public TaskboardWorkItemRunStateRecord WorkItem { get; init; } = new();

        public static RunnableSelectionResult Succeed(TaskboardBatchRunStateRecord batch, TaskboardWorkItemRunStateRecord workItem)
        {
            return new RunnableSelectionResult
            {
                Success = true,
                Batch = batch,
                WorkItem = workItem
            };
        }

        public static RunnableSelectionResult Fail(string statusCode, string message)
        {
            return new RunnableSelectionResult
            {
                Success = false,
                StatusCode = statusCode,
                Message = message
            };
        }
    }

    private sealed class TaskboardRepairContinuationDecision
    {
        public static readonly TaskboardRepairContinuationDecision None = new();

        public bool ShouldContinueCurrentItem { get; init; }
        public ToolRequest? NextToolRequest { get; init; }
        public TaskboardExecutionOutcome? OverrideOutcome { get; init; }
        public string PhaseCode { get; init; } = "";
        public string PhaseText { get; init; } = "";
        public string EventKind { get; init; } = "";
        public string Summary { get; init; } = "";
    }

    private sealed class TaskboardRepairContinuationEvidence
    {
        public ArtifactRecord? VerificationArtifact { get; init; }
        public VerificationOutcomeRecord? Verification { get; init; }
        public DateTime VerificationUtc { get; init; }
        public ArtifactRecord? ApplyArtifact { get; init; }
        public PatchApplyResultRecord? ApplyResult { get; init; }
        public DateTime ApplyUtc { get; init; }
        public ArtifactRecord? DraftArtifact { get; init; }
        public PatchDraftRecord? Draft { get; init; }
        public DateTime DraftUtc { get; init; }
        public bool HasVerifiedMutationProof { get; init; }
        public bool IsReconciliationOnly { get; init; }
        public bool HasAny => Verification is not null || ApplyResult is not null || Draft is not null;
    }
}
