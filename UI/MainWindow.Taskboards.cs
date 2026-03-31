using System.Collections.ObjectModel;
using System.Globalization;
using RAM.Models;
using RAM.Services;

namespace RAM;

public partial class MainWindow
{
    private readonly TaskboardActivationService _taskboardActivationService = new();
    private readonly TaskboardActionMessageService _taskboardActionMessageService = new();
    private readonly TaskboardArtifactStore _taskboardArtifactStore = new();
    private readonly TaskboardAutoRunService _taskboardAutoRunService;
    private readonly TaskboardCleanupService _taskboardCleanupService = new();
    private readonly TaskboardImportService _taskboardImportService = new();
    private readonly TaskboardOperatorSummaryService _taskboardOperatorSummaryService;
    private readonly TaskboardProjectionService _taskboardProjectionService = new();
    private readonly TaskboardRunSummaryService _taskboardRunSummaryService = new();
    private readonly TaskboardRunProjectionService _taskboardRunProjectionService = new();
    private readonly ObservableCollection<TaskboardImportRecord> _taskboardImports = [];
    private readonly ObservableCollection<TaskboardBatchProjection> _taskboardBatches = [];

    private TaskboardProjection _taskboardProjection = new();
    private string _selectedTaskboardImportId = "";
    private string _selectedTaskboardBatchId = "";
    private string _lastTaskboardActionMessage = "Last action: (none)";
    private string _lastPublishedTaskboardTerminalSummaryFingerprint = "";
    private string _lastRenderedTaskboardOperatorSummaryFingerprint = "";
    private string _lastRenderedTaskboardOperatorSummaryText = "";
    private string _lastRenderedTaskboardOperatorSummaryModeLabel = "";
    private bool _taskboardAutoRunInProgress;
    private bool _taskboardBatchAutoFollowEnabled = true;
    private bool _taskboardDetailsAutoFollowEnabled = true;
    private bool _taskboardBatchSelectionUpdateInProgress;

    private void InitializeTaskboardUi()
    {
        TaskboardImportsListBox.ItemsSource = _taskboardImports;
        TaskboardBatchesListBox.ItemsSource = _taskboardBatches;
        RefreshTaskboardUi();
    }

    private async Task<bool> TryHandleTaskboardImportAsync(string prompt)
    {
        if (!_taskboardImportService.LooksLikeStructuredDocument(prompt))
            return false;

        if (!_workspaceService.HasWorkspace())
        {
            const string message = "Set a workspace before importing a structured taskboard.";
            AddMessage("assistant", message);
            AppendOutput(message);
            return true;
        }

        var result = _taskboardImportService.ImportFromText(
            _workspaceService.WorkspaceRoot,
            prompt,
            "paste",
            _ramDbService);
        AppendPendingDatabaseMessages();

        if (_settings.AutoActivateValidatedTaskboardWhenNoActivePlan
            && result.ImportRecord?.State == TaskboardImportState.ReadyForPromotion)
        {
            result.AutoActivationResult = _taskboardActivationService.TryAutoPromoteImportedTaskboard(
                _workspaceService.WorkspaceRoot,
                result.ImportRecord,
                _ramDbService);
            if (result.AutoActivationResult.Success && result.AutoActivationResult.ImportRecord is not null)
                result.ImportRecord = result.AutoActivationResult.ImportRecord;
        }

        if (result.AutoActivationResult is not null)
            _lastTaskboardActionMessage = $"Last action: {result.AutoActivationResult.Message}";

        _selectedTaskboardImportId = result.AutoActivationResult?.ImportRecord?.ImportId
            ?? result.ImportRecord?.ImportId
            ?? _selectedTaskboardImportId;
        RefreshTaskboardUi();

        var assistantMessage = BuildTaskboardIntakeAssistantMessage(result);
        AddMessage("assistant", assistantMessage);
        AppendOutput("Taskboard intake:" + Environment.NewLine + assistantMessage);

        if (result.AutoActivationResult?.Success == true)
            await TryAutoRunActivePlanAsync("Auto-run Active Plan", "", allowRetryCurrent: false);

        await Task.CompletedTask;
        return true;
    }

    private async Task<bool> TryHandleTaskboardPromptAsync(string prompt)
    {
        var normalized = NormalizeTaskboardPrompt(prompt);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (normalized.Contains("promote latest taskboard", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("promote taskboard", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("activate taskboard", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("activate selected", StringComparison.OrdinalIgnoreCase))
        {
            await HandleActivateTaskboardAsync();
            return true;
        }

        if (normalized.Contains("archive selected taskboard", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("archive selected plan", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("archive active taskboard", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("archive active plan", StringComparison.OrdinalIgnoreCase))
        {
            HandleArchiveSelectedTaskboard();
            return true;
        }

        if (normalized.Contains("delete selected taskboard", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("delete selected plan", StringComparison.OrdinalIgnoreCase))
        {
            HandleDeleteSelectedTaskboard();
            return true;
        }

        if (normalized.Contains("clear rejected", StringComparison.OrdinalIgnoreCase))
        {
            HandleClearRejectedTaskboards();
            return true;
        }

        if (normalized.Contains("clear inactive", StringComparison.OrdinalIgnoreCase))
        {
            HandleClearInactiveTaskboards();
            return true;
        }

        if (normalized.Contains("run active plan", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("run next ready batch", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRunActivePlanAsync();
            return true;
        }

        if (normalized.Contains("run selected batch", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("generate next work item", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRunSelectedBatchAsync();
            return true;
        }

        if (normalized.Contains("show active plan", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("show active taskboard", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("summarize active plan", StringComparison.OrdinalIgnoreCase))
        {
            var message = _taskboardProjectionService.BuildActivePlanSummary(_taskboardProjection);
            AddMessage("assistant", message);
            AppendOutput(message);
            return true;
        }

        if (normalized.Contains("summarize active batch", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("show active batch", StringComparison.OrdinalIgnoreCase))
        {
            var batch = GetSelectedBatchProjection() ?? _taskboardProjection.Batches.FirstOrDefault();
            var message = batch is null ? "No active parsed taskboard batch is available." : batch.DetailsText;
            AddMessage("assistant", message);
            AppendOutput(message);
            return true;
        }

        if (normalized.Contains("next-ready batch items", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("next ready batch items", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("next batch items", StringComparison.OrdinalIgnoreCase))
        {
            var message = _taskboardProjectionService.BuildNextBatchItemsSummary(_taskboardProjection);
            AddMessage("assistant", message);
            AppendOutput(message);
            return true;
        }

        if (normalized.Contains("validation blocker", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("taskboard validation", StringComparison.OrdinalIgnoreCase))
        {
            var importRecord = GetSelectedImportRecord() ?? _taskboardProjection.ActiveImport ?? _taskboardProjection.Imports.FirstOrDefault();
            var validation = importRecord is null || !_workspaceService.HasWorkspace()
                ? null
                : _taskboardArtifactStore.LoadValidation(_ramDbService, _workspaceService.WorkspaceRoot, importRecord);
            var message = _taskboardProjectionService.BuildValidationSummary(importRecord, validation);
            AddMessage("assistant", message);
            AppendOutput(message);
            return true;
        }

        if (normalized.Contains("what can ram do next", StringComparison.OrdinalIgnoreCase))
        {
            var message = _taskboardProjection.ActionAvailabilityBanner;
            AddMessage("assistant", message);
            AppendOutput(message);
            return true;
        }

        return false;
    }

    private void RefreshTaskboardsButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        RefreshTaskboardUi();
    }

    private async void ActivateTaskboardButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        await HandleActivateTaskboardAsync();
    }

    private void ArchiveSelectedTaskboardButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        HandleArchiveSelectedTaskboard();
    }

    private void DeleteSelectedTaskboardButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        HandleDeleteSelectedTaskboard();
    }

    private void ClearRejectedTaskboardsButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        HandleClearRejectedTaskboards();
    }

    private void ClearInactiveTaskboardsButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        HandleClearInactiveTaskboards();
    }

    private async void RunActivePlanButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        await HandleRunActivePlanAsync();
    }

    private async void RunSelectedBatchButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        await HandleRunSelectedBatchAsync();
    }

    private void InspectTaskboardRawButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var importRecord = GetSelectedImportRecord();
        if (importRecord is null || !_workspaceService.HasWorkspace())
        {
            TaskboardDetailsTextBox.Text = "No taskboard import is selected.";
            return;
        }

        _taskboardDetailsAutoFollowEnabled = false;
        TaskboardDetailsTextBox.Text = _taskboardArtifactStore.LoadRawText(_ramDbService, _workspaceService.WorkspaceRoot, importRecord);
    }

    private void InspectTaskboardParsedButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var importRecord = GetSelectedImportRecord();
        if (importRecord is null || !_workspaceService.HasWorkspace())
        {
            TaskboardDetailsTextBox.Text = "No taskboard import is selected.";
            return;
        }

        _taskboardDetailsAutoFollowEnabled = false;
        TaskboardDetailsTextBox.Text = _taskboardArtifactStore.LoadParsedJson(_ramDbService, _workspaceService.WorkspaceRoot, importRecord);
    }

    private void InspectTaskboardValidationButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var importRecord = GetSelectedImportRecord();
        if (importRecord is null || !_workspaceService.HasWorkspace())
        {
            TaskboardDetailsTextBox.Text = "No taskboard import is selected.";
            return;
        }

        _taskboardDetailsAutoFollowEnabled = false;
        TaskboardDetailsTextBox.Text = _taskboardArtifactStore.LoadValidationJson(_ramDbService, _workspaceService.WorkspaceRoot, importRecord);
    }

    private void TaskboardImportsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedTaskboardImportId = GetSelectedImportRecord()?.ImportId ?? "";
        UpdateTaskboardStatusBlocks();
        UpdateTaskboardActionControls();
        UpdateTaskboardDetails();
    }

    private void TaskboardBatchesListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedTaskboardBatchId = GetSelectedBatchProjection()?.BatchId ?? "";
        if (!_taskboardBatchSelectionUpdateInProgress)
        {
            var authoritativeBatchId = GetAuthoritativeTaskboardBatchId();
            _taskboardBatchAutoFollowEnabled = string.IsNullOrWhiteSpace(authoritativeBatchId)
                || string.Equals(_selectedTaskboardBatchId, authoritativeBatchId, StringComparison.OrdinalIgnoreCase);
            _taskboardDetailsAutoFollowEnabled = _taskboardBatchAutoFollowEnabled;
        }

        UpdateTaskboardStatusBlocks();
        UpdateTaskboardActionControls();
        UpdateTaskboardDetails();
    }

    private void TaskboardBatchesListBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(GetAuthoritativeTaskboardBatchId()))
            _taskboardBatchAutoFollowEnabled = false;
    }

    private void TaskboardBatchesListBox_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(GetAuthoritativeTaskboardBatchId()))
            _taskboardBatchAutoFollowEnabled = false;
    }

    private void TaskboardDetailsTextBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _taskboardDetailsAutoFollowEnabled = false;
    }

    private void TaskboardDetailsTextBox_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        _taskboardDetailsAutoFollowEnabled = false;
    }

    private void TaskboardDetailsTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.PageUp:
            case System.Windows.Input.Key.PageDown:
            case System.Windows.Input.Key.Up:
            case System.Windows.Input.Key.Down:
            case System.Windows.Input.Key.Home:
            case System.Windows.Input.Key.End:
                _taskboardDetailsAutoFollowEnabled = false;
                break;
        }
    }

    private void RefreshTaskboardUi()
    {
        if (!_workspaceService.HasWorkspace())
        {
            _taskboardProjection = new TaskboardProjection();
            ReplaceItems(_taskboardImports, []);
            ReplaceItems(_taskboardBatches, []);
            TaskboardImportStatusTextBlock.Text = "Imports: 0";
            TaskboardActiveTitleTextBlock.Text = "Active plan: (none)";
            TaskboardValidationTextBlock.Text = "Validation: (none)";
            TaskboardSelectedStatusTextBlock.Text = "Selection: (none)";
            TaskboardRunTargetTextBlock.Text = "Run target: (none)";
            TaskboardActionsTextBlock.Text = "Actions: (none)";
            TaskboardRuntimeStatusTextBlock.Text = "Runtime: (none)";
            TaskboardRuntimeFreshnessTextBlock.Text = "Runtime snapshot: (none)";
            TaskboardRuntimeEntryTextBlock.Text = "Run entry: (none)";
            TaskboardRuntimeActivationHandoffTextBlock.Text = "Activation handoff: (none)";
            TaskboardRuntimePhaseTextBlock.Text = "Current phase: (none)";
            TaskboardRuntimeCurrentStepTextBlock.Text = "Current step: (none)";
            TaskboardRuntimeLatestStepTextBlock.Text = "Latest step: (none)";
            TaskboardRuntimeLastCompletedStepTextBlock.Text = "Last completed step: (none)";
            TaskboardRuntimeNextStepTextBlock.Text = "Next unresolved work: (none)";
            TaskboardRuntimeProgressTextBlock.Text = "Progress: (none)";
            TaskboardRuntimeLastResultTextBlock.Text = "Last result: (none)";
            TaskboardRuntimeBlockerOriginTextBlock.Text = "Blocker origin: (none)";
            TaskboardRuntimeSummaryModeTextBlock.Text = "Summary mode: (none)";
            TaskboardRuntimeSummaryTextBlock.Text = "A deterministic run summary will appear after the current run reaches a truthful terminal state.";
            TaskboardRuntimeBuildProfileTextBlock.Text = "Build profile: (none)";
            TaskboardRuntimeDecompositionTextBlock.Text = "Decomposition: (none)";
            TaskboardRuntimeWorkFamilyTextBlock.Text = "Work family: (none)";
            TaskboardRuntimeCoverageTextBlock.Text = "Coverage: (none)";
            TaskboardRuntimeLaneTextBlock.Text = "Execution lane: (none)";
            TaskboardRuntimeLaneBlockerTextBlock.Text = "Lane blocker: (none)";
            TaskboardRuntimeGoalTextBlock.Text = "Execution goal: (none)";
            TaskboardRuntimeGoalBlockerTextBlock.Text = "Goal blocker: (none)";
            TaskboardRecentActivityTextBox.Text = "No recent activity recorded.";
            TaskboardLastActionTextBlock.Text = _lastTaskboardActionMessage;
            TaskboardDetailsTextBox.Text = "Set a workspace to view taskboard imports.";
            UpdateTaskboardActionControls();
            return;
        }

        _taskboardProjection = _taskboardProjectionService.BuildProjection(
            _workspaceService.WorkspaceRoot,
            _ramDbService,
            _selectedTaskboardImportId,
            _selectedTaskboardBatchId,
            _settings.ShowArchivedTaskboards);
        ReplaceItems(_taskboardImports, _taskboardProjection.Imports);
        ReplaceItems(_taskboardBatches, _taskboardProjection.Batches);

        SelectTaskboardImport(_selectedTaskboardImportId);
        SelectTaskboardBatch(ResolveTaskboardBatchSelectionTarget());
        _selectedTaskboardImportId = GetSelectedImportRecord()?.ImportId ?? "";
        _selectedTaskboardBatchId = GetSelectedBatchProjection()?.BatchId ?? "";

        UpdateTaskboardStatusBlocks();
        UpdateTaskboardActionControls();
        UpdateTaskboardDetails();
    }

    private void UpdateTaskboardStatusBlocks()
    {
        var hiddenArchivedSuffix = _taskboardProjection.HiddenArchivedCount > 0
            ? $" ({_taskboardProjection.HiddenArchivedCount} archived hidden)"
            : "";
        TaskboardImportStatusTextBlock.Text = $"Imports: {_taskboardProjection.Imports.Count} visible / {_taskboardProjection.TotalImportCount} total{hiddenArchivedSuffix}";
        TaskboardActiveTitleTextBlock.Text = _taskboardProjection.ActiveImport is null
            ? "Active plan: (none)"
            : $"Active plan: {_taskboardProjection.ActiveImport.Title}";
        TaskboardValidationTextBlock.Text = $"Validation: {_taskboardProjection.ValidationBanner}";
        TaskboardSelectedStatusTextBlock.Text = $"Selection: {_taskboardProjection.SelectedStatusBanner}";
        TaskboardRunTargetTextBlock.Text = _taskboardProjection.RunTargetBanner;
        TaskboardActionsTextBlock.Text = $"Actions: {_taskboardProjection.ActionAvailabilityBanner}";
        TaskboardRuntimeStatusTextBlock.Text = _taskboardProjection.RuntimeStatusBanner;
        TaskboardRuntimeFreshnessTextBlock.Text = _taskboardProjection.RuntimeFreshnessBanner;
        TaskboardRuntimeEntryTextBlock.Text = _taskboardProjection.RuntimeEntryBanner;
        TaskboardRuntimeActivationHandoffTextBlock.Text = _taskboardProjection.RuntimeActivationHandoffBanner;
        TaskboardRuntimePhaseTextBlock.Text = _taskboardProjection.RuntimePhaseBanner;
        TaskboardRuntimeCurrentStepTextBlock.Text = _taskboardProjection.RuntimeCurrentStepBanner;
        TaskboardRuntimeLatestStepTextBlock.Text = _taskboardProjection.RuntimeLatestStepBanner;
        TaskboardRuntimeLastCompletedStepTextBlock.Text = _taskboardProjection.RuntimeLastCompletedStepBanner;
        TaskboardRuntimeNextStepTextBlock.Text = _taskboardProjection.RuntimeNextStepBanner;
        TaskboardRuntimeProgressTextBlock.Text = _taskboardProjection.RuntimeProgressBanner;
        TaskboardRuntimeLastResultTextBlock.Text = _taskboardProjection.RuntimeLastResultBanner;
        TaskboardRuntimeBlockerOriginTextBlock.Text = _taskboardProjection.RuntimeBlockerOriginBanner;
        TaskboardRuntimeSummaryModeTextBlock.Text = ResolveEffectiveTaskboardRuntimeSummaryModeLabel();
        TaskboardRuntimeSummaryTextBlock.Text = ResolveEffectiveTaskboardRuntimeSummaryText();
        TaskboardRuntimeBaselineTextBlock.Text = _taskboardProjection.RuntimeBaselineBanner;
        TaskboardRuntimeBuildProfileTextBlock.Text = _taskboardProjection.RuntimeBuildProfileBanner;
        TaskboardRuntimeDecompositionTextBlock.Text = _taskboardProjection.RuntimeDecompositionBanner;
        TaskboardRuntimeWorkFamilyTextBlock.Text = _taskboardProjection.RuntimeWorkFamilyBanner;
        TaskboardRuntimeCoverageTextBlock.Text = _taskboardProjection.RuntimeCoverageBanner;
        TaskboardRuntimeLaneTextBlock.Text = _taskboardProjection.RuntimeLaneBanner;
        TaskboardRuntimeLaneBlockerTextBlock.Text = _taskboardProjection.RuntimeLaneBlockerBanner;
        TaskboardRuntimeGoalTextBlock.Text = _taskboardProjection.RuntimeGoalBanner;
        TaskboardRuntimeGoalBlockerTextBlock.Text = _taskboardProjection.RuntimeGoalBlockerBanner;
        TaskboardRecentActivityTextBox.Text = _taskboardProjection.RuntimeRecentActivityText;
        TaskboardLastActionTextBlock.Text = _lastTaskboardActionMessage;
    }

    private void UpdateTaskboardActionControls()
    {
        var hasWorkspace = _workspaceService.HasWorkspace();
        var allowMutatingActions = hasWorkspace && !_taskboardAutoRunInProgress;
        ActivateTaskboardButton.IsEnabled = allowMutatingActions && _taskboardProjection.CanPromoteSelected;
        ArchiveSelectedTaskboardButton.IsEnabled = allowMutatingActions && _taskboardProjection.CanArchiveSelected;
        DeleteSelectedTaskboardButton.IsEnabled = allowMutatingActions && _taskboardProjection.CanDeleteSelected;
        ClearRejectedTaskboardsButton.IsEnabled = allowMutatingActions && _taskboardProjection.CanClearRejected;
        ClearInactiveTaskboardsButton.IsEnabled = allowMutatingActions && _taskboardProjection.CanClearInactive;
        RunActivePlanButton.IsEnabled = allowMutatingActions && _taskboardProjection.CanRunActivePlan;
        RunSelectedBatchButton.IsEnabled = allowMutatingActions && _taskboardProjection.CanRunSelectedBatch;
        InspectTaskboardRawButton.IsEnabled = hasWorkspace && GetSelectedImportRecord() is not null;
        InspectTaskboardParsedButton.IsEnabled = hasWorkspace && GetSelectedImportRecord() is not null;
        InspectTaskboardValidationButton.IsEnabled = hasWorkspace && GetSelectedImportRecord() is not null;
    }

    private void UpdateTaskboardDetails()
    {
        var preservedFirstVisibleLine = _taskboardDetailsAutoFollowEnabled
            ? -1
            : GetTaskboardDetailsFirstVisibleLine();
        TaskboardDetailsTextBox.Text = BuildTaskboardDetailsText();

        if (_taskboardDetailsAutoFollowEnabled)
        {
            ScrollTaskboardDetailsToAnchorOrEnd();
            return;
        }

        RestoreTaskboardDetailsLine(preservedFirstVisibleLine);
    }

    private string BuildTaskboardDetailsText()
    {
        var batch = GetSelectedBatchProjection();
        if (batch is not null)
        {
            return batch.DetailsText;
        }

        var importRecord = GetSelectedImportRecord();
        if (importRecord is null)
        {
            return _taskboardProjection.ActiveImport is null
                ? "No taskboard imports are available for this workspace."
                : _taskboardProjectionService.BuildActivePlanSummary(_taskboardProjection);
        }

        if (!_workspaceService.HasWorkspace())
        {
            return "Set a workspace to inspect taskboards.";
        }

        var validation = _taskboardArtifactStore.LoadValidation(_ramDbService, _workspaceService.WorkspaceRoot, importRecord);
        var details = new List<string>
        {
            _taskboardProjection.SelectedStatusBanner,
            _taskboardProjection.RunTargetBanner,
            _taskboardProjection.ActionAvailabilityBanner,
            _taskboardProjection.RuntimeStatusBanner,
            _taskboardProjection.RuntimeFreshnessBanner,
            _taskboardProjection.RuntimeEntryBanner,
            _taskboardProjection.RuntimeActivationHandoffBanner,
            _taskboardProjection.RuntimePhaseBanner,
            _taskboardProjection.RuntimeCurrentStepBanner,
            _taskboardProjection.RuntimeLatestStepBanner,
            _taskboardProjection.RuntimeLastCompletedStepBanner,
            _taskboardProjection.RuntimeNextStepBanner,
            _taskboardProjection.RuntimeProgressBanner,
            _taskboardProjection.RuntimeLastResultBanner,
            _taskboardProjection.RuntimeBlockerOriginBanner,
            _taskboardProjection.RuntimeSummaryBanner,
            ResolveEffectiveTaskboardRuntimeSummaryModeLabel(),
            ResolveEffectiveTaskboardRuntimeSummaryText(),
            "",
            "Raw summary / debug packet:",
            _taskboardProjection.RuntimeRawSummaryText,
            _taskboardProjection.RuntimeBaselineBanner,
            _taskboardProjection.RuntimeBuildProfileBanner,
            _taskboardProjection.RuntimeDecompositionBanner,
            _taskboardProjection.RuntimeExecutionWiringBanner,
            _taskboardProjection.RuntimeChainDepthBanner,
            _taskboardProjection.RuntimeExecutionTraceText,
            _taskboardProjection.RuntimeMutationProofBanner,
            _taskboardProjection.RuntimeGenerationGuardrailBanner,
            _taskboardProjection.RuntimeWorkFamilyBanner,
            _taskboardProjection.RuntimeCoverageBanner,
            _taskboardProjection.RuntimeLaneBanner,
            _taskboardProjection.RuntimeLaneBlockerBanner,
            _taskboardProjection.RuntimeGoalBanner,
            _taskboardProjection.RuntimeGoalBlockerBanner,
            "",
            "Recent activity:",
            _taskboardProjection.RuntimeRecentActivityText,
            ""
        };
        details.Add(_taskboardProjectionService.BuildValidationSummary(importRecord, validation));
        return string.Join(Environment.NewLine, details);
    }

    private void HandleArchiveSelectedTaskboard()
    {
        if (!_workspaceService.HasWorkspace())
        {
            EmitTaskboardActionMessage("Archive Selected", "Set a workspace before archiving a taskboard.");
            return;
        }

        var importRecord = GetSelectedImportRecord() ?? _taskboardProjection.ActiveImport;
        if (importRecord is null)
        {
            EmitTaskboardActionMessage("Archive Selected", "Archive Selected blocked: no taskboard is selected.");
            return;
        }

        var result = _taskboardCleanupService.ArchiveSelected(_workspaceService.WorkspaceRoot, importRecord.ImportId, _ramDbService);
        _selectedTaskboardImportId = "";
        RefreshTaskboardUi();
        EmitTaskboardActionMessage(result.ActionName, result.Message);
    }

    private void HandleDeleteSelectedTaskboard()
    {
        if (!_workspaceService.HasWorkspace())
        {
            EmitTaskboardActionMessage("Delete Selected", "Set a workspace before deleting a taskboard.");
            return;
        }

        var importRecord = GetSelectedImportRecord();
        if (importRecord is null)
        {
            EmitTaskboardActionMessage("Delete Selected", "Delete Selected blocked: no taskboard is selected.");
            return;
        }

        var result = _taskboardCleanupService.DeleteSelected(_workspaceService.WorkspaceRoot, importRecord.ImportId, _ramDbService);
        _selectedTaskboardImportId = "";
        RefreshTaskboardUi();
        EmitTaskboardActionMessage(result.ActionName, result.Message);
    }

    private void HandleClearRejectedTaskboards()
    {
        if (!_workspaceService.HasWorkspace())
        {
            EmitTaskboardActionMessage("Clear Rejected", "Set a workspace before clearing rejected taskboards.");
            return;
        }

        var result = _taskboardCleanupService.ClearRejected(_workspaceService.WorkspaceRoot, _ramDbService);
        _selectedTaskboardImportId = "";
        RefreshTaskboardUi();
        EmitTaskboardActionMessage(result.ActionName, result.Message);
    }

    private void HandleClearInactiveTaskboards()
    {
        if (!_workspaceService.HasWorkspace())
        {
            EmitTaskboardActionMessage("Clear Inactive", "Set a workspace before clearing inactive taskboards.");
            return;
        }

        var result = _taskboardCleanupService.ClearInactive(_workspaceService.WorkspaceRoot, _ramDbService);
        _selectedTaskboardImportId = _taskboardProjection.ActiveImport?.ImportId ?? "";
        RefreshTaskboardUi();
        EmitTaskboardActionMessage(result.ActionName, result.Message);
    }


    private void EmitTaskboardActionMessage(string actionName, string message)
    {
        SetTaskboardActionStatus(message);

        if (_taskboardActionMessageService.ShouldSuppress(actionName, message, _settings.TaskboardActionMessageDedupeWindowSeconds))
            return;

        AddMessage("assistant", message);
        AppendOutput(message);
    }

    private void SetTaskboardActionStatus(string message)
    {
        _lastTaskboardActionMessage = $"Last action: {message}";
        UpdateTaskboardStatusBlocks();
    }

    private async Task PublishTaskboardRunOutcomeAsync(string actionName, TaskboardAutoRunResult result)
    {
        SetTaskboardActionStatus(result.Message);
        if (await TryPublishTaskboardTerminalSummaryToPrimaryShellAsync(result))
            return;

        EmitTaskboardActionMessage(actionName, result.Message);
    }

    private async Task<bool> TryPublishTaskboardTerminalSummaryToPrimaryShellAsync(TaskboardAutoRunResult result)
    {
        var summary = ResolveVisibleTaskboardTerminalSummary(result);
        if (summary is null || string.IsNullOrWhiteSpace(summary.SummaryId))
            return false;

        var fingerprint = FirstNonEmpty(
            summary.TerminalFingerprint,
            _taskboardRunSummaryService.ComputeTerminalFingerprint(summary),
            summary.SummaryId);
        if (!string.IsNullOrWhiteSpace(fingerprint)
            && string.Equals(_lastPublishedTaskboardTerminalSummaryFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        _lastPublishedTaskboardTerminalSummaryFingerprint = fingerprint;
        var packet = _taskboardOperatorSummaryService.BuildPacket(summary);
        var settings = ResolveCurrentAppSettingsSnapshot(persist: true);
        var renderResult = await _taskboardOperatorSummaryService.RenderAsync(
            packet,
            settings.Endpoint,
            settings.IntakeModel,
            CancellationToken.None);
        CacheRenderedTaskboardOperatorSummary(fingerprint, renderResult);
        RefreshTaskboardUi();

        var shellSummary = BuildPrimaryShellOperatorSummaryText(renderResult);
        if (string.IsNullOrWhiteSpace(shellSummary))
            return false;

        AddMessage("assistant", shellSummary);
        AppendOutput(shellSummary);
        return true;
    }

    private void CacheRenderedTaskboardOperatorSummary(string fingerprint, TaskboardOperatorSummaryRenderResult renderResult)
    {
        _lastRenderedTaskboardOperatorSummaryFingerprint = fingerprint;
        _lastRenderedTaskboardOperatorSummaryText = renderResult.RenderedText;
        _lastRenderedTaskboardOperatorSummaryModeLabel = string.IsNullOrWhiteSpace(renderResult.ModeLabel)
            ? "Summary mode: operator summary"
            : renderResult.ModeLabel;
    }

    private string ResolveEffectiveTaskboardRuntimeSummaryText()
    {
        var visibleSummary = _taskboardProjection.VisibleTerminalSummary;
        var fingerprint = FirstNonEmpty(
            visibleSummary?.TerminalFingerprint,
            _taskboardRunSummaryService.ComputeTerminalFingerprint(visibleSummary),
            visibleSummary?.SummaryId);
        if (!string.IsNullOrWhiteSpace(fingerprint)
            && string.Equals(fingerprint, _lastRenderedTaskboardOperatorSummaryFingerprint, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_lastRenderedTaskboardOperatorSummaryText))
        {
            return _lastRenderedTaskboardOperatorSummaryText;
        }

        return _taskboardProjection.RuntimeSummaryText;
    }

    private string ResolveEffectiveTaskboardRuntimeSummaryModeLabel()
    {
        var visibleSummary = _taskboardProjection.VisibleTerminalSummary;
        var fingerprint = FirstNonEmpty(
            visibleSummary?.TerminalFingerprint,
            _taskboardRunSummaryService.ComputeTerminalFingerprint(visibleSummary),
            visibleSummary?.SummaryId);
        if (!string.IsNullOrWhiteSpace(fingerprint)
            && string.Equals(fingerprint, _lastRenderedTaskboardOperatorSummaryFingerprint, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_lastRenderedTaskboardOperatorSummaryModeLabel))
        {
            return _lastRenderedTaskboardOperatorSummaryModeLabel;
        }

        return _taskboardProjection.RuntimeSummaryModeBanner;
    }

    private TaskboardRunTerminalSummaryRecord? ResolveVisibleTaskboardTerminalSummary(TaskboardAutoRunResult? result = null)
    {
        if (_taskboardProjection.VisibleTerminalSummary is { SummaryId.Length: > 0 } visibleSummary)
            return visibleSummary;

        if (result?.RunState?.LastTerminalSummary is { SummaryId.Length: > 0 } runStateSummary)
            return runStateSummary;

        if (!_workspaceService.HasWorkspace() || _taskboardProjection.ActiveImport is null)
            return null;

        var persistedSummary = _taskboardArtifactStore.LoadLatestRunSummary(_ramDbService, _workspaceService.WorkspaceRoot, _taskboardProjection.ActiveImport);
        var effectiveRunState = result?.RunState ?? _taskboardProjection.RunState;
        return ShouldSuppressPersistedTaskboardTerminalSummary(effectiveRunState, persistedSummary)
            ? null
            : persistedSummary;
    }

    private string BuildPrimaryShellOperatorSummaryText(TaskboardOperatorSummaryRenderResult renderResult)
    {
        if (string.IsNullOrWhiteSpace(renderResult.RenderedText))
            return "";

        return string.IsNullOrWhiteSpace(renderResult.ModeLabel)
            ? renderResult.RenderedText
            : $"{renderResult.ModeLabel}{Environment.NewLine}{renderResult.RenderedText}";
    }

    private static bool ShouldSuppressPersistedTaskboardTerminalSummary(
        TaskboardPlanRunStateRecord? runState,
        TaskboardRunTerminalSummaryRecord? persistedSummary)
    {
        if (runState is null || persistedSummary is null || string.IsNullOrWhiteSpace(persistedSummary.SummaryId))
            return false;

        if (runState.LastTerminalSummary is { SummaryId.Length: > 0 })
            return false;

        if (runState.PlanStatus is TaskboardPlanRuntimeStatus.Completed
            or TaskboardPlanRuntimeStatus.Blocked
            or TaskboardPlanRuntimeStatus.Failed
            or TaskboardPlanRuntimeStatus.PausedManualOnly)
        {
            return false;
        }

        var runUpdatedUtc = ParseTaskboardUtc(runState.UpdatedUtc);
        var summaryEndedUtc = ParseTaskboardUtc(FirstNonEmpty(
            persistedSummary.EndedUtc,
            persistedSummary.CreatedUtc,
            persistedSummary.StartedUtc));
        if (runUpdatedUtc.HasValue
            && summaryEndedUtc.HasValue
            && runUpdatedUtc.Value > summaryEndedUtc.Value)
        {
            return true;
        }

        return runState.CompletedWorkItemCount > persistedSummary.CompletedWorkItemCount;
    }

    private static DateTimeOffset? ParseTaskboardUtc(string? value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private string BuildTaskboardIntakeAssistantMessage(TaskboardIntakeResult result)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(result.Message))
            lines.Add(result.Message);

        if (result.ImportRecord is not null)
        {
            lines.Add($"Classification: {FormatTaskboardDocumentType(result.ImportRecord.DocumentType)} ({FormatTaskboardConfidence(result.ImportRecord.ClassificationConfidence)}).");
            lines.Add($"Classifier: {result.ImportRecord.ClassificationReason}");
            lines.Add($"State: {FormatTaskboardState(result.ImportRecord.State)}.");
            lines.Add($"Validation: {FormatTaskboardValidation(result.ImportRecord.ValidationOutcome)}.");
            if (result.ImportRecord.TitlePatternKind != TaskboardTitlePatternKind.Unknown)
                lines.Add($"Heading format: {FormatTaskboardTitlePatternKind(result.ImportRecord.TitlePatternKind)}.");
        }

        if (result.ParseResult is not null)
        {
            var parserError = result.ParseResult.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == TaskboardParserDiagnosticSeverity.Error);
            var parserWarning = result.ParseResult.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == TaskboardParserDiagnosticSeverity.Warning);
            lines.Add(parserError is null
                ? parserWarning is null
                    ? "Parser: accepted the document structure."
                    : $"Parser: accepted with warning: {parserWarning.Message}"
                : $"Parser: {parserError.Message}");
        }

        if (result.ValidationReport is not null)
        {
            lines.Add(result.ValidationReport.Errors.Count == 0
                ? $"Validator: accepted with {result.ValidationReport.Warnings.Count} warning(s)."
                : $"Validator: rejected with {result.ValidationReport.Errors.Count} error(s).");
        }

        if (result.AutoActivationResult is not null)
        {
            lines.Add(result.AutoActivationResult.Message);
        }
        else if (result.ValidationReport is not null && result.ValidationReport.Errors.Count > 0)
        {
            lines.Add($"Validation errors: {result.ValidationReport.Errors.Count}. Open the Taskboards tab to inspect the stored validation snapshot.");
        }
        else if (result.ImportRecord?.State == TaskboardImportState.ReadyForPromotion)
        {
            lines.Add("Use the Taskboards tab or say `activate selected taskboard` to make it the active parsed plan.");
        }

        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private TaskboardImportRecord? GetSelectedImportRecord()
    {
        return TaskboardImportsListBox.SelectedItem as TaskboardImportRecord;
    }

    private TaskboardBatchProjection? GetSelectedBatchProjection()
    {
        return TaskboardBatchesListBox.SelectedItem as TaskboardBatchProjection;
    }

    private void SelectTaskboardImport(string? importId)
    {
        var selected = _taskboardImports.FirstOrDefault(record => string.Equals(record.ImportId, importId, StringComparison.OrdinalIgnoreCase))
            ?? _taskboardImports.FirstOrDefault(record => record.State == TaskboardImportState.ActivePlan)
            ?? _taskboardImports.FirstOrDefault();
        TaskboardImportsListBox.SelectedItem = selected;
    }

    private void SelectTaskboardBatch(string? batchId)
    {
        var selected = _taskboardBatches.FirstOrDefault(batch => string.Equals(batch.BatchId, batchId, StringComparison.OrdinalIgnoreCase))
            ?? _taskboardBatches.FirstOrDefault();

        _taskboardBatchSelectionUpdateInProgress = true;
        try
        {
            TaskboardBatchesListBox.SelectedItem = selected;
            if (selected is not null)
                TaskboardBatchesListBox.ScrollIntoView(selected);
        }
        finally
        {
            _taskboardBatchSelectionUpdateInProgress = false;
        }
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    private string ResolveTaskboardBatchSelectionTarget()
    {
        if (!_taskboardBatchAutoFollowEnabled)
            return _selectedTaskboardBatchId;

        return FirstTaskboardNonEmpty(
            GetAuthoritativeTaskboardBatchId(),
            _selectedTaskboardBatchId);
    }

    private string GetAuthoritativeTaskboardBatchId()
    {
        return FirstTaskboardNonEmpty(
            _taskboardProjection.RunState?.CurrentBatchId,
            _taskboardProjection.RunState?.LastFollowupBatchId);
    }

    private void ScrollTaskboardDetailsToAnchorOrEnd()
    {
        try
        {
            TaskboardDetailsTextBox.UpdateLayout();
            var anchorLine = ResolveTaskboardDetailsAnchorLine();
            if (anchorLine >= 0)
            {
                TaskboardDetailsTextBox.ScrollToLine(Math.Max(anchorLine - 2, 0));
                return;
            }

            TaskboardDetailsTextBox.CaretIndex = TaskboardDetailsTextBox.Text.Length;
            TaskboardDetailsTextBox.ScrollToEnd();
        }
        catch
        {
            TaskboardDetailsTextBox.CaretIndex = TaskboardDetailsTextBox.Text.Length;
            TaskboardDetailsTextBox.ScrollToEnd();
        }
    }

    private int ResolveTaskboardDetailsAnchorLine()
    {
        var anchor = ResolveTaskboardDetailsAnchorText();
        if (string.IsNullOrWhiteSpace(anchor))
            return -1;

        var lines = TaskboardDetailsTextBox.Text
            .Split(new[] { Environment.NewLine }, StringSplitOptions.None);
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Contains(anchor, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    private string ResolveTaskboardDetailsAnchorText()
    {
        var runState = _taskboardProjection.RunState;
        if (runState is null)
            return "";

        var selectedBatchId = GetSelectedBatchProjection()?.BatchId ?? _selectedTaskboardBatchId;
        if (!string.IsNullOrWhiteSpace(selectedBatchId))
        {
            if (string.Equals(selectedBatchId, runState.CurrentBatchId, StringComparison.OrdinalIgnoreCase))
            {
                return FirstTaskboardNonEmpty(
                    ResolveWorkItemTitle(runState, runState.CurrentBatchId, runState.CurrentWorkItemId),
                    runState.CurrentWorkItemId,
                    runState.LastCompletedWorkItemTitle);
            }

            if (string.Equals(selectedBatchId, runState.LastFollowupBatchId, StringComparison.OrdinalIgnoreCase))
            {
                return FirstTaskboardNonEmpty(
                    ResolveWorkItemTitle(runState, runState.LastFollowupBatchId, runState.LastFollowupWorkItemId),
                    runState.LastFollowupWorkItemTitle,
                    runState.LastCompletedWorkItemTitle);
            }
        }

        return FirstTaskboardNonEmpty(
            ResolveWorkItemTitle(runState, runState.CurrentBatchId, runState.CurrentWorkItemId),
            runState.LastFollowupWorkItemTitle,
            runState.LastCompletedWorkItemTitle);
    }

    private static string ResolveWorkItemTitle(TaskboardPlanRunStateRecord? runState, string? batchId, string? workItemId)
    {
        if (runState is null
            || string.IsNullOrWhiteSpace(batchId)
            || string.IsNullOrWhiteSpace(workItemId))
        {
            return "";
        }

        var batch = runState.Batches.FirstOrDefault(current =>
            string.Equals(current.BatchId, batchId, StringComparison.OrdinalIgnoreCase));
        var workItem = batch?.WorkItems.FirstOrDefault(current =>
            string.Equals(current.WorkItemId, workItemId, StringComparison.OrdinalIgnoreCase));
        return workItem?.Title ?? "";
    }

    private int GetTaskboardDetailsFirstVisibleLine()
    {
        try
        {
            return TaskboardDetailsTextBox.GetFirstVisibleLineIndex();
        }
        catch
        {
            return -1;
        }
    }

    private void RestoreTaskboardDetailsLine(int firstVisibleLine)
    {
        if (firstVisibleLine < 0)
            return;

        try
        {
            TaskboardDetailsTextBox.UpdateLayout();
            var targetLine = Math.Min(firstVisibleLine, Math.Max(TaskboardDetailsTextBox.LineCount - 1, 0));
            TaskboardDetailsTextBox.ScrollToLine(targetLine);
        }
        catch
        {
            TaskboardDetailsTextBox.ScrollToEnd();
        }
    }

    private void ResetTaskboardLiveRunFeed()
    {
        _taskboardBatchAutoFollowEnabled = true;
        _taskboardDetailsAutoFollowEnabled = true;
        DispatchTaskboardUiRefreshAsync("taskboard_live_run_reset", null).GetAwaiter().GetResult();
    }

    private Task PublishTaskboardLiveProgressAsync(TaskboardLiveProgressUpdate update)
    {
        return DispatchTaskboardUiRefreshAsync("taskboard_live_progress", update);
    }

    private async Task DispatchTaskboardUiRefreshAsync(string source, TaskboardLiveProgressUpdate? update)
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshTaskboardUi();
            return;
        }

        var callingThreadId = Environment.CurrentManagedThreadId;
        var dispatcherThreadId = Dispatcher.Thread.ManagedThreadId;

        try
        {
            await Dispatcher.InvokeAsync(RefreshTaskboardUi);
        }
        catch (Exception ex)
        {
            AppendOutput(
                $"Taskboard UI refresh failed: source={source} event={DisplayTaskboardValue(update?.EventKind ?? update?.PhaseCode ?? "(none)")} calling_thread={callingThreadId} dispatcher_thread={dispatcherThreadId} message={ex.Message}");
            throw new InvalidOperationException(
                $"Taskboard UI refresh failed for source={source} on calling_thread={callingThreadId} dispatcher_thread={dispatcherThreadId}.",
                ex);
        }
    }

    private static string NormalizeTaskboardPrompt(string prompt)
    {
        return (prompt ?? "").Trim().ToLowerInvariant();
    }

    private static string FirstTaskboardNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static string FormatTaskboardDocumentType(TaskboardDocumentType documentType)
    {
        return documentType switch
        {
            TaskboardDocumentType.CodexTaskboard => "codex_taskboard",
            TaskboardDocumentType.TaskboardCandidate => "taskboard_candidate",
            TaskboardDocumentType.PlainRequest => "plain_request",
            TaskboardDocumentType.UnsupportedStructuredDocument => "unsupported_structured_document",
            _ => "unknown"
        };
    }

    private static string FormatTaskboardTitlePatternKind(TaskboardTitlePatternKind patternKind)
    {
        return patternKind switch
        {
            TaskboardTitlePatternKind.Preferred => "preferred",
            TaskboardTitlePatternKind.Accepted => "accepted",
            TaskboardTitlePatternKind.Candidate => "candidate",
            TaskboardTitlePatternKind.Other => "other",
            _ => "unknown"
        };
    }

    private static string FormatTaskboardConfidence(TaskboardClassificationConfidence confidence)
    {
        return confidence switch
        {
            TaskboardClassificationConfidence.High => "high",
            TaskboardClassificationConfidence.Medium => "medium",
            _ => "low"
        };
    }

    private static string FormatTaskboardState(TaskboardImportState state)
    {
        return state switch
        {
            TaskboardImportState.ReadyForPromotion => "ready_for_promotion",
            TaskboardImportState.ActivePlan => "active_plan",
            _ => state.ToString().ToLowerInvariant()
        };
    }

    private static string FormatTaskboardValidation(TaskboardValidationOutcome outcome)
    {
        return outcome switch
        {
            TaskboardValidationOutcome.ValidWithWarnings => "valid_with_warnings",
            TaskboardValidationOutcome.MissingRequiredSection => "missing_required_section",
            TaskboardValidationOutcome.DuplicateBatch => "duplicate_batch",
            TaskboardValidationOutcome.UnsupportedStructure => "unsupported_structure",
            TaskboardValidationOutcome.TextLimitExceeded => "text_limit_exceeded",
            TaskboardValidationOutcome.UnsafeContentDetected => "unsafe_content_detected",
            TaskboardValidationOutcome.ValidationException => "validation_exception",
            _ => outcome.ToString().ToLowerInvariant()
        };
    }
}
