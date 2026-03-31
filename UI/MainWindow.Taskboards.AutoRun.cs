using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private async Task HandleActivateTaskboardAsync()
    {
        if (!_workspaceService.HasWorkspace())
        {
            EmitTaskboardActionMessage("Activate Selected", "Set a workspace before activating a taskboard.");
            return;
        }

        var importRecord = GetSelectedImportRecord()
            ?? _taskboardProjection.Imports.FirstOrDefault(record => record.State == TaskboardImportState.ReadyForPromotion);
        if (importRecord is null)
        {
            EmitTaskboardActionMessage("Activate Selected", "Activate Selected blocked: no validated taskboard is ready for activation.");
            return;
        }

        var activation = _taskboardActivationService.Promote(
            _workspaceService.WorkspaceRoot,
            importRecord.ImportId,
            _ramDbService,
            allowReplaceActive: !_settings.ConfirmBeforeReplacingActivePlan,
            actionName: "Activate Selected");
        _selectedTaskboardImportId = activation.ImportRecord?.ImportId ?? importRecord.ImportId;
        RefreshTaskboardUi();
        EmitTaskboardActionMessage(activation.ActionName, activation.Message);

        if (activation.Success)
            await TryAutoRunActivePlanAsync("Auto-run Active Plan", "", allowRetryCurrent: false);
    }

    private async Task HandleRunActivePlanAsync()
    {
        if (!_workspaceService.HasWorkspace())
        {
            EmitTaskboardActionMessage("Run Active Plan", "Set a workspace before running a taskboard plan.");
            return;
        }

        var runTarget = ResolveActivePlanRunTarget("Run Active Plan");
        if (!runTarget.Success)
        {
            EmitTaskboardActionMessage("Run Active Plan", runTarget.Message);
            return;
        }

        await TryAutoRunActivePlanAsync("Run Active Plan", "", allowRetryCurrent: true, runTarget.Context);
    }

    private async Task HandleRunSelectedBatchAsync()
    {
        if (!_workspaceService.HasWorkspace())
        {
            EmitTaskboardActionMessage("Run Selected Batch", "Set a workspace before running a taskboard batch.");
            return;
        }

        var selectedBatchId = FirstTaskboardNonEmpty(
            _selectedTaskboardBatchId,
            GetSelectedBatchProjection()?.BatchId,
            _taskboardProjection.SelectedBatch?.BatchId);
        if (string.IsNullOrWhiteSpace(selectedBatchId))
        {
            EmitTaskboardActionMessage("Run Selected Batch", "Run Selected Batch blocked: no active batch is selected.");
            return;
        }

        var activeImport = _taskboardProjection.ActiveImport;
        if (activeImport is null)
        {
            EmitTaskboardActionMessage("Run Selected Batch", "Run Selected Batch blocked: no active plan is available.");
            return;
        }

        var context = new TaskboardLiveRunEntryContext
        {
            ActionName = "Run Selected Batch",
            EntryPath = "selected_batch",
            SelectedImportId = _taskboardProjection.SelectedImport?.ImportId ?? activeImport.ImportId,
            SelectedImportTitle = _taskboardProjection.SelectedImport?.Title ?? activeImport.Title,
            SelectedImportState = FormatTaskboardState(_taskboardProjection.SelectedImport?.State ?? activeImport.State),
            ActivePlanImportId = activeImport.ImportId,
            ActivePlanTitle = activeImport.Title,
            SelectedBatchId = selectedBatchId,
            Message = $"Run Selected Batch is executing active plan `{activeImport.Title}` for batch `{selectedBatchId}`."
        };

        await TryAutoRunActivePlanAsync("Run Selected Batch", selectedBatchId, allowRetryCurrent: true, context);
    }

    private async Task TryAutoRunActivePlanAsync(string actionName, string selectedBatchId, bool allowRetryCurrent, TaskboardLiveRunEntryContext? runEntryContext = null)
    {
        if (_taskboardAutoRunInProgress)
        {
            EmitTaskboardActionMessage(actionName, $"{actionName} skipped: a taskboard auto-run is already in progress.");
            return;
        }

        if (!_workspaceService.HasWorkspace())
        {
            EmitTaskboardActionMessage(actionName, $"{actionName} blocked: no workspace is set.");
            return;
        }

        if (_taskboardProjection.ActiveImport is null || _taskboardProjection.ActiveDocument is null)
        {
            EmitTaskboardActionMessage(actionName, $"{actionName} blocked: no active validated taskboard plan is available.");
            return;
        }

        var effectiveRunEntryContext = runEntryContext ?? new TaskboardLiveRunEntryContext
        {
            ActionName = actionName,
            EntryPath = string.IsNullOrWhiteSpace(selectedBatchId) ? "active_plan" : "selected_batch",
            SelectedImportId = _taskboardProjection.SelectedImport?.ImportId ?? _taskboardProjection.ActiveImport.ImportId,
            SelectedImportTitle = _taskboardProjection.SelectedImport?.Title ?? _taskboardProjection.ActiveImport.Title,
            SelectedImportState = FormatTaskboardState(_taskboardProjection.SelectedImport?.State ?? _taskboardProjection.ActiveImport.State),
            ActivePlanImportId = _taskboardProjection.ActiveImport.ImportId,
            ActivePlanTitle = _taskboardProjection.ActiveImport.Title,
            SelectedBatchId = selectedBatchId,
            Message = string.IsNullOrWhiteSpace(selectedBatchId)
                ? $"Run Active Plan is executing active plan `{_taskboardProjection.ActiveImport.Title}`."
                : $"Run Selected Batch is executing batch `{selectedBatchId}` from active plan `{_taskboardProjection.ActiveImport.Title}`."
        };
        var uiExecutionContext = CaptureUiExecutionContextSnapshot(nameof(TryAutoRunActivePlanAsync));
        var workspaceRoot = _workspaceService.WorkspaceRoot;
        var activeImport = _taskboardProjection.ActiveImport;
        var activeDocument = _taskboardProjection.ActiveDocument;
        var selectedModel = uiExecutionContext.SelectedModel;
        var endpoint = uiExecutionContext.Endpoint;
        var activeTargetRelativePath = uiExecutionContext.ActiveTargetRelativePath;

        _taskboardArtifactStore.SaveLiveRunEntryArtifact(
            _ramDbService,
            workspaceRoot,
            new TaskboardLiveRunEntryRecord
            {
                EntryId = Guid.NewGuid().ToString("N"),
                WorkspaceRoot = workspaceRoot,
                ActionName = effectiveRunEntryContext.ActionName,
                EntryPath = effectiveRunEntryContext.EntryPath,
                SelectedImportId = effectiveRunEntryContext.SelectedImportId,
                SelectedImportTitle = effectiveRunEntryContext.SelectedImportTitle,
                SelectedImportState = effectiveRunEntryContext.SelectedImportState,
                ActivePlanImportId = effectiveRunEntryContext.ActivePlanImportId,
                ActivePlanTitle = effectiveRunEntryContext.ActivePlanTitle,
                SelectedBatchId = effectiveRunEntryContext.SelectedBatchId,
                ActivationHandoffPerformed = effectiveRunEntryContext.ActivationHandoffPerformed,
                ActivationHandoffSummary = effectiveRunEntryContext.ActivationHandoffSummary,
                Message = effectiveRunEntryContext.Message,
                CreatedUtc = DateTime.UtcNow.ToString("O")
            });

        try
        {
            _taskboardAutoRunInProgress = true;
            _lastPublishedTaskboardTerminalSummaryFingerprint = "";
            _lastRenderedTaskboardOperatorSummaryFingerprint = "";
            _lastRenderedTaskboardOperatorSummaryText = "";
            _lastRenderedTaskboardOperatorSummaryModeLabel = "";
            SetBusy(true);
            ResetTaskboardLiveRunFeed();
            RefreshTaskboardUi();

            AppendOutput($"{actionName}: {effectiveRunEntryContext.Message}");
            if (!string.IsNullOrWhiteSpace(selectedBatchId))
                AppendOutput($"{actionName}: scoped to batch `{selectedBatchId}`.");

            var result = await Task.Run(async () => await _taskboardAutoRunService.RunAsync(
                workspaceRoot,
                activeImport!,
                activeDocument!,
                actionName,
                selectedBatchId,
                activeTargetRelativePath,
                _ramDbService,
                (bridge, observer) => ExecuteTaskboardBridgeAsync(bridge, uiExecutionContext, observer),
                allowRetryCurrent,
                _settings,
                endpoint,
                selectedModel,
                effectiveRunEntryContext,
                CancellationToken.None,
                PublishTaskboardLiveProgressAsync));

            RefreshTaskboardUi();
            TaskboardDetailsTextBox.Text = BuildTaskboardAutoRunDetails(result);
            await PublishTaskboardRunOutcomeAsync(actionName, result);
        }
        catch (Exception ex)
        {
            EmitTaskboardActionMessage(actionName, $"{actionName} failed: {ex.Message}");
        }
        finally
        {
            _taskboardAutoRunInProgress = false;
            SetBusy(false);
            RefreshTaskboardUi();
        }
    }

    private async Task<TaskboardExecutionOutcome> ExecuteTaskboardBridgeAsync(
        TaskboardExecutionBridgeResult bridge,
        UiExecutionContextSnapshot uiExecutionContext,
        Func<TaskboardLiveProgressUpdate, Task>? liveProgressObserver = null)
    {
        if (bridge.ToolRequest is null)
        {
            return new TaskboardExecutionOutcome
            {
                ResultKind = TaskboardWorkItemResultKind.ValidationFailed,
                Summary = FirstNonEmpty(bridge.Reason, "Taskboard execution bridge produced no tool request."),
                ResultClassification = "validation_failed"
            };
        }

        AppendOutput(
            $"Taskboard execution bridge: prompt=`{bridge.PromptText}` goal={FormatTaskboardExecutionGoalKind(bridge.ExecutionGoalResolution.GoalKind)} tool={bridge.ToolRequest.ToolName} chain={DisplayTaskboardValue(bridge.ExecutionGoalResolution.Goal.SelectedChainTemplateId)} mode={FormatResponseMode(bridge.ResponseMode)} eligibility={FormatTaskboardExecutionEligibility(bridge.Eligibility)} target={DisplayTaskboardValue(bridge.ResolvedTargetPath)}.");
        if (liveProgressObserver is not null)
        {
            await liveProgressObserver(new TaskboardLiveProgressUpdate
            {
                PhaseCode = "starting_chain",
                PhaseText = "Starting deterministic tool or chain execution.",
                ActivitySummary = $"Starting {bridge.ToolRequest.ToolName}.",
                EventKind = "execution_started",
                ToolName = bridge.ToolRequest.ToolName,
                ChainTemplateId = bridge.ExecutionGoalResolution.Goal.SelectedChainTemplateId
            });
        }

        var chainRun = await ExecuteControlledToolChainAsync(
            bridge.ToolRequest,
            "Taskboard auto-run",
            bridge.PromptText,
            allowModelSummary: false,
            bridge.ResponseMode,
            liveProgressObserver,
            uiExecutionContext);

        return BuildTaskboardExecutionOutcome(chainRun);
    }

    private TaskboardExecutionOutcome BuildTaskboardExecutionOutcome(ToolChainRunResult chainRun)
    {
        var lastResult = chainRun.LastResult;
        if (chainRun.Chain.StopReason is ToolChainStopReason.PolicyBlockedNextStep or ToolChainStopReason.NoFurtherStepAllowed or ToolChainStopReason.InvalidModelStep)
        {
            return new TaskboardExecutionOutcome
            {
                ResultKind = TaskboardWorkItemResultKind.Blocked,
                ResultClassification = chainRun.Chain.StopReason.ToString().ToLowerInvariant(),
                Summary = FirstNonEmpty(chainRun.Chain.FinalOutcomeSummary, chainRun.Summary, lastResult?.Summary, lastResult?.ErrorMessage, "The controlled chain stopped before the taskboard work item completed."),
                ExecutionAttempted = chainRun.Chain.Steps.Any(step => step.ExecutionAttempted),
                ExecutedToolCalls = BuildExecutedToolCalls(chainRun)
            };
        }

        if (lastResult is null)
        {
            return new TaskboardExecutionOutcome
            {
                ResultKind = TaskboardWorkItemResultKind.ValidationFailed,
                ResultClassification = "validation_failed",
                Summary = FirstNonEmpty(chainRun.Chain.FinalOutcomeSummary, chainRun.Summary, "No tool result was recorded for the taskboard work item."),
                ExecutedToolCalls = BuildExecutedToolCalls(chainRun)
            };
        }

        var summary = FirstNonEmpty(lastResult.Summary, lastResult.ErrorMessage, chainRun.Chain.FinalOutcomeSummary, chainRun.Summary);
        if (!lastResult.Success)
        {
            var resultKind = chainRun.Chain.StopReason switch
            {
                ToolChainStopReason.ManualOnly => TaskboardWorkItemResultKind.ManualOnly,
                ToolChainStopReason.ScopeBlocked or ToolChainStopReason.SafetyBlocked or ToolChainStopReason.PolicyBlockedNextStep or ToolChainStopReason.NoFurtherStepAllowed or ToolChainStopReason.InvalidModelStep => TaskboardWorkItemResultKind.Blocked,
                _ => TaskboardWorkItemResultKind.Failed
            };

            return new TaskboardExecutionOutcome
            {
                ResultKind = resultKind,
                ResultClassification = lastResult.OutcomeType,
                Summary = summary,
                ExecutionAttempted = chainRun.Chain.Steps.Any(step => step.ExecutionAttempted),
                ExecutedToolCalls = BuildExecutedToolCalls(chainRun)
            };
        }

        return new TaskboardExecutionOutcome
        {
            ResultKind = TaskboardWorkItemResultKind.Passed,
            ResultClassification = FirstNonEmpty(lastResult.OutcomeType, "passed"),
            Summary = summary,
            ExecutionAttempted = chainRun.Chain.Steps.Any(step => step.ExecutionAttempted),
            ExecutedToolCalls = BuildExecutedToolCalls(chainRun)
        };
    }

    private static List<TaskboardExecutedToolCallRecord> BuildExecutedToolCalls(ToolChainRunResult chainRun)
    {
        return chainRun.Chain.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.ToolName))
            .Select(step => new TaskboardExecutedToolCallRecord
            {
                ToolName = step.ToolName,
                ChainTemplateId = step.TemplateName,
                Stage = ClassifyExecutedToolStage(step),
                ResultClassification = step.ResultClassification,
                Summary = FirstNonEmpty(step.ResultSummary, step.ExecutionBlockedReason),
                MutationObserved = step.MutationObserved,
                TouchedFilePaths = [.. step.TouchedFilePaths],
                StructuredDataJson = step.StructuredDataJson ?? "",
                CreatedUtc = FirstNonEmpty(step.CreatedUtc, DateTime.UtcNow.ToString("O"))
            })
            .ToList();
    }

    private static string ClassifyExecutedToolStage(ToolChainStepRecord step)
    {
        if (!step.AllowedByPolicy)
            return "blocked";

        if (!step.ExecutionAttempted)
            return string.IsNullOrWhiteSpace(step.ExecutionBlockedReason) ? "planned" : "blocked";

        return string.IsNullOrWhiteSpace(step.ResultClassification) switch
        {
            true => "completed",
            false when string.Equals(step.ResultClassification, "manual_only", StringComparison.OrdinalIgnoreCase) => "blocked",
            false when step.ResultClassification.Contains("blocked", StringComparison.OrdinalIgnoreCase) => "blocked",
            false when step.ResultClassification.Contains("failed", StringComparison.OrdinalIgnoreCase) => "failed",
            false when step.ResultClassification.Contains("error", StringComparison.OrdinalIgnoreCase) => "failed",
            _ => "completed"
        };
    }

    private string BuildTaskboardAutoRunDetails(TaskboardAutoRunResult result)
    {
        var lines = new List<string>
        {
            result.Message
        };

        if (result.Projection is not null)
        {
            lines.Add("");
            lines.Add(result.Projection.Message);
        }

        if (_taskboardProjection.RunState is not null)
        {
            lines.Add("");
            lines.Add(_taskboardProjection.RuntimeStatusBanner);
            lines.Add(_taskboardProjection.RuntimeFreshnessBanner);
            lines.Add(_taskboardProjection.RuntimeEntryBanner);
            lines.Add(_taskboardProjection.RuntimeActivationHandoffBanner);
            lines.Add(_taskboardProjection.RuntimePhaseBanner);
            lines.Add(_taskboardProjection.RuntimeCurrentStepBanner);
            lines.Add(_taskboardProjection.RuntimeLatestStepBanner);
            lines.Add(_taskboardProjection.RuntimeLastCompletedStepBanner);
            lines.Add(_taskboardProjection.RuntimeNextStepBanner);
            lines.Add(_taskboardProjection.RuntimeProgressBanner);
            lines.Add(_taskboardProjection.RuntimeLastResultBanner);
            lines.Add(_taskboardProjection.RuntimeBlockerOriginBanner);
            lines.Add(_taskboardProjection.RuntimeSummaryBanner);
            lines.Add(ResolveEffectiveTaskboardRuntimeSummaryModeLabel());
            lines.Add(ResolveEffectiveTaskboardRuntimeSummaryText());
            lines.Add("Raw summary / debug packet:");
            lines.Add(_taskboardProjection.RuntimeRawSummaryText);
            lines.Add(_taskboardProjection.RuntimeBuildProfileBanner);
            lines.Add(_taskboardProjection.RuntimeDecompositionBanner);
            lines.Add(_taskboardProjection.RuntimeExecutionWiringBanner);
            lines.Add(_taskboardProjection.RuntimeChainDepthBanner);
            lines.Add(_taskboardProjection.RuntimeExecutionTraceText);
            lines.Add(_taskboardProjection.RuntimeMutationProofBanner);
            lines.Add(_taskboardProjection.RuntimeWorkFamilyBanner);
            lines.Add(_taskboardProjection.RuntimeCoverageBanner);
            lines.Add(_taskboardProjection.RuntimeLaneBanner);
            lines.Add(_taskboardProjection.RuntimeLaneBlockerBanner);
            lines.Add(_taskboardProjection.RuntimeGoalBanner);
            lines.Add(_taskboardProjection.RuntimeGoalBlockerBanner);
            if (!string.IsNullOrWhiteSpace(_taskboardProjection.RuntimeRecentActivityText))
            {
                lines.Add("Recent activity:");
                lines.Add(_taskboardProjection.RuntimeRecentActivityText);
            }
        }

        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string FormatTaskboardExecutionEligibility(TaskboardExecutionEligibilityKind eligibility)
    {
        return eligibility switch
        {
            TaskboardExecutionEligibilityKind.WorkspaceBuildSafe => "workspace_build_safe",
            TaskboardExecutionEligibilityKind.WorkspaceEditSafe => "workspace_edit_safe",
            TaskboardExecutionEligibilityKind.WorkspaceTestSafe => "workspace_test_safe",
            TaskboardExecutionEligibilityKind.ManualOnlyElevated => "manual_only_elevated",
            TaskboardExecutionEligibilityKind.ManualOnlySystemMutation => "manual_only_system_mutation",
            TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous => "manual_only_ambiguous",
            TaskboardExecutionEligibilityKind.BlockedUnsafe => "blocked_unsafe",
            _ => "unknown"
        };
    }

    private static string DisplayTaskboardValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static string FormatTaskboardExecutionGoalKind(TaskboardExecutionGoalKind goalKind)
    {
        return goalKind switch
        {
            TaskboardExecutionGoalKind.ToolGoal => "tool_goal",
            TaskboardExecutionGoalKind.ChainGoal => "chain_goal",
            TaskboardExecutionGoalKind.ManualOnlyGoal => "manual_only_goal",
            TaskboardExecutionGoalKind.BlockedGoal => "blocked_goal",
            _ => "unknown"
        };
    }

    private TaskboardRunTargetResolution ResolveActivePlanRunTarget(string actionName)
    {
        var selectedImport = GetSelectedImportRecord() ?? _taskboardProjection.SelectedImport;
        var activeImport = _taskboardProjection.ActiveImport;
        if (activeImport is not null && _taskboardProjection.ActiveDocument is not null)
        {
            return TaskboardRunTargetResolution.Succeed(new TaskboardLiveRunEntryContext
            {
                ActionName = actionName,
                EntryPath = "active_plan",
                SelectedImportId = selectedImport?.ImportId ?? activeImport.ImportId,
                SelectedImportTitle = selectedImport?.Title ?? activeImport.Title,
                SelectedImportState = FormatTaskboardState(selectedImport?.State ?? activeImport.State),
                ActivePlanImportId = activeImport.ImportId,
                ActivePlanTitle = activeImport.Title,
                Message = selectedImport is not null
                    && !string.Equals(selectedImport.ImportId, activeImport.ImportId, StringComparison.OrdinalIgnoreCase)
                    ? $"Run Active Plan is executing active plan `{activeImport.Title}`. Selected import `{selectedImport.Title}` is not active."
                    : $"Run Active Plan is executing active plan `{activeImport.Title}`."
            });
        }

        if (selectedImport is null)
        {
            return TaskboardRunTargetResolution.Fail($"{actionName} blocked: no active plan is available, and no promotable taskboard is selected.");
        }

        if (selectedImport.State is not TaskboardImportState.ReadyForPromotion and not TaskboardImportState.Validated)
        {
            return TaskboardRunTargetResolution.Fail($"{actionName} blocked: selected taskboard `{selectedImport.Title}` is in state {FormatTaskboardState(selectedImport.State)} and cannot be activated-and-run.");
        }

        var activation = _taskboardActivationService.Promote(
            _workspaceService.WorkspaceRoot,
            selectedImport.ImportId,
            _ramDbService,
            allowReplaceActive: !_settings.ConfirmBeforeReplacingActivePlan,
            actionName: "Activate-and-Run Selected");
        _taskboardArtifactStore.SaveActivationHandoffArtifact(
            _ramDbService,
            _workspaceService.WorkspaceRoot,
            new TaskboardActivationHandoffRecord
            {
                HandoffId = Guid.NewGuid().ToString("N"),
                WorkspaceRoot = _workspaceService.WorkspaceRoot,
                ActionName = actionName,
                SourceImportId = selectedImport.ImportId,
                SourceImportTitle = selectedImport.Title,
                SourceState = FormatTaskboardState(selectedImport.State),
                Success = activation.Success,
                WasSkipped = activation.WasSkipped,
                StatusCode = activation.StatusCode,
                Message = activation.Message,
                ActivatedImportId = activation.ImportRecord?.ImportId ?? "",
                CreatedUtc = DateTime.UtcNow.ToString("O")
            });

        _selectedTaskboardImportId = activation.ImportRecord?.ImportId ?? selectedImport.ImportId;
        RefreshTaskboardUi();

        if (!activation.Success)
            return TaskboardRunTargetResolution.Fail(activation.Message);

        var activatedImport = _taskboardProjection.ActiveImport;
        if (activatedImport is null)
            return TaskboardRunTargetResolution.Fail($"{actionName} blocked: activation succeeded, but no active plan is available after the handoff.");

        return TaskboardRunTargetResolution.Succeed(new TaskboardLiveRunEntryContext
        {
            ActionName = actionName,
            EntryPath = "activate_then_run",
            SelectedImportId = selectedImport.ImportId,
            SelectedImportTitle = selectedImport.Title,
            SelectedImportState = FormatTaskboardState(selectedImport.State),
            ActivePlanImportId = activatedImport.ImportId,
            ActivePlanTitle = activatedImport.Title,
            ActivationHandoffPerformed = true,
            ActivationHandoffSummary = activation.Message,
            Message = $"Run Active Plan activated `{activatedImport.Title}` first, then started the active-plan run path."
        });
    }

    private sealed class TaskboardRunTargetResolution
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public TaskboardLiveRunEntryContext Context { get; init; } = new();

        public static TaskboardRunTargetResolution Succeed(TaskboardLiveRunEntryContext context)
        {
            return new TaskboardRunTargetResolution
            {
                Success = true,
                Context = context,
                Message = context.Message
            };
        }

        public static TaskboardRunTargetResolution Fail(string message)
        {
            return new TaskboardRunTargetResolution
            {
                Success = false,
                Message = message
            };
        }
    }
}
