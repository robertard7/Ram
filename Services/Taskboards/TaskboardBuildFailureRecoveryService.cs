using System.IO;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardBuildFailureRecoveryService
{
    private readonly RepairContextService _repairContextService = new(
        new WorkspaceBuildIndexService(),
        new ArtifactClassificationService());
    private readonly TaskboardExecutionLaneResolutionService _laneResolutionService = new();
    private readonly TaskboardWorkItemStateRefreshService _workItemStateRefreshService = new();

    public TaskboardBuildFailureRecoveryResult TryPromote(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord failedWorkItem,
        RamDbService ramDbService)
    {
        var executionState = ramDbService.LoadExecutionState(workspaceRoot);
        var failureKind = ResolveRecoverableFailureKind(executionState.LastFailureOutcomeType);
        if (string.IsNullOrWhiteSpace(failureKind))
            return new TaskboardBuildFailureRecoveryResult();

        var failureContext = _repairContextService.ResolveContext(workspaceRoot, ramDbService, ResolveFailureScope(failureKind));
        if (!failureContext.Success || failureContext.RepairContext is null)
            return new TaskboardBuildFailureRecoveryResult();

        var classification = ClassifyFailure(runState, failedWorkItem, executionState, failureContext, failureKind);
        if (!classification.IsActionable)
            return new TaskboardBuildFailureRecoveryResult();

        var repairArtifact = ramDbService.LoadLatestArtifactByType(workspaceRoot, "repair_context");
        var followUpWorkItem = EnsureFollowUpWorkItem(batch, failedWorkItem, classification, repairArtifact?.RelativePath ?? "");
        _workItemStateRefreshService.Refresh(runState, batch, followUpWorkItem);

        var lanePreview = _laneResolutionService.Resolve(
            workspaceRoot,
            _workItemStateRefreshService.ToRunWorkItem(followUpWorkItem),
            runState.PlanTitle,
            "");

        var selection = new TaskboardFollowUpWorkItemSelectionRecord
        {
            SelectionId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            PlanImportId = activeImport.ImportId,
            PlanTitle = activeImport.Title,
            BatchId = batch.BatchId,
            BatchTitle = batch.Title,
            WorkItemId = followUpWorkItem.WorkItemId,
            WorkItemTitle = followUpWorkItem.Title,
            SelectionReason = BuildSelectionReason(failureKind),
            SelectedAfterPostChainReconciliation = false,
            Summary = BuildSelectionSummary(failureKind, followUpWorkItem.Title, failedWorkItem.Title),
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };

        var resolution = new TaskboardFollowUpWorkItemResolutionRecord
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            PlanImportId = activeImport.ImportId,
            PlanTitle = activeImport.Title,
            BatchId = batch.BatchId,
            BatchTitle = batch.Title,
            WorkItemId = followUpWorkItem.WorkItemId,
            WorkItemTitle = followUpWorkItem.Title,
            SelectionReason = selection.SelectionReason,
            WorkFamily = FirstMeaningful(lanePreview.WorkFamily, followUpWorkItem.WorkFamily),
            PhraseFamily = FirstMeaningful(lanePreview.PhraseFamily, followUpWorkItem.PhraseFamily),
            OperationKind = FirstMeaningful(lanePreview.OperationKind, followUpWorkItem.OperationKind),
            StackFamily = FirstMeaningful(lanePreview.TargetStack, followUpWorkItem.TargetStack),
            LaneKind = lanePreview.LaneKind.ToString().ToLowerInvariant(),
            LaneTarget = FirstMeaningful(lanePreview.SelectedChainTemplateId, lanePreview.SelectedToolId, followUpWorkItem.TemplateId),
            LaneBlockerCode = lanePreview.Blocker.Code == TaskboardExecutionLaneBlockerCode.None
                ? ""
                : lanePreview.Blocker.Code.ToString().ToLowerInvariant(),
            LaneBlockerMessage = lanePreview.Blocker.Message,
            FailureKind = failureKind,
            FailureFamily = classification.FailureFamily,
            FailureErrorCode = classification.ErrorCode,
            FailureNormalizedSummary = classification.NormalizedSummary,
            FailureTargetPath = classification.TargetPath,
            FailureSourcePath = classification.SourcePath,
            RepairContextPath = repairArtifact?.RelativePath ?? "",
            ResolutionOrigin = BuildResolutionOrigin(failureKind),
            DeterministicRefreshAttempted = true,
            AdvisoryAssistAttempted = false,
            Summary = BuildSummary(followUpWorkItem, classification, lanePreview, repairArtifact?.RelativePath ?? ""),
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };

        return new TaskboardBuildFailureRecoveryResult
        {
            Promoted = true,
            FollowUpWorkItem = followUpWorkItem,
            FollowUpSelection = selection,
            FollowUpResolution = resolution,
            FailureKind = failureKind,
            FailureFamily = classification.FailureFamily,
            FailureErrorCode = classification.ErrorCode,
            FailureNormalizedSummary = classification.NormalizedSummary,
            FailureTargetPath = classification.TargetPath,
            FailureSourcePath = classification.SourcePath,
            RepairContextPath = repairArtifact?.RelativePath ?? "",
            Summary = resolution.Summary
        };
    }

    internal TaskboardBuildFailureClassification TryClassifyActiveFailure(
        string workspaceRoot,
        TaskboardPlanRunStateRecord? runState,
        TaskboardRunWorkItem workItem,
        RamDbService ramDbService)
    {
        var executionState = ramDbService.LoadExecutionState(workspaceRoot);
        var failureKind = ResolveRecoverableFailureKind(executionState.LastFailureOutcomeType);
        if (string.IsNullOrWhiteSpace(failureKind))
            return new TaskboardBuildFailureClassification();

        if (HasVerifiedRepairAfterFailure(executionState))
            return new TaskboardBuildFailureClassification();

        var failureContext = _repairContextService.ResolveContext(workspaceRoot, ramDbService, ResolveFailureScope(failureKind));
        if (!failureContext.Success || failureContext.RepairContext is null)
            return new TaskboardBuildFailureClassification();

        var failedWorkItem = new TaskboardWorkItemRunStateRecord
        {
            WorkItemId = workItem.WorkItemId,
            Title = workItem.Title,
            PromptText = workItem.PromptText,
            Summary = workItem.Summary,
            OperationKind = workItem.OperationKind,
            TargetStack = workItem.TargetStack,
            WorkFamily = workItem.WorkFamily,
            PhraseFamily = workItem.PhraseFamily,
            TemplateId = workItem.TemplateId
        };

        return ClassifyFailure(
            runState ?? new TaskboardPlanRunStateRecord(),
            failedWorkItem,
            executionState,
            failureContext,
            failureKind);
    }

    private static TaskboardBuildFailureClassification ClassifyFailure(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord failedWorkItem,
        WorkspaceExecutionStateRecord executionState,
        FailureContextResolutionResult failureContext,
        string failureKind)
    {
        var repairContext = failureContext.RepairContext!;
        var verificationTargetPath = FirstMeaningful(
            NormalizeRelativePath(repairContext.TargetPath),
            NormalizeRelativePath(executionState.LastFailureTargetPath));
        var errorCode = FirstMeaningful(
            repairContext.NormalizedErrorCode,
            failureContext.Item?.Code);
        var normalizedSummary = FirstMeaningful(
            repairContext.NormalizedFailureSummary,
            repairContext.Summary,
            executionState.LastFailureSummary);
        var sourcePath = FirstMeaningful(
            NormalizeRelativePath(repairContext.NormalizedSourcePath),
            NormalizeRelativePath(failureContext.Item?.RelativePath),
            NormalizeRelativePath(failureContext.Item?.RawPath));
        var targetPath = string.Equals(failureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
            ? FirstMeaningful(sourcePath, verificationTargetPath)
            : FirstMeaningful(
                verificationTargetPath,
                NormalizeRelativePath(failureContext.Item?.RelativePath));
        if (string.IsNullOrWhiteSpace(targetPath))
            return new TaskboardBuildFailureClassification();

        var failureFamily = FirstMeaningful(
            repairContext.FailureFamily,
            InferFailureFamily(failureKind, errorCode, normalizedSummary, targetPath, verificationTargetPath, sourcePath));
        var stackFamily = InferStackFamily(runState, failedWorkItem, targetPath);
        var operationKind = InferOperationKind(targetPath, errorCode, normalizedSummary);
        var phraseFamily = string.Equals(failureFamily, "solution_graph_circular_dependency", StringComparison.OrdinalIgnoreCase)
            ? "solution_graph_repair"
            : "build_failure_repair";
        var isTestFailure = string.Equals(failureKind, "test_failure", StringComparison.OrdinalIgnoreCase);
        var isTestCodeTarget = IsLikelyTestCodeTarget(targetPath, verificationTargetPath);
        var title = BuildRepairTitle(isTestFailure, isTestCodeTarget, errorCode);
        var promptText = BuildRepairPrompt(isTestFailure, isTestCodeTarget, operationKind, targetPath, verificationTargetPath);
        var summary = BuildFailurePromotionSummary(
            failureKind,
            targetPath,
            verificationTargetPath,
            errorCode,
            normalizedSummary,
            failureContext.Item?.Title ?? "");

        return new TaskboardBuildFailureClassification
        {
            IsActionable = true,
            FailureKind = failureKind,
            Title = title,
            PromptText = promptText,
            Summary = summary,
            WorkFamily = "build_repair",
            PhraseFamily = phraseFamily,
            OperationKind = operationKind,
            StackFamily = stackFamily,
            TemplateId = "repair_execution_chain",
            TargetPath = targetPath,
            VerificationTargetPath = verificationTargetPath,
            SourcePath = sourcePath,
            FailureFamily = failureFamily,
            ErrorCode = errorCode,
            NormalizedSummary = normalizedSummary
        };
    }

    private static TaskboardWorkItemRunStateRecord EnsureFollowUpWorkItem(
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord failedWorkItem,
        TaskboardBuildFailureClassification classification,
        string repairContextPath)
    {
        var existing = batch.WorkItems.FirstOrDefault(item =>
            string.Equals(item.SourceWorkItemId, failedWorkItem.WorkItemId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.WorkFamily, "build_repair", StringComparison.OrdinalIgnoreCase));
        var followUp = existing ?? new TaskboardWorkItemRunStateRecord
        {
            WorkItemId = $"{failedWorkItem.WorkItemId}:repair",
            Ordinal = Math.Max(batch.WorkItems.Count == 0 ? 0 : batch.WorkItems.Max(item => item.Ordinal), failedWorkItem.Ordinal) + 1,
            DisplayOrdinal = string.IsNullOrWhiteSpace(failedWorkItem.DisplayOrdinal)
                ? $"{failedWorkItem.Ordinal}.repair"
                : $"{failedWorkItem.DisplayOrdinal}.repair",
            IsDecomposedItem = true,
            SourceWorkItemId = failedWorkItem.WorkItemId,
            Status = TaskboardWorkItemRuntimeStatus.Pending
        };

        followUp.Title = classification.Title;
        followUp.PromptText = classification.PromptText;
        followUp.Summary = classification.Summary;
        followUp.OperationKind = classification.OperationKind;
        followUp.TargetStack = classification.StackFamily;
        followUp.WorkFamily = classification.WorkFamily;
        followUp.PhraseFamily = classification.PhraseFamily;
        followUp.PhraseFamilySource = "failure_recovery";
        followUp.TemplateId = classification.TemplateId;
        followUp.TemplateCandidateIds = [classification.TemplateId];
        followUp.ExpectedArtifact = FirstMeaningful(repairContextPath, classification.TargetPath);
        followUp.ValidationHint = BuildValidationHint(classification, repairContextPath);
        followUp.DirectToolRequest = BuildRepairToolRequest(classification);
        followUp.LastResultKind = "";
        followUp.LastResultSummary = "";
        followUp.LastExecutionGoalResolution = new TaskboardExecutionGoalResolution();
        followUp.Status = TaskboardWorkItemRuntimeStatus.Pending;
        followUp.UpdatedUtc = DateTime.UtcNow.ToString("O");

        if (existing is null)
            batch.WorkItems.Add(followUp);

        batch.TotalWorkItemCount = batch.WorkItems.Count(item => item.Status != TaskboardWorkItemRuntimeStatus.Skipped || item.IsDecomposedItem);
        return followUp;
    }

    private static ToolRequest BuildRepairToolRequest(TaskboardBuildFailureClassification classification)
    {
        var request = new ToolRequest
        {
            ToolName = "plan_repair",
            PreferredChainTemplateName = "repair_execution_chain",
            Reason = classification.Summary,
            ExecutionSourceType = ExecutionSourceType.BuildTool,
            ExecutionSourceName = "taskboard_failure_recovery",
            IsAutomaticTrigger = true,
            ExecutionAllowed = true,
            ExecutionPolicyMode = "taskboard_auto_run",
            ExecutionBuildFamily = "repair"
        };
        request.Arguments["scope"] = ResolveFailureScope(classification.FailureKind);
        request.Arguments["path"] = classification.TargetPath;
        return request;
    }

    private static string BuildValidationHint(TaskboardBuildFailureClassification classification, string repairContextPath)
    {
        var repairContextHint = string.IsNullOrWhiteSpace(repairContextPath)
            ? ""
            : $" Repair context: {repairContextPath}.";
        if (string.Equals(classification.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase))
            return $"Use recorded test failure evidence to inspect `{classification.TargetPath}` and rerun `dotnet test` for `{FirstMeaningful(classification.VerificationTargetPath, classification.TargetPath)}` after the smallest safe repair.{repairContextHint}";

        return $"Use recorded build failure evidence to inspect `{classification.TargetPath}` and rerun workspace build verification after the smallest safe repair.{repairContextHint}";
    }

    private static string BuildSummary(
        TaskboardWorkItemRunStateRecord followUpWorkItem,
        TaskboardBuildFailureClassification classification,
        TaskboardExecutionLaneResolution lanePreview,
        string repairContextPath)
    {
        var lane = lanePreview.LaneKind == TaskboardExecutionLaneKind.Unknown
            ? "(none)"
            : lanePreview.LaneKind.ToString().ToLowerInvariant();
        var laneTarget = FirstMeaningful(lanePreview.SelectedChainTemplateId, lanePreview.SelectedToolId, "(none)");
        var repairContext = string.IsNullOrWhiteSpace(repairContextPath)
            ? ""
            : $" repair_context={repairContextPath}";
        var verificationTarget = string.IsNullOrWhiteSpace(classification.VerificationTargetPath)
            ? ""
            : $" verification_target={classification.VerificationTargetPath}";
        var prefix = string.Equals(classification.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
            ? "Test failure follow-up"
            : "Build failure follow-up";
        return $"{prefix} `{followUpWorkItem.Title}` family={classification.WorkFamily} phrase_family={classification.PhraseFamily} operation_kind={classification.OperationKind} stack={classification.StackFamily} error={FirstMeaningful(classification.ErrorCode, "(none)")} failure_family={FirstMeaningful(classification.FailureFamily, "(none)")} target={classification.TargetPath}{verificationTarget} lane={lane} lane_target={laneTarget}{repairContext} summary={FirstMeaningful(followUpWorkItem.Summary, classification.NormalizedSummary)}";
    }

    private static string InferFailureFamily(
        string failureKind,
        string errorCode,
        string normalizedSummary,
        string targetPath,
        string verificationTargetPath,
        string sourcePath)
    {
        if (string.Equals(failureKind, "test_failure", StringComparison.OrdinalIgnoreCase))
        {
            var effectiveTarget = FirstMeaningful(targetPath, sourcePath);
            return IsLikelyTestCodeTarget(effectiveTarget, verificationTargetPath)
                ? "test_code_failure"
                : "production_code_failure";
        }

        if (string.Equals(errorCode, "MSB4006", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(normalizedSummary, "MSB4006", "circular dependency", "target dependency graph")
            || ContainsAny(sourcePath, "NuGet.targets"))
        {
            return "solution_graph_circular_dependency";
        }

        return "build_failure";
    }

    private static string InferOperationKind(string targetPath, string errorCode, string normalizedSummary)
    {
        if (NormalizeRelativePath(targetPath).EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return "inspect_solution_wiring";

        if (string.Equals(errorCode, "MSB4006", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(normalizedSummary, "circular dependency", "target dependency graph", "project reference"))
        {
            return "inspect_project_reference_graph";
        }

        return "repair_generated_build_targets";
    }

    private static string InferStackFamily(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord failedWorkItem,
        string targetPath)
    {
        var direct = FirstMeaningful(failedWorkItem.TargetStack, runState.LastResolvedBuildProfile.StackFamily switch
        {
            TaskboardStackFamily.DotnetDesktop => "dotnet_desktop",
            TaskboardStackFamily.NativeCppDesktop => "native_cpp_desktop",
            _ => ""
        });
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        return ContainsAny(targetPath, ".sln", ".csproj", ".props", ".targets")
            ? "dotnet_desktop"
            : "";
    }

    private static string NormalizeRelativePath(string? path)
    {
        return (path ?? "").Replace('\\', '/').Trim();
    }

    private static string ResolveRecoverableFailureKind(string? failureKind)
    {
        return string.Equals(failureKind, "build_failure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(failureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
            ? failureKind!.Trim()
            : "";
    }

    private static string ResolveFailureScope(string failureKind)
    {
        return string.Equals(failureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
            ? "test"
            : "build";
    }

    private static string BuildSelectionReason(string failureKind)
    {
        return string.Equals(failureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
            ? "test_failure_recovery_followup"
            : "build_failure_recovery_followup";
    }

    private static string BuildSelectionSummary(string failureKind, string followUpTitle, string failedWorkItemTitle)
    {
        return string.Equals(failureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
            ? $"Selected bounded test-failure recovery follow-up `{followUpTitle}` after `{failedWorkItemTitle}` failed test execution."
            : $"Selected bounded build-failure recovery follow-up `{followUpTitle}` after `{failedWorkItemTitle}` failed verification.";
    }

    private static string BuildResolutionOrigin(string failureKind)
    {
        return string.Equals(failureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
            ? "test_failure_followup_refresh"
            : "build_failure_followup_refresh";
    }

    private static string BuildRepairTitle(bool isTestFailure, bool isTestCodeTarget, string errorCode)
    {
        if (!isTestFailure)
        {
            return string.Equals(errorCode, "MSB4006", StringComparison.OrdinalIgnoreCase)
                ? "Repair circular build dependency"
                : "Repair workspace build failure";
        }

        return isTestCodeTarget
            ? "Repair failing test"
            : "Repair implementation surfaced by failing test";
    }

    private static string BuildRepairPrompt(
        bool isTestFailure,
        bool isTestCodeTarget,
        string operationKind,
        string targetPath,
        string verificationTargetPath)
    {
        if (!isTestFailure)
        {
            return string.Equals(operationKind, "inspect_solution_wiring", StringComparison.OrdinalIgnoreCase)
                ? $"Inspect solution wiring and repair the circular build dependency for `{targetPath}`, then rerun workspace build verification."
                : $"Inspect project wiring and repair the build failure for `{targetPath}`, then rerun workspace build verification.";
        }

        var rerunTarget = FirstMeaningful(verificationTargetPath, targetPath);
        return isTestCodeTarget
            ? $"Inspect the failing test code in `{targetPath}` and repair the bounded failure, then rerun `dotnet test` for `{rerunTarget}`."
            : $"Inspect `{targetPath}` and repair the implementation surfaced by the failing test, then rerun `dotnet test` for `{rerunTarget}`.";
    }

    private static string BuildFailurePromotionSummary(
        string failureKind,
        string targetPath,
        string verificationTargetPath,
        string errorCode,
        string normalizedSummary,
        string failedTestName)
    {
        if (string.Equals(failureKind, "test_failure", StringComparison.OrdinalIgnoreCase))
        {
            var label = FirstMeaningful(failedTestName, errorCode, "test_failure");
            var rerunTarget = FirstMeaningful(verificationTargetPath, targetPath);
            return string.IsNullOrWhiteSpace(normalizedSummary)
                ? $"Promoted failing test `{label}` against `{targetPath}` into bounded repair work before rerunning `{rerunTarget}`."
                : $"Promoted failing test `{label}` against `{targetPath}` into bounded repair work: {normalizedSummary}";
        }

        return string.IsNullOrWhiteSpace(normalizedSummary)
            ? $"Promoted build verification failure for `{targetPath}` into bounded repair work."
            : $"Promoted build verification failure `{FirstMeaningful(errorCode, "build_failure")}` for `{targetPath}` into bounded repair work: {normalizedSummary}";
    }

    private static bool IsLikelyTestCodeTarget(string path, string verificationTargetPath)
    {
        var normalizedPath = NormalizeRelativePath(path);
        var normalizedVerificationTarget = NormalizeRelativePath(verificationTargetPath);
        return ContainsAny(normalizedPath, "/tests/", "/test/", ".tests/", ".test/", "tests.cs", "test.cs")
            || ContainsAny(normalizedVerificationTarget, "tests", "test");
    }

    private static bool HasVerifiedRepairAfterFailure(WorkspaceExecutionStateRecord executionState)
    {
        if (!string.Equals(executionState.LastVerificationOutcomeType, "verified_fixed", StringComparison.OrdinalIgnoreCase))
            return false;

        var failureUtc = ParseUtc(executionState.LastFailureUtc);
        var verificationUtc = ParseUtc(executionState.LastVerificationUtc);
        return verificationUtc >= failureUtc && verificationUtc != DateTime.MinValue;
    }

    private static DateTime ParseUtc(string? value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.MinValue;
    }

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstMeaningful(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && !string.Equals(value.Trim(), "unknown", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value.Trim(), "(none)", StringComparison.OrdinalIgnoreCase))
            {
                return value.Trim();
            }
        }

        return "";
    }
}

public sealed class TaskboardBuildFailureRecoveryResult
{
    public bool Promoted { get; set; }
    public string Summary { get; set; } = "";
    public string FailureKind { get; set; } = "";
    public string FailureFamily { get; set; } = "";
    public string FailureErrorCode { get; set; } = "";
    public string FailureNormalizedSummary { get; set; } = "";
    public string FailureTargetPath { get; set; } = "";
    public string FailureSourcePath { get; set; } = "";
    public string RepairContextPath { get; set; } = "";
    public TaskboardWorkItemRunStateRecord? FollowUpWorkItem { get; set; }
    public TaskboardFollowUpWorkItemSelectionRecord FollowUpSelection { get; set; } = new();
    public TaskboardFollowUpWorkItemResolutionRecord FollowUpResolution { get; set; } = new();
}

internal sealed class TaskboardBuildFailureClassification
{
    public bool IsActionable { get; set; }
    public string FailureKind { get; set; } = "";
    public string Title { get; set; } = "";
    public string PromptText { get; set; } = "";
    public string Summary { get; set; } = "";
    public string WorkFamily { get; set; } = "";
    public string PhraseFamily { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string StackFamily { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string VerificationTargetPath { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string FailureFamily { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public string NormalizedSummary { get; set; } = "";
}
