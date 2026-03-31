using System.Globalization;
using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardProjectionService
{
    private readonly TaskboardArtifactStore _artifactStore = new();
    private readonly CSharpExecutionCoverageService _cSharpExecutionCoverageService = new();
    private readonly TaskboardRuntimeStateFingerprintService _runtimeStateFingerprintService = new();
    private readonly TaskboardOperatorSummaryService _operatorSummaryService = new();
    private readonly TaskboardRunSummaryService _runSummaryService = new();

    public TaskboardProjection BuildProjection(string workspaceRoot, RamDbService ramDbService)
    {
        return BuildProjection(workspaceRoot, ramDbService, null, null, includeArchived: false);
    }

    public TaskboardProjection BuildProjection(
        string workspaceRoot,
        RamDbService ramDbService,
        string? selectedImportId,
        string? selectedBatchId,
        bool includeArchived)
    {
        var allImports = _artifactStore.LoadImports(ramDbService, workspaceRoot, 80)
            .Where(importRecord => importRecord.State != TaskboardImportState.Deleted)
            .ToList();
        var activeImport = allImports.FirstOrDefault(importRecord => importRecord.State == TaskboardImportState.ActivePlan);
        var activeDocument = activeImport is null
            ? null
            : _artifactStore.LoadPlan(ramDbService, workspaceRoot, activeImport);
        var activeValidation = activeImport is null
            ? null
            : _artifactStore.LoadValidation(ramDbService, workspaceRoot, activeImport);
        var persistedSummary = activeImport is null
            ? null
            : _artifactStore.LoadLatestRunSummary(ramDbService, workspaceRoot, activeImport);
        var runState = activeImport is null
            ? null
            : _artifactStore.LoadRunState(ramDbService, workspaceRoot, activeImport);
        var runtimeAssessment = activeImport is null || activeDocument is null
            ? null
            : _runtimeStateFingerprintService.Evaluate(runState, activeImport, activeDocument);
        var effectiveRunState = runtimeAssessment is not null && runtimeAssessment.HasSnapshot && !runtimeAssessment.IsCompatible
            ? null
            : runState;
        var visibleImports = includeArchived
            ? allImports
            : allImports.Where(importRecord => importRecord.State != TaskboardImportState.Archived).ToList();
        var selectedImport = ResolveSelectedImport(visibleImports, activeImport, selectedImportId);
        var selectedDocument = selectedImport is null
            ? null
            : _artifactStore.LoadPlan(ramDbService, workspaceRoot, selectedImport);
        var selectedValidation = selectedImport is null
            ? null
            : _artifactStore.LoadValidation(ramDbService, workspaceRoot, selectedImport);
        var batchProjections = BuildBatchProjections(activeDocument, effectiveRunState);
        var selectedBatch = ResolveSelectedBatch(batchProjections, selectedBatchId);
        var visibleSummary = ResolveVisibleTerminalSummary(runState, persistedSummary);
        var visibleSummaryPacket = _operatorSummaryService.BuildPacket(visibleSummary);
        var canPromoteSelected = selectedImport is not null && selectedImport.State == TaskboardImportState.ReadyForPromotion;
        var canRunActivePlanViaActivationHandoff = activeImport is null
            && selectedImport is not null
            && selectedImport.State is TaskboardImportState.ReadyForPromotion or TaskboardImportState.Validated
            && selectedDocument is not null
            && selectedDocument.Batches.Count > 0;
        var canArchiveSelected = selectedImport is not null
            && selectedImport.State is not TaskboardImportState.Archived and not TaskboardImportState.Deleted;
        var canDeleteSelected = selectedImport is not null
            && selectedImport.State is not TaskboardImportState.ActivePlan and not TaskboardImportState.Deleted;
        var canRunActivePlan = (activeImport is not null && activeDocument is not null && activeDocument.Batches.Count > 0)
            || canRunActivePlanViaActivationHandoff;

        return new TaskboardProjection
        {
            WorkspaceRoot = workspaceRoot,
            TotalImportCount = allImports.Count,
            HiddenArchivedCount = allImports.Count(importRecord => importRecord.State == TaskboardImportState.Archived) - visibleImports.Count(importRecord => importRecord.State == TaskboardImportState.Archived),
            RejectedCount = allImports.Count(importRecord => importRecord.State == TaskboardImportState.Rejected),
            InactiveCount = allImports.Count(importRecord => importRecord.State is not TaskboardImportState.ActivePlan and not TaskboardImportState.Deleted),
            Imports = visibleImports,
            ActiveImport = activeImport,
            ActiveDocument = activeDocument,
            ActiveValidationReport = activeValidation,
            RunState = runState,
            IsRuntimeSnapshotStale = runtimeAssessment is not null && runtimeAssessment.HasSnapshot && !runtimeAssessment.IsCompatible,
            SelectedImport = selectedImport,
            SelectedValidationReport = selectedValidation,
            SelectedBatch = selectedBatch,
            ObjectiveSummary = BuildObjectiveSummary(activeDocument),
            ValidationBanner = BuildValidationBanner(activeImport, activeValidation),
            SelectedStatusBanner = BuildSelectedStatusBanner(selectedImport, activeImport, canRunActivePlanViaActivationHandoff),
            RunTargetBanner = BuildRunTargetBanner(selectedImport, activeImport, canRunActivePlanViaActivationHandoff),
            RuntimeStatusBanner = BuildRuntimeStatusBanner(runState),
            RuntimeFreshnessBanner = BuildRuntimeFreshnessBanner(runState, runtimeAssessment),
            RuntimeEntryBanner = BuildRuntimeEntryBanner(runState),
            RuntimeActivationHandoffBanner = BuildRuntimeActivationHandoffBanner(runState),
            RuntimePhaseBanner = BuildRuntimePhaseBanner(runState, runtimeAssessment),
            RuntimeCurrentStepBanner = BuildRuntimeCurrentStepBanner(runState, runtimeAssessment),
            RuntimeLatestStepBanner = BuildRuntimeLatestStepBanner(runState, runtimeAssessment),
            RuntimeLastCompletedStepBanner = BuildRuntimeLastCompletedStepBanner(runState, runtimeAssessment),
            RuntimeNextStepBanner = BuildRuntimeNextStepBanner(runState, runtimeAssessment),
            RuntimeRecentActivityText = BuildRuntimeRecentActivityText(runState, runtimeAssessment),
            RuntimeProgressBanner = BuildRuntimeProgressBanner(runState),
            RuntimeLastResultBanner = BuildRuntimeLastResultBanner(runState),
            RuntimeBlockerOriginBanner = BuildRuntimeBlockerOriginBanner(runState, runtimeAssessment),
            RuntimeSummaryBanner = BuildRuntimeSummaryBanner(runState, runtimeAssessment, visibleSummary),
            RuntimeSummaryModeBanner = BuildRuntimeSummaryModeBanner(runState, runtimeAssessment, visibleSummaryPacket),
            RuntimeSummaryText = BuildRuntimeSummaryText(runState, runtimeAssessment, visibleSummaryPacket),
            RuntimeRawSummaryText = BuildRuntimeRawSummaryText(runState, runtimeAssessment, visibleSummary),
            VisibleTerminalSummary = visibleSummary,
            VisibleOperatorSummaryPacket = visibleSummaryPacket,
            RuntimeBaselineBanner = BuildRuntimeBaselineBanner(runState),
            RuntimeBuildProfileBanner = BuildRuntimeBuildProfileBanner(runState, runtimeAssessment),
            RuntimeDecompositionBanner = BuildRuntimeDecompositionBanner(runState, runtimeAssessment),
            RuntimeExecutionWiringBanner = BuildRuntimeExecutionWiringBanner(runState, runtimeAssessment),
            RuntimeChainDepthBanner = BuildRuntimeChainDepthBanner(runState, runtimeAssessment),
            RuntimeExecutionTraceText = BuildRuntimeExecutionTraceText(runState, runtimeAssessment),
            RuntimeMutationProofBanner = BuildRuntimeMutationProofBanner(runState, runtimeAssessment),
            RuntimeRepairBanner = BuildRuntimeRepairBanner(runState, runtimeAssessment),
            RuntimeGenerationGuardrailBanner = BuildRuntimeGenerationGuardrailBanner(runState, runtimeAssessment),
            RuntimeProjectAttachBanner = BuildRuntimeProjectAttachBanner(runState, runtimeAssessment),
            RuntimeProjectReferenceBanner = BuildRuntimeProjectReferenceBanner(runState, runtimeAssessment),
            RuntimeHeadingPolicyBanner = BuildRuntimeHeadingPolicyBanner(runState, runtimeAssessment),
            RuntimeWorkFamilyBanner = BuildRuntimeWorkFamilyBanner(runState, runtimeAssessment),
            RuntimeCoverageBanner = BuildRuntimeCoverageBanner(runState, runtimeAssessment),
            RuntimeLaneBanner = BuildRuntimeLaneBanner(runState, runtimeAssessment),
            RuntimeLaneBlockerBanner = BuildRuntimeLaneBlockerBanner(runState, runtimeAssessment),
            RuntimeGoalBanner = BuildRuntimeGoalBanner(runState, runtimeAssessment),
            RuntimeGoalBlockerBanner = BuildRuntimeGoalBlockerBanner(runState, runtimeAssessment),
            ActionAvailabilityBanner = BuildActionAvailabilityBanner(
                canPromoteSelected,
                canRunActivePlan,
                canRunActivePlanViaActivationHandoff,
                selectedBatch is not null,
                canArchiveSelected,
                canDeleteSelected,
                allImports.Any(importRecord => importRecord.State == TaskboardImportState.Rejected),
                allImports.Any(importRecord => importRecord.State is not TaskboardImportState.ActivePlan and not TaskboardImportState.Deleted)),
            Batches = batchProjections,
            CanPromoteSelected = canPromoteSelected,
            CanArchiveSelected = canArchiveSelected,
            CanDeleteSelected = canDeleteSelected,
            CanRunActivePlan = canRunActivePlan,
            CanRunActivePlanViaActivationHandoff = canRunActivePlanViaActivationHandoff,
            CanRunSelectedBatch = activeImport is not null && selectedBatch is not null,
            CanClearRejected = allImports.Any(importRecord => importRecord.State == TaskboardImportState.Rejected),
            CanClearInactive = allImports.Any(importRecord => importRecord.State is not TaskboardImportState.ActivePlan and not TaskboardImportState.Deleted)
        };
    }

    public string BuildBatchDetails(TaskboardBatch batch)
    {
        var lines = new List<string>
        {
            $"Batch {batch.BatchNumber}: {batch.Title}"
        };

        AppendSectionContent(lines, batch.Content);

        if (batch.Steps.Count > 0)
        {
            lines.Add("");
            lines.Add("Steps:");
            foreach (var step in batch.Steps)
            {
                lines.Add($"{step.Ordinal}. {step.Title}");
                AppendSectionContent(lines, step.Content, "   ");
            }
        }

        return string.Join(Environment.NewLine, lines.Where(line => line is not null));
    }

    public string BuildActivePlanSummary(TaskboardProjection projection)
    {
        if (projection.ActiveImport is null || projection.ActiveDocument is null)
            return "No active taskboard plan is currently promoted for this workspace.";

        var lines = new List<string>
        {
            $"Active plan: {projection.ActiveDocument.Title}",
            $"Objective: {projection.ObjectiveSummary}",
            $"Validation: {projection.ValidationBanner}",
            $"Batches: {projection.Batches.Count}"
        };

        if (projection.Batches.Count > 0)
        {
            lines.Add("Batch order:");
            foreach (var batch in projection.Batches)
            {
                var runtime = string.IsNullOrWhiteSpace(batch.RuntimeStatus) ? "" : $" [{batch.RuntimeStatus}]";
                lines.Add($"- {batch.DisplayLabel}{runtime}");
            }
        }

        if (!string.IsNullOrWhiteSpace(projection.RuntimeStatusBanner))
        {
            lines.Add(projection.RuntimeStatusBanner);
            lines.Add(projection.RuntimeFreshnessBanner);
            lines.Add(projection.RuntimeEntryBanner);
            lines.Add(projection.RuntimeActivationHandoffBanner);
            lines.Add(projection.RuntimePhaseBanner);
            lines.Add(projection.RuntimeCurrentStepBanner);
            lines.Add(projection.RuntimeLatestStepBanner);
            lines.Add(projection.RuntimeLastCompletedStepBanner);
            lines.Add(projection.RuntimeNextStepBanner);
            lines.Add(projection.RuntimeProgressBanner);
            lines.Add(projection.RuntimeLastResultBanner);
            lines.Add(projection.RuntimeBlockerOriginBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeSummaryBanner))
                lines.Add(projection.RuntimeSummaryBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeSummaryModeBanner))
                lines.Add(projection.RuntimeSummaryModeBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeSummaryText))
                lines.Add(projection.RuntimeSummaryText);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeBaselineBanner))
                lines.Add(projection.RuntimeBaselineBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeBuildProfileBanner))
                lines.Add(projection.RuntimeBuildProfileBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeDecompositionBanner))
                lines.Add(projection.RuntimeDecompositionBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeExecutionWiringBanner))
                lines.Add(projection.RuntimeExecutionWiringBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeChainDepthBanner))
                lines.Add(projection.RuntimeChainDepthBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeExecutionTraceText))
                lines.Add(projection.RuntimeExecutionTraceText);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeMutationProofBanner))
                lines.Add(projection.RuntimeMutationProofBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeRepairBanner))
                lines.Add(projection.RuntimeRepairBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeGenerationGuardrailBanner))
                lines.Add(projection.RuntimeGenerationGuardrailBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeProjectAttachBanner))
                lines.Add(projection.RuntimeProjectAttachBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeProjectReferenceBanner))
                lines.Add(projection.RuntimeProjectReferenceBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeHeadingPolicyBanner))
                lines.Add(projection.RuntimeHeadingPolicyBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeWorkFamilyBanner))
                lines.Add(projection.RuntimeWorkFamilyBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeCoverageBanner))
                lines.Add(projection.RuntimeCoverageBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeLaneBanner))
                lines.Add(projection.RuntimeLaneBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeLaneBlockerBanner))
                lines.Add(projection.RuntimeLaneBlockerBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeGoalBanner))
                lines.Add(projection.RuntimeGoalBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeGoalBlockerBanner))
                lines.Add(projection.RuntimeGoalBlockerBanner);
            if (!string.IsNullOrWhiteSpace(projection.RuntimeRecentActivityText))
            {
                lines.Add("Recent activity:");
                lines.Add(projection.RuntimeRecentActivityText);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    public string BuildNextBatchItemsSummary(TaskboardProjection projection)
    {
        if (projection.ActiveDocument is null || projection.ActiveDocument.Batches.Count == 0)
            return "No active parsed taskboard batch is available.";

        var batch = projection.RunState is not null
            ? ResolveNextRunnableBatch(projection.ActiveDocument, projection.RunState)
            : projection.ActiveDocument.Batches.OrderBy(current => current.BatchNumber).First();
        var lines = new List<string>
        {
            $"Next ready batch: Batch {batch.BatchNumber} — {batch.Title}"
        };

        if (batch.Steps.Count == 0)
        {
            lines.Add("This batch has no parsed H3 work items yet.");
            AppendSectionContent(lines, batch.Content);
            return string.Join(Environment.NewLine, lines);
        }

        lines.Add("Ready work items:");
        if (projection.RunState is not null && !projection.IsRuntimeSnapshotStale)
        {
            var runtimeBatch = projection.RunState.Batches.FirstOrDefault(current =>
                string.Equals(current.BatchId, batch.BatchId, StringComparison.OrdinalIgnoreCase));
            if (runtimeBatch is not null)
            {
                foreach (var item in runtimeBatch.WorkItems.Where(current => current.Status == TaskboardWorkItemRuntimeStatus.Pending))
                {
                    lines.Add($"- {FirstNonEmpty(item.DisplayOrdinal, item.Ordinal.ToString())}. {item.Title}");
                }

                return string.Join(Environment.NewLine, lines);
            }
        }

        foreach (var step in batch.Steps)
            lines.Add($"- {step.Ordinal}. {step.Title}");

        return string.Join(Environment.NewLine, lines);
    }

    public string BuildValidationSummary(TaskboardImportRecord? importRecord, TaskboardValidationReport? report)
    {
        if (importRecord is null)
            return "No taskboard import is selected.";

        if (report is null)
            return $"No validation report is stored for `{importRecord.Title}`.";

        var lines = new List<string>
        {
            $"Validation outcome: {report.Outcome}",
            $"State: {importRecord.State}",
            $"Errors: {report.Errors.Count}",
            $"Warnings: {report.Warnings.Count}"
        };

        if (report.Errors.Count > 0)
        {
            lines.Add("Validation errors:");
            foreach (var error in report.Errors.Take(8))
                lines.Add(FormatValidationMessage(error));
        }

        if (report.Warnings.Count > 0)
        {
            lines.Add("Validation warnings:");
            foreach (var warning in report.Warnings.Take(8))
                lines.Add(FormatValidationMessage(warning));
        }

        if ((report.Errors.Count > 0 || report.Warnings.Count > 0) && !string.IsNullOrWhiteSpace(report.CanonicalFormHint))
        {
            lines.Add("Canonical form:");
            lines.Add($"- {report.CanonicalFormHint}");
            foreach (var example in report.CanonicalExamples.Take(8))
                lines.Add($"- {example}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatValidationMessage(TaskboardValidationMessage message)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(message.Code))
            parts.Add($"[{message.Code}]");
        if (message.LineNumber > 0)
            parts.Add($"line {message.LineNumber}");
        if (!string.IsNullOrWhiteSpace(message.LineClassification))
            parts.Add($"class={message.LineClassification}");

        var prefix = parts.Count == 0 ? "-" : $"- {string.Join(" ", parts)}:";
        var detail = message.Message;
        if (!string.IsNullOrWhiteSpace(message.OffendingText))
            detail += $" Offending text: `{message.OffendingText}`.";
        if (!string.IsNullOrWhiteSpace(message.ExpectedGrammar))
            detail += $" Expected: {message.ExpectedGrammar}";

        return $"{prefix} {detail}".Trim();
    }

    private string BuildRuntimeSummaryBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment, TaskboardRunTerminalSummaryRecord? visibleSummary)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible && visibleSummary is null)
        {
            return $"Run summary: stale cached summary invalidated ({FirstNonEmpty(assessment.InvalidationReason, assessment.Summary)}).";
        }

        if (visibleSummary is null || string.IsNullOrWhiteSpace(visibleSummary.SummaryId))
        {
            return runState is null
                ? "Run summary: (none)"
                : "Run summary: available after the current run reaches a terminal state.";
        }

        var persistedSuffix = assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible
            ? " [persisted]"
            : "";
        return $"Run summary: {FirstNonEmpty(visibleSummary.FinalStatus, "(none)")} ({FirstNonEmpty(visibleSummary.TerminalCategory, "(none)")}){persistedSuffix}";
    }

    private string BuildRuntimeSummaryModeBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment, TaskboardOperatorSummaryPacket? packet)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible && packet is null)
            return "Summary mode: (pending)";

        return packet is null || string.IsNullOrWhiteSpace(packet.SummaryId)
            ? runState is null
                ? "Summary mode: (none)"
                : "Summary mode: operator summary (pending terminal state)"
            : "Summary mode: operator summary (deterministic packet)";
    }

    private string BuildRuntimeSummaryText(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment, TaskboardOperatorSummaryPacket? packet)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible && packet is null)
            return "";

        return _operatorSummaryService.BuildDeterministicSummaryText(packet);
    }

    private string BuildRuntimeRawSummaryText(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment, TaskboardRunTerminalSummaryRecord? visibleSummary)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible && visibleSummary is null)
            return "";

        return _runSummaryService.BuildSummaryText(visibleSummary);
    }

    private static TaskboardRunTerminalSummaryRecord? ResolveVisibleTerminalSummary(
        TaskboardPlanRunStateRecord? runState,
        TaskboardRunTerminalSummaryRecord? persistedSummary)
    {
        if (runState?.LastTerminalSummary is { SummaryId.Length: > 0 })
            return runState.LastTerminalSummary;

        if (ShouldSuppressPersistedTerminalSummary(runState, persistedSummary))
            return null;

        return persistedSummary is { SummaryId.Length: > 0 }
            ? persistedSummary
            : null;
    }

    private static bool ShouldSuppressPersistedTerminalSummary(
        TaskboardPlanRunStateRecord? runState,
        TaskboardRunTerminalSummaryRecord? persistedSummary)
    {
        if (runState is null || persistedSummary is null || string.IsNullOrWhiteSpace(persistedSummary.SummaryId))
            return false;

        if (runState.PlanStatus is TaskboardPlanRuntimeStatus.Completed
            or TaskboardPlanRuntimeStatus.Blocked
            or TaskboardPlanRuntimeStatus.Failed
            or TaskboardPlanRuntimeStatus.PausedManualOnly)
        {
            return false;
        }

        var runUpdatedUtc = ParseUtc(runState.UpdatedUtc);
        var summaryEndedUtc = ParseUtc(FirstNonEmpty(
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

    private static List<TaskboardBatchProjection> BuildBatchProjections(TaskboardDocument? document, TaskboardPlanRunStateRecord? runState)
    {
        if (document is null)
            return [];

        return document.Batches
            .OrderBy(batch => batch.BatchNumber)
            .Select(batch => new TaskboardBatchProjection
            {
                BatchId = batch.BatchId,
                BatchNumber = batch.BatchNumber,
                Title = batch.Title,
                DisplayLabel = BuildBatchDisplayLabel(batch, runState),
                StepCount = batch.Steps.Count,
                RuntimeStatus = GetBatchRuntimeStatus(runState, batch.BatchId),
                DetailsText = BuildBatchDetailsStatic(batch, runState)
            })
            .ToList();
    }

    private static string BuildObjectiveSummary(TaskboardDocument? document)
    {
        if (document is null || string.IsNullOrWhiteSpace(document.ObjectiveText))
            return "(missing objective)";

        var objective = document.ObjectiveText.Replace(Environment.NewLine, " ").Trim();
        return objective.Length <= 220
            ? objective
            : objective[..220] + "...";
    }

    private static string BuildValidationBanner(TaskboardImportRecord? importRecord, TaskboardValidationReport? report)
    {
        if (importRecord is null)
            return "No taskboard imported";

        var validationText = report is null
            ? importRecord.ValidationOutcome.ToString()
            : report.Outcome.ToString();
        return $"state={importRecord.State.ToString().ToLowerInvariant()} validation={validationText.ToLowerInvariant()}";
    }

    private static TaskboardImportRecord? ResolveSelectedImport(
        IReadOnlyList<TaskboardImportRecord> imports,
        TaskboardImportRecord? activeImport,
        string? selectedImportId)
    {
        return imports.FirstOrDefault(record => string.Equals(record.ImportId, selectedImportId, StringComparison.OrdinalIgnoreCase))
            ?? activeImport
            ?? imports.FirstOrDefault();
    }

    private static TaskboardBatchProjection? ResolveSelectedBatch(IReadOnlyList<TaskboardBatchProjection> batches, string? selectedBatchId)
    {
        return batches.FirstOrDefault(batch => string.Equals(batch.BatchId, selectedBatchId, StringComparison.OrdinalIgnoreCase))
            ?? batches.FirstOrDefault();
    }

    private static string BuildSelectedStatusBanner(TaskboardImportRecord? selectedImport, TaskboardImportRecord? activeImport, bool canRunActivePlanViaActivationHandoff)
    {
        if (selectedImport is null)
            return "No taskboard import is selected.";

        if (selectedImport.State == TaskboardImportState.ActivePlan)
            return $"Selected import `{selectedImport.Title}` is already the active plan.";

        if (selectedImport.State == TaskboardImportState.ReadyForPromotion)
        {
            if (activeImport is not null && !string.Equals(activeImport.ImportId, selectedImport.ImportId, StringComparison.OrdinalIgnoreCase))
            {
                return $"Selected import `{selectedImport.Title}` is ready for activation, but active plan `{activeImport.Title}` remains the current run target.";
            }

            if (canRunActivePlanViaActivationHandoff)
            {
                return $"Selected import `{selectedImport.Title}` is ready for activation. Run Active Plan will activate it first.";
            }

            return $"Selected import `{selectedImport.Title}` is ready for activation.";
        }

        return selectedImport.State switch
        {
            TaskboardImportState.Rejected => $"Selected import `{selectedImport.Title}` is rejected and cannot be activated.",
            TaskboardImportState.Archived => $"Selected import `{selectedImport.Title}` is archived.",
            TaskboardImportState.Deleted => $"Selected import `{selectedImport.Title}` was deleted.",
            _ => $"Selected import `{selectedImport.Title}` is in state {selectedImport.State.ToString().ToLowerInvariant()}."
        };
    }

    private static string BuildRunTargetBanner(TaskboardImportRecord? selectedImport, TaskboardImportRecord? activeImport, bool canRunActivePlanViaActivationHandoff)
    {
        if (canRunActivePlanViaActivationHandoff && selectedImport is not null)
        {
            return $"Run target: `Run Active Plan` will activate `{selectedImport.Title}` first, then start auto-run.";
        }

        if (activeImport is not null)
        {
            if (selectedImport is not null
                && !string.Equals(selectedImport.ImportId, activeImport.ImportId, StringComparison.OrdinalIgnoreCase))
            {
                return $"Run target: `Run Active Plan` will execute active plan `{activeImport.Title}`. Selected import `{selectedImport.Title}` is not active yet.";
            }

            return $"Run target: `Run Active Plan` will execute active plan `{activeImport.Title}`.";
        }

        return "Run target: no active plan is currently runnable.";
    }

    private static string BuildActionAvailabilityBanner(
        bool canPromoteSelected,
        bool canRunActivePlan,
        bool canRunActivePlanViaActivationHandoff,
        bool canRunSelectedBatch,
        bool canArchiveSelected,
        bool canDeleteSelected,
        bool canClearRejected,
        bool canClearInactive)
    {
        var actions = new List<string>();
        if (canPromoteSelected)
            actions.Add("activate selected");
        if (canRunActivePlan)
            actions.Add(canRunActivePlanViaActivationHandoff ? "run active plan (activates selected first)" : "run active plan");
        if (canRunSelectedBatch)
            actions.Add("run selected batch");
        if (canArchiveSelected)
            actions.Add("archive selected");
        if (canDeleteSelected)
            actions.Add("delete selected");
        if (canClearRejected)
            actions.Add("clear rejected");
        if (canClearInactive)
            actions.Add("clear inactive");

        return actions.Count == 0
            ? "No taskboard actions are currently ready."
            : "Ready actions: " + string.Join(", ", actions) + ".";
    }

    private static string BuildBatchDetailsStatic(TaskboardBatch batch, TaskboardPlanRunStateRecord? runState)
    {
        var lines = new List<string>();
        var runtimeStatus = GetBatchRuntimeStatus(runState, batch.BatchId);
        if (!string.IsNullOrWhiteSpace(runtimeStatus))
        {
            lines.Add($"Runtime: {runtimeStatus}");
            lines.Add("");
        }

        var runtimeBatch = runState?.Batches.FirstOrDefault(current =>
            string.Equals(current.BatchId, batch.BatchId, StringComparison.OrdinalIgnoreCase));
        if (runtimeBatch is not null && runtimeBatch.WorkItems.Count > 0)
        {
            lines.Add("Steps:");
            foreach (var workItem in runtimeBatch.WorkItems)
            {
                var stepStatus = workItem.Status.ToString().ToLowerInvariant();
                var statusSuffix = string.IsNullOrWhiteSpace(stepStatus) ? "" : $" [{stepStatus}]";
                var sourceSuffix = workItem.IsDecomposedItem && !string.IsNullOrWhiteSpace(workItem.SourceWorkItemId)
                    ? $" [from {workItem.SourceWorkItemId}]"
                    : "";
                var templateSuffix = !string.IsNullOrWhiteSpace(workItem.TemplateId)
                    ? $" [template {workItem.TemplateId}]"
                    : !string.IsNullOrWhiteSpace(workItem.PhraseFamily)
                        ? $" [phrase {workItem.PhraseFamily}]"
                        : "";
                var familySuffix = string.IsNullOrWhiteSpace(workItem.WorkFamily)
                    ? ""
                    : $" [family {workItem.WorkFamily}]";
                var laneSuffix = workItem.LastExecutionGoalResolution.LaneResolution.LaneKind == TaskboardExecutionLaneKind.Unknown
                    ? ""
                    : $" [lane {workItem.LastExecutionGoalResolution.LaneResolution.LaneKind.ToString().ToLowerInvariant()} -> {FirstNonEmpty(workItem.LastExecutionGoalResolution.LaneResolution.SelectedChainTemplateId, workItem.LastExecutionGoalResolution.LaneResolution.SelectedToolId, workItem.LastExecutionGoalResolution.LaneResolution.Blocker.Code.ToString().ToLowerInvariant())}]";
                var goalSuffix = workItem.LastExecutionGoalResolution.GoalKind == TaskboardExecutionGoalKind.Unknown
                    ? ""
                    : $" [goal {workItem.LastExecutionGoalResolution.GoalKind.ToString().ToLowerInvariant()} -> {FirstNonEmpty(workItem.LastExecutionGoalResolution.Goal.SelectedChainTemplateId, workItem.LastExecutionGoalResolution.Goal.SelectedToolId, workItem.LastExecutionGoalResolution.Blocker.Code.ToString().ToLowerInvariant())}]";
                lines.Add($"{FirstNonEmpty(workItem.DisplayOrdinal, workItem.Ordinal.ToString())}. {workItem.Title}{statusSuffix}{sourceSuffix}{templateSuffix}{familySuffix}{laneSuffix}{goalSuffix}");
            }

            return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
        }

        if (batch.Steps.Count == 0)
            AppendSectionContent(lines, batch.Content);

        if (batch.Steps.Count > 0)
        {
            lines.Add("Steps:");
            foreach (var step in batch.Steps)
            {
                var stepStatus = GetWorkItemRuntimeStatus(runState, batch.BatchId, step.StepId);
                var statusSuffix = string.IsNullOrWhiteSpace(stepStatus) ? "" : $" [{stepStatus}]";
                lines.Add($"{step.Ordinal}. {step.Title}{statusSuffix}");
            }
        }

        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static void AppendSectionContent(List<string> lines, TaskboardSectionContent content, string prefix = "")
    {
        foreach (var paragraph in content.Paragraphs)
            lines.Add(prefix + paragraph);
        foreach (var item in content.BulletItems)
            lines.Add(prefix + "- " + item);
        foreach (var item in content.NumberedItems)
            lines.Add(prefix + "- " + item);
        foreach (var subsection in content.Subsections)
        {
            lines.Add(prefix + subsection.Title);
            AppendSectionContent(lines, subsection, prefix + "  ");
        }
    }

    private static string BuildRuntimeStatusBanner(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is null)
            return "Runtime: no run state recorded.";

        var status = runState.PlanStatus switch
        {
            TaskboardPlanRuntimeStatus.PausedManualOnly => "paused_manual_only",
            _ => runState.PlanStatus.ToString().ToLowerInvariant()
        };
        return $"Runtime: {status}.";
    }

    private static string BuildRuntimeFreshnessBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (runState is null)
            return "Runtime snapshot: (none)";

        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
        {
            return $"Runtime snapshot: stale_cached_snapshot reason={FirstNonEmpty(assessment.InvalidationReason, assessment.Summary)} cached_version={FirstNonEmpty(assessment.CachedVersion, "(none)")} current_version={FirstNonEmpty(assessment.CurrentVersion, "(none)")}.";
        }

        return $"Runtime snapshot: {FirstNonEmpty(runState.RuntimeStateStatusCode, "fresh_runtime_snapshot")} version={FirstNonEmpty(runState.RuntimeStateVersion, "(none)")} resolver_contract={TaskboardRuntimeStateFingerprintService.CurrentResolverContractVersion} computed_utc={FirstNonEmpty(runState.RuntimeStateComputedUtc, "(none)")}.";
    }

    private static string BuildRuntimeEntryBanner(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is null || string.IsNullOrWhiteSpace(runState.LastRunEntryAction))
            return "Run entry: (none)";

        return $"Run entry: action={runState.LastRunEntryAction} path={FirstNonEmpty(runState.LastRunEntryPath, "(none)")} selected={FirstNonEmpty(runState.LastRunEntrySelectedImportTitle, "(none)")} state={FirstNonEmpty(runState.LastRunEntrySelectedState, "(none)")}.";
    }

    private static string BuildRuntimeActivationHandoffBanner(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is null || string.IsNullOrWhiteSpace(runState.LastActivationHandoffSummary))
            return "Activation handoff: (none)";

        return $"Activation handoff: {runState.LastActivationHandoffSummary}";
    }

    private static string BuildRuntimePhaseBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (runState is null)
            return "Current phase: (none)";

        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return $"Current phase: stale cached snapshot invalidated ({FirstNonEmpty(assessment.InvalidationReason, assessment.Summary)}).";

        return string.IsNullOrWhiteSpace(runState.CurrentRunPhaseText)
            ? "Current phase: idle."
            : $"Current phase: {runState.CurrentRunPhaseText}";
    }

    private static string BuildRuntimeCurrentStepBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (runState is null)
            return "Current step: (none)";

        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return "Current step: stale cached snapshot invalidated; rerun active plan to recompute the live step.";

        var currentBatch = runState.Batches.FirstOrDefault(batch =>
            string.Equals(batch.BatchId, runState.CurrentBatchId, StringComparison.OrdinalIgnoreCase));
        var currentItem = currentBatch?.WorkItems.FirstOrDefault(item =>
            string.Equals(item.WorkItemId, runState.CurrentWorkItemId, StringComparison.OrdinalIgnoreCase));
        if (currentBatch is null || currentItem is null)
            return "Current step: (none)";

        return $"Current step: Batch {currentBatch.BatchNumber} / Work Item {FirstNonEmpty(currentItem.DisplayOrdinal, currentItem.Ordinal.ToString())} — {currentItem.Title}";
    }

    private static string BuildRuntimeLatestStepBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (runState is null)
            return "Latest step: (none)";

        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return "Latest step: stale cached snapshot invalidated; latest live step will appear after recomputation.";

        return string.IsNullOrWhiteSpace(runState.LatestStepSummary)
            ? "Latest step: (none)"
            : $"Latest step: {runState.LatestStepSummary}";
    }

    private static string BuildRuntimeLastCompletedStepBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (runState is null)
            return "Last completed step: (none)";

        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return "Last completed step: stale cached snapshot invalidated.";

        var summary = FirstNonEmpty(runState.LastCompletedStepSummary, runState.LastCompletedWorkItemTitle);
        return string.IsNullOrWhiteSpace(summary)
            ? "Last completed step: (none)"
            : $"Last completed step: {summary}";
    }

    private static string BuildRuntimeNextStepBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (runState is null)
            return "Next unresolved work: (none)";

        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return "Next unresolved work: stale cached snapshot invalidated; current follow-up work will be recomputed on the next live run.";

        if (string.IsNullOrWhiteSpace(runState.LastFollowupWorkItemTitle))
            return "Next unresolved work: (none)";

        var failureContext = BuildFailureContextSuffix(runState, includeRepairContext: false);
        return $"Next unresolved work: {runState.LastFollowupWorkItemTitle} family={FirstNonEmpty(runState.LastFollowupWorkFamily, "(none)")} phrase_family={FormatFollowupValue(runState.LastFollowupPhraseFamily, runState.LastFollowupPhraseFamilyReasonCode)} operation_kind={FormatFollowupValue(runState.LastFollowupOperationKind, runState.LastFollowupOperationKindReasonCode)} stack={FormatFollowupValue(runState.LastFollowupStackFamily, runState.LastFollowupStackFamilyReasonCode)} reason={FirstNonEmpty(runState.LastFollowupSelectionReason, "(none)")}{failureContext}";
    }

    private static string BuildRuntimeRecentActivityText(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (runState is null)
            return "No recent activity recorded.";

        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return $"Stale cached snapshot invalidated: {FirstNonEmpty(assessment.InvalidationReason, assessment.Summary)}";

        if (runState.Events.Count == 0)
            return "No recent activity recorded.";

        var lines = runState.Events
            .TakeLast(10)
            .Reverse()
            .Select(BuildRecentActivityLine)
            .ToList();
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildRuntimeProgressBanner(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is null)
            return "Progress: (none)";

        var skipSuffix = runState.SatisfactionSkipCount > 0
            ? $" skipped={runState.SatisfactionSkipCount}"
            : "";
        var currentBatch = runState.Batches.FirstOrDefault(batch =>
            string.Equals(batch.BatchId, runState.CurrentBatchId, StringComparison.OrdinalIgnoreCase));
        var currentItem = currentBatch?.WorkItems.FirstOrDefault(item =>
            string.Equals(item.WorkItemId, runState.CurrentWorkItemId, StringComparison.OrdinalIgnoreCase));
        if (currentBatch is not null && currentItem is not null)
        {
            return $"Progress: Batch {currentBatch.BatchNumber} / Work Item {FirstNonEmpty(currentItem.DisplayOrdinal, currentItem.Ordinal.ToString())} running. Completed {runState.CompletedWorkItemCount}/{runState.TotalWorkItemCount}{skipSuffix}.";
        }

        return $"Progress: completed {runState.CompletedWorkItemCount}/{runState.TotalWorkItemCount} work item(s){skipSuffix}.";
    }

    private static string BuildRuntimeLastResultBanner(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is null)
            return "Last result: (none)";

        var details = string.Equals(runState.LastResultKind, "state_already_satisfied", StringComparison.OrdinalIgnoreCase)
            ? FirstNonEmpty(runState.LastSatisfactionSkipEvidenceSummary, runState.LastResultSummary)
            : FirstNonEmpty(runState.LastBlockerReason, runState.LastResultSummary);
        var failureContext = BuildFailureContextSuffix(runState, includeRepairContext: false);
        return string.IsNullOrWhiteSpace(details)
            ? "Last result: (none)"
            : $"Last result: {FirstNonEmpty(runState.LastResultKind, "unknown")} — {details}{failureContext}";
    }

    private static string BuildRuntimeBlockerOriginBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (runState is null)
            return "Blocker origin: (none)";

        if (assessment is not null
            && assessment.HasSnapshot
            && !assessment.IsCompatible
            && (!string.IsNullOrWhiteSpace(runState.LastBlockerReason)
                || runState.LastExecutionGoalResolution.Blocker.Code != TaskboardExecutionGoalBlockerCode.None
                || runState.LastExecutionGoalResolution.LaneResolution.Blocker.Code != TaskboardExecutionLaneBlockerCode.None))
        {
            return $"Blocker origin: stale_cached_blocker reason={FirstNonEmpty(assessment.InvalidationReason, assessment.Summary)}.";
        }

        if (string.IsNullOrWhiteSpace(runState.LastBlockerOrigin))
        {
            return string.IsNullOrWhiteSpace(runState.LastPostChainReconciliationSummary)
                ? "Blocker origin: (none)"
                : $"Blocker origin: (none) post_chain={runState.LastPostChainReconciliationSummary}.";
        }

        return $"Blocker origin: {runState.LastBlockerOrigin} work_item={FirstNonEmpty(runState.LastBlockerWorkItemId, "(none)")} title={FirstNonEmpty(runState.LastBlockerWorkItemTitle, "(none)")} phase={FirstNonEmpty(runState.LastBlockerPhase, "(none)")} generation={runState.LastBlockerGeneration} family={FirstNonEmpty(runState.LastBlockerWorkFamily, "(none)")} phrase_family={FirstNonEmpty(runState.LastBlockerPhraseFamily, "(none)")} operation_kind={FirstNonEmpty(runState.LastBlockerOperationKind, "(none)")} stack={FirstNonEmpty(runState.LastBlockerStackFamily, "(none)")}{BuildFailureContextSuffix(runState, includeRepairContext: true)}.";
    }

    private static string BuildRuntimeBuildProfileBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return "Build profile: stale snapshot invalidated; current build profile will be recomputed on the next live run.";

        if (runState?.LastResolvedBuildProfile is null
            || runState.LastResolvedBuildProfile.Status == TaskboardBuildProfileResolutionStatus.Unknown)
        {
            return "Build profile: (none)";
        }

        var profile = runState.LastResolvedBuildProfile;
        return $"Build profile: {FormatStackFamily(profile.StackFamily)} confidence={profile.Confidence.ToString().ToLowerInvariant()}";
    }

    private static string BuildRuntimeBaselineBanner(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is null)
            return "Baseline: (none)";

        var summary = FirstNonEmpty(runState.LastMaintenanceBaselineSummary);
        if (string.IsNullOrWhiteSpace(summary))
            return "Baseline: (none)";

        if (!string.IsNullOrWhiteSpace(runState.LastMaintenanceGuardSummary))
            return $"{summary}{Environment.NewLine}Guard: {runState.LastMaintenanceGuardSummary}";

        return summary;
    }

    private static string BuildRuntimeDecompositionBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
        {
            return $"Decomposition: stale snapshot invalidated; phrase-family and template data will be recomputed on the next live run ({FirstNonEmpty(assessment.InvalidationReason, assessment.Summary)}).";
        }

        return string.IsNullOrWhiteSpace(runState?.LastDecompositionSummary)
            ? "Decomposition: (none)"
            : $"Decomposition: {runState.LastDecompositionSummary} raw={FirstNonEmpty(runState.LastPhraseFamilyRawPhraseText, "(none)")} normalized={FirstNonEmpty(runState.LastPhraseFamilyNormalizedPhraseText, "(none)")} canonical_operation={FirstNonEmpty(runState.LastCanonicalOperationKind, "(none)")} canonical_target={FirstNonEmpty(runState.LastCanonicalTargetPath, "(none)")} canonical_project={FirstNonEmpty(runState.LastCanonicalProjectName, "(none)")} canonical_template={FirstNonEmpty(runState.LastCanonicalTemplateHint, "(none)")} canonical_role={FirstNonEmpty(runState.LastCanonicalRoleHint, "(none)")} closest={FirstNonEmpty(runState.LastPhraseFamilyClosestKnownFamilyGroup, "(none)")} phrase_family={FirstNonEmpty(runState.LastPhraseFamily, "(none)")} source={FirstNonEmpty(runState.LastPhraseFamilySource, "(none)")} candidates={FormatList(runState.LastPhraseFamilyCandidates)} deterministic={FirstNonEmpty(runState.LastPhraseFamilyDeterministicCandidate, "(none)")} advisory={FirstNonEmpty(runState.LastPhraseFamilyAdvisoryCandidate, "(none)")} tie_break={FirstNonEmpty(runState.LastPhraseFamilyTieBreakRuleId, "(none)")} tie_break_summary={FirstNonEmpty(runState.LastPhraseFamilyTieBreakSummary, "(none)")} blocker={FirstNonEmpty(runState.LastPhraseFamilyBlockerCode, "(none)")} terminal_stage={FirstNonEmpty(runState.LastPhraseFamilyTerminalResolverStage, "(none)")} builder_operation={FirstNonEmpty(runState.LastPhraseFamilyBuilderOperationStatus, "(none)")} lane_resolution={FirstNonEmpty(runState.LastPhraseFamilyLaneResolutionStatus, "(none)")} trace={FirstNonEmpty(runState.LastPhraseFamilyResolutionPathTrace, "(none)")} canonical_trace={FirstNonEmpty(runState.LastCanonicalizationTrace, "(none)")} resolution={FirstNonEmpty(runState.LastPhraseFamilyResolutionSummary, "(none)")} template={FirstNonEmpty(runState.LastTemplateId, "(none)")}";
    }

    private static string BuildRuntimeExecutionWiringBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return "Execution wiring: stale snapshot invalidated; rerun active plan to observe the current tool and chain selection.";

        if (runState is null)
            return "Execution wiring: (none)";

        var plannedTool = FirstMeaningful(runState.LastPlannedToolName, runState.LastExecutionGoalResolution?.Goal.SelectedToolId, "(none)");
        var plannedChain = FirstMeaningful(runState.LastPlannedChainTemplateId, runState.LastExecutionGoalResolution?.Goal.SelectedChainTemplateId, "(none)");
        var lastExecutedTool = FirstMeaningful(runState.LastObservedToolName, "(none)");
        var lastExecutedChain = FirstMeaningful(runState.LastObservedChainTemplateId, "(none)");
        var recentTools = runState.RecentObservedToolNames.Count == 0
            ? FirstMeaningful(lastExecutedTool, plannedTool)
            : string.Join(">", runState.RecentObservedToolNames.TakeLast(6));
        var recentChains = runState.RecentObservedChainTemplateIds.Count == 0
            ? FirstMeaningful(lastExecutedChain, plannedChain)
            : string.Join(">", runState.RecentObservedChainTemplateIds.TakeLast(6));
        var route = ClassifyExecutionRoute(FirstMeaningful(lastExecutedTool, plannedTool));
        var reason = FirstMeaningful(
            runState.LastExecutionDecisionSummary,
            runState.LastExecutionGoalResolution?.ResolutionReason,
            runState.LatestStepSummary,
            "(none)");
        return $"Execution wiring: route={route} planned_tool={plannedTool} planned_chain={plannedChain} last_executed_tool={lastExecutedTool} last_executed_chain={lastExecutedChain} recent_tools={recentTools} recent_chains={recentChains} target_type={FirstMeaningful(runState.LastResolvedTargetFileType, "(none)")} target_role={FirstMeaningful(runState.LastResolvedTargetRole, "(none)")} target_project={FirstMeaningful(runState.LastResolvedTargetProjectName, "(none)")} target_namespace={FirstMeaningful(runState.LastResolvedTargetNamespaceHint, "(none)")} reason={reason}";
    }

    private string BuildRuntimeChainDepthBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return "Chain depth: stale snapshot invalidated; rerun active plan to rebuild current chain depth truth.";

        return _cSharpExecutionCoverageService.BuildRuntimeChainDepthBanner(runState);
    }

    private static string BuildRuntimeExecutionTraceText(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return "Execution trace: stale snapshot invalidated; rerun active plan to rebuild the exact tool-call trace.";

        if (runState is null || runState.ExecutedToolCalls.Count == 0)
            return "Execution trace: (none)";

        var entries = runState.ExecutedToolCalls
            .TakeLast(20)
            .Select(call =>
            {
                var tool = FirstMeaningful(call.ToolName, "(none)");
                var chain = FirstMeaningful(call.ChainTemplateId, "(none)");
                var stage = FirstMeaningful(call.Stage, "unknown");
                var result = string.IsNullOrWhiteSpace(call.ResultClassification)
                    ? ""
                    : $"[{call.ResultClassification}]";
                var touched = call.TouchedFilePaths.Count == 0
                    ? ""
                    : $"{{{string.Join(",", call.TouchedFilePaths.Take(3))}}}";
                var detail = string.Equals(call.ToolName, "add_dotnet_project_reference", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(call.Summary)
                    ? $"<{BoundTraceSegment(call.Summary, 220)}>"
                    : "";
                return $"{stage}:{tool}@{chain}{result}{touched}{detail}";
            });
        return $"Execution trace: {string.Join(" | ", entries)}";
    }

    private static string BuildRuntimeMutationProofBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return "Mutation proof: stale snapshot invalidated; rerun active plan to rebuild current mutation evidence.";

        if (runState is null)
            return "Mutation proof: (none)";

        if (string.IsNullOrWhiteSpace(runState.LastMutationToolName))
            return "Mutation proof: no workspace mutation has been recorded in the current run.";

        var files = runState.LastMutationTouchedFilePaths.Count == 0
            ? "(none)"
            : string.Join(", ", runState.LastMutationTouchedFilePaths);
        var verification = string.IsNullOrWhiteSpace(runState.LastVerificationAfterMutationOutcome)
            ? "verification_after_mutation=(none)"
            : $"verification_after_mutation={runState.LastVerificationAfterMutationOutcome}@{FirstMeaningful(runState.LastVerificationAfterMutationUtc, "(unknown)")}";
        return $"Mutation proof: tool={runState.LastMutationToolName} files={files} applied_at={FirstMeaningful(runState.LastMutationUtc, "(unknown)")} {verification}";
    }

    private static string BuildRuntimeRepairBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return "Repair continuation: stale snapshot invalidated; rerun active plan to rebuild current repair-continuation truth.";

        if (runState is null || string.IsNullOrWhiteSpace(runState.LastRepairTargetPath))
            return "Repair continuation: (none)";

        return $"Repair continuation: target={FirstMeaningful(runState.LastRepairTargetPath, "(none)")} targeting={FirstMeaningful(runState.LastRepairTargetingStrategy, "(none)")} targeting_summary={FirstMeaningful(runState.LastRepairTargetingSummary, "(none)")} draft_kind={FirstMeaningful(runState.LastRepairDraftKind, "(none)")} local_patch_available={runState.LastRepairLocalPatchAvailable.ToString().ToLowerInvariant()} symbol={FirstMeaningful(runState.LastRepairReferencedSymbolName, runState.LastRepairReferencedMemberName, "(none)")} symbol_recovery_status={FirstMeaningful(runState.LastRepairSymbolRecoveryStatus, "(none)")} symbol_recovery_candidate={FirstMeaningful(runState.LastRepairSymbolRecoveryCandidatePath, "(none)")} symbol_recovery_summary={FirstMeaningful(runState.LastRepairSymbolRecoverySummary, "(none)")} status={FirstMeaningful(runState.LastRepairContinuationStatus, "(none)")} summary={FirstMeaningful(runState.LastRepairContinuationSummary, "(none)")}";
    }

    private static string BuildRuntimeGenerationGuardrailBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return "Generation guardrails: stale snapshot invalidated; rerun active plan to rebuild current generation-acceptance truth.";

        if (runState is null || runState.ExecutedToolCalls.Count == 0)
            return "Generation guardrails: (none)";

        var latestCall = runState.ExecutedToolCalls
            .LastOrDefault(call =>
                TryParseGenerationGuardrail(call.StructuredDataJson, out var parsed)
                && parsed.Contract.Applicable);
        if (latestCall is null || !TryParseGenerationGuardrail(latestCall.StructuredDataJson, out var evaluation))
            return "Generation guardrails: (none)";

        var contract = evaluation.Contract ?? new CSharpGenerationPromptContractRecord();
        var allowedNamespaces = contract.AllowedNamespaces.Count == 0
            ? "(none)"
            : string.Join(", ", contract.AllowedNamespaces);
        var allowedApiOwners = contract.AllowedApiOwnerTokens.Count == 0
            ? "(none)"
            : string.Join(", ", contract.AllowedApiOwnerTokens);
        var requiredApis = contract.RequiredApiTokens.Count == 0
            ? "(none)"
            : string.Join(", ", contract.RequiredApiTokens);
        var profileRequirements = contract.ProfileRequirements.Count == 0
            ? "(none)"
            : string.Join(" | ", contract.ProfileRequirements);
        var reasons = evaluation.RejectionReasons.Count == 0
            ? "(none)"
            : string.Join(", ", evaluation.RejectionReasons);
        var profileEnforcementRules = evaluation.ProfileEnforcement.FailedRules.Count == 0
            ? "(none)"
            : string.Join(", ", evaluation.ProfileEnforcement.FailedRules);
        var postWriteRules = evaluation.PostWriteFailedRules.Count == 0
            ? "(none)"
            : string.Join(", ", evaluation.PostWriteFailedRules);
        var postWriteSignals = evaluation.PostWriteObservedSignals.Count == 0
            ? "(none)"
            : string.Join(", ", evaluation.PostWriteObservedSignals);
        var dependencyPrerequisites = evaluation.DependencyPrerequisites.Count == 0
            ? "(none)"
            : string.Join(", ", evaluation.DependencyPrerequisites);
        var followThrough = contract.FollowThroughRequirements.Count == 0
            ? "(none)"
            : string.Join(", ", contract.FollowThroughRequirements);
        var companionArtifacts = contract.CompanionArtifactHints.Count == 0
            ? "(none)"
            : string.Join(", ", contract.CompanionArtifactHints);
        return $"Generation guardrails: tool={FirstMeaningful(latestCall.ToolName, contract.ToolName, "(none)")} intent={FormatGenerationIntent(contract.Intent)} profile={FormatGenerationProfile(contract.Profile)} template={FirstMeaningful(contract.TemplateKind, "(none)")} role={FirstMeaningful(contract.DeclaredRole, "(none)")} pattern={FirstMeaningful(contract.DeclaredPattern, "(none)")} project={FirstMeaningful(contract.DeclaredProject, "(none)")} depth={FirstMeaningful(contract.ImplementationDepth, "(none)")} followthrough={followThrough} validation_target={FirstMeaningful(contract.ValidationTarget, "(none)")} companion_artifacts={companionArtifacts} framework={FirstMeaningful(contract.TargetFramework, "(none)")} namespace={FirstMeaningful(contract.NamespaceName, "(none)")} accepted={evaluation.Accepted.ToString().ToLowerInvariant()} decision={FirstMeaningful(evaluation.DecisionCode, "(none)")} rejected_by={FirstMeaningful(evaluation.PrimaryRejectionClass, "(none)")} anti_stub={FirstMeaningful(evaluation.AntiStubStatus, "(none)")} anti_hallucination={FirstMeaningful(evaluation.AntiHallucinationStatus, "(none)")} behavior={FirstMeaningful(evaluation.BehaviorStatus, "(none)")} profile_enforcement={FirstMeaningful(evaluation.ProfileEnforcement.Status, "(none)")} post_write={FirstMeaningful(evaluation.PostWriteCheckStatus, "(none)")} family_alignment={FirstMeaningful(evaluation.FamilyAlignmentStatus, "(none)")} integration={FirstMeaningful(evaluation.IntegrationStatus, "(none)")} behavior_depth={FirstMeaningful(evaluation.BehaviorDepthTier, "(none)")} dependency_prerequisites={dependencyPrerequisites} dependency_status={FirstMeaningful(evaluation.DependencyStatus, "(none)")} dependency_summary={FirstMeaningful(evaluation.DependencySummary, "(none)")} escalation={FirstMeaningful(evaluation.EscalationStatus, "(none)")} escalation_summary={FirstMeaningful(evaluation.EscalationSummary, "(none)")} retry={FirstMeaningful(evaluation.RetryStatus, "(none)")} quality={FirstMeaningful(evaluation.OutputQuality, "(none)")} completion_strength={FirstMeaningful(evaluation.CompletionStrength, "(none)")} stronger_behavior_proof_missing={evaluation.StrongerBehaviorProofStillMissing.ToString().ToLowerInvariant()} behavior_depth_artifact={FirstMeaningful(runState?.LastBehaviorDepthArtifactPath, "(none)")} behavior_depth_source={FirstMeaningful(runState?.LastBehaviorDepthTargetPath, "(none)")} behavior_depth_profile={FirstMeaningful(runState?.LastBehaviorDepthProfile, "(none)")} behavior_depth_namespace={FirstMeaningful(runState?.LastBehaviorDepthNamespace, "(none)")} behavior_depth_feature_family={FirstMeaningful(runState?.LastBehaviorDepthFeatureFamily, "(none)")} behavior_depth_gap={FirstMeaningful(runState?.LastBehaviorDepthIntegrationGapKind, "(none)")} behavior_depth_recommendation={FirstMeaningful(runState?.LastBehaviorDepthCompletionRecommendation, "(none)")} behavior_depth_follow_up={FirstMeaningful(runState?.LastBehaviorDepthFollowUpRecommendation, "(none)")} behavior_depth_next_followthrough={FirstMeaningful(runState?.LastBehaviorDepthNextFollowThroughHint, "(none)")} behavior_depth_candidates={FirstMeaningful(runState is null || runState.LastBehaviorDepthCandidateSurfaceHints.Count == 0 ? "" : string.Join(", ", runState.LastBehaviorDepthCandidateSurfaceHints), "(none)")} allowed_namespaces={allowedNamespaces} allowed_api_owners={allowedApiOwners} required_apis={requiredApis} profile_requirements={profileRequirements} profile_failed_rules={profileEnforcementRules} post_write_failed_rules={postWriteRules} post_write_signals={postWriteSignals} reasons={reasons}";
    }

    private static string BuildRuntimeProjectAttachBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return "Project attach continuity: stale snapshot invalidated; rerun active plan to rebuild current scaffold-attach truth.";

        if (runState is null)
            return "Project attach continuity: (none)";

        if (string.IsNullOrWhiteSpace(runState.LastProjectAttachTargetPath)
            && string.IsNullOrWhiteSpace(runState.LastProjectAttachContinuationStatus)
            && string.IsNullOrWhiteSpace(runState.LastProjectAttachSummary))
        {
            return "Project attach continuity: (none)";
        }

        return $"Project attach continuity: target={FirstMeaningful(runState.LastProjectAttachTargetPath, "(none)")} project_existed_at_decision={runState.LastProjectAttachProjectExistedAtDecision.ToString().ToLowerInvariant()} status={FirstMeaningful(runState.LastProjectAttachContinuationStatus, "(none)")} inserted_step={FirstMeaningful(runState.LastProjectAttachInsertedStep, "(none)")} summary={FirstMeaningful(runState.LastProjectAttachSummary, "(none)")}";
    }

    private static string BuildRuntimeProjectReferenceBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return "Project reference decision: stale snapshot invalidated; rerun active plan to rebuild current reference-direction truth.";

        if (runState is null)
            return "Project reference decision: (none)";

        var latestDecision = runState.ExecutedToolCalls
            .LastOrDefault(call => string.Equals(call.ToolName, "add_dotnet_project_reference", StringComparison.OrdinalIgnoreCase));
        if (latestDecision is null || string.IsNullOrWhiteSpace(latestDecision.Summary))
            return "Project reference decision: (none)";

        return $"Project reference decision: {FirstMeaningful(latestDecision.Summary, "(none)")}";
    }

    private static string BoundTraceSegment(string? value, int maxLength)
    {
        var normalized = FirstMeaningful(value, "(none)");
        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static bool TryParseGenerationGuardrail(string? json, out CSharpGenerationGuardrailEvaluationRecord evaluation)
    {
        evaluation = new CSharpGenerationGuardrailEvaluationRecord();
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("generation_guardrail", out var section))
                return false;

            var parsed = JsonSerializer.Deserialize<CSharpGenerationGuardrailEvaluationRecord>(section.GetRawText());
            if (parsed is null)
                return false;

            evaluation = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatGenerationIntent(CSharpGenerationIntent intent)
    {
        return intent switch
        {
            CSharpGenerationIntent.ScaffoldFile => "scaffold_file",
            CSharpGenerationIntent.ImplementBehavior => "implement_behavior",
            CSharpGenerationIntent.WireRuntimeIntegration => "wire_runtime_integration",
            CSharpGenerationIntent.VerifyBehavior => "verify_behavior",
            _ => "none"
        };
    }

    private static string FormatGenerationProfile(CSharpGenerationProfile profile)
    {
        return profile switch
        {
            CSharpGenerationProfile.ContractGeneration => "contract_generation",
            CSharpGenerationProfile.SimpleImplementation => "simple_implementation",
            CSharpGenerationProfile.TestRegistryImplementation => "test_registry_impl",
            CSharpGenerationProfile.SnapshotBuilderImplementation => "snapshot_builder_impl",
            CSharpGenerationProfile.FindingsNormalizerImplementation => "findings_normalizer_impl",
            CSharpGenerationProfile.TestHelperImplementation => "test_helper_impl",
            CSharpGenerationProfile.BuilderImplementation => "builder_impl",
            CSharpGenerationProfile.NormalizerImplementation => "normalizer_impl",
            CSharpGenerationProfile.RepositoryImplementation => "repository_implementation",
            CSharpGenerationProfile.ViewmodelGeneration => "viewmodel_generation",
            CSharpGenerationProfile.WpfXamlStubOnly => "wpf_xaml_stub_only",
            CSharpGenerationProfile.WpfXamlLayoutImplementation => "wpf_xaml_layout_impl",
            CSharpGenerationProfile.WpfViewmodelImplementation => "wpf_viewmodel_impl",
            CSharpGenerationProfile.WpfShellIntegration => "wpf_shell_integration",
            CSharpGenerationProfile.RuntimeWiring => "runtime_wiring",
            _ => "none"
        };
    }

    private static string BuildRuntimeHeadingPolicyBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
            return "Heading policy: stale snapshot invalidated; rerun active plan to rebuild current heading-class treatment truth.";

        if (runState is null || string.IsNullOrWhiteSpace(runState.LastHeadingPolicyWorkItemTitle))
            return "Heading policy: (none)";

        return $"Heading policy: title={FirstMeaningful(runState.LastHeadingPolicyWorkItemTitle, "(none)")} normalized={FirstMeaningful(runState.LastHeadingPolicyNormalizedTitle, "(none)")} class={FirstMeaningful(runState.LastHeadingPolicyClass, "(none)")} treatment={FirstMeaningful(runState.LastHeadingPolicyTreatment, "(none)")} reason={FirstMeaningful(runState.LastHeadingPolicyReasonCode, "(none)")} summary={FirstMeaningful(runState.LastHeadingPolicySummary, "(none)")}";
    }

    private static string BuildRuntimeGoalBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
        {
            return "Execution goal: stale snapshot invalidated; rerun active plan to recompute the current execution goal.";
        }

        if (runState is not null && IsGoalStaleForCurrentBlocker(runState))
        {
            return $"Execution goal: current blocker is attached to follow-up work item `{FirstMeaningful(runState.LastBlockerWorkItemTitle, runState.LastBlockerWorkItemId, "(none)")}`; no current stored goal preview is available.";
        }

        if (runState?.LastExecutionGoalResolution is null
            || runState.LastExecutionGoalResolution.GoalKind == TaskboardExecutionGoalKind.Unknown)
        {
            return "Execution goal: (none)";
        }

        var goal = runState.LastExecutionGoalResolution.Goal;
        var selected = FirstMeaningful(goal.SelectedChainTemplateId, goal.SelectedToolId, "(none)");
        var selectionPath = FirstMeaningful(runState.LastExecutionGoalResolution.SelectionPath, goal.SelectionPath, "(none)");
        var resolvedTargetPath = FirstMeaningful(runState.LastExecutionGoalResolution.ResolvedTargetPath, goal.ResolvedTargetPath);
        var targetSuffix = string.IsNullOrWhiteSpace(resolvedTargetPath)
            ? ""
            : $" target={resolvedTargetPath}";
        return $"Execution goal: {runState.LastExecutionGoalResolution.GoalKind.ToString().ToLowerInvariant()} -> {selected} work_family={FirstMeaningful(runState.LastExecutionGoalResolution.WorkFamily, goal.WorkFamily, runState.LastWorkFamily, "(none)")} phrase_family={FirstMeaningful(runState.LastExecutionGoalResolution.PhraseFamily, goal.PhraseFamily, "(none)")} template={FirstMeaningful(runState.LastExecutionGoalResolution.TemplateId, goal.TemplateId, "(none)")} via={selectionPath}{targetSuffix}";
    }

    private static string BuildRuntimeLaneBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
        {
            return "Execution lane: stale snapshot invalidated; rerun active plan to recompute the current lane.";
        }

        if (runState is not null && IsLaneStaleForCurrentBlocker(runState))
        {
            return $"Execution lane: current blocker is attached to follow-up work item `{FirstMeaningful(runState.LastBlockerWorkItemTitle, runState.LastBlockerWorkItemId, "(none)")}`; no current stored lane preview is available.";
        }

        if (runState?.LastExecutionGoalResolution?.LaneResolution is null
            || runState.LastExecutionGoalResolution.LaneResolution.LaneKind == TaskboardExecutionLaneKind.Unknown)
        {
            return "Execution lane: (none)";
        }

        var lane = runState.LastExecutionGoalResolution.LaneResolution;
        var selected = FirstMeaningful(lane.SelectedChainTemplateId, lane.SelectedToolId, "(none)");
        var targetSuffix = string.IsNullOrWhiteSpace(lane.ResolvedTargetPath)
            ? ""
            : $" target={lane.ResolvedTargetPath}";
        return $"Execution lane: {lane.LaneKind.ToString().ToLowerInvariant()} -> {selected} work_item={FirstMeaningful(lane.SourceWorkItemTitle, "(none)")} family={FirstMeaningful(lane.WorkFamily, runState.LastWorkFamily, "(none)")} operation={FirstMeaningful(lane.OperationKind, "(none)")} phrase_family={FirstMeaningful(lane.PhraseFamily, "(none)")} template={FirstMeaningful(lane.TemplateId, "(none)")} via={FirstMeaningful(lane.SelectionPath, "(none)")}{targetSuffix}";
    }

    private static string BuildRuntimeLaneBlockerBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
        {
            return $"Lane blocker: stale_cached_blocker — cached runtime snapshot predates current services ({FirstNonEmpty(assessment.InvalidationReason, assessment.Summary)}).";
        }

        if (runState is not null
            && !string.IsNullOrWhiteSpace(runState.LastBlockerReason)
            && !string.IsNullOrWhiteSpace(runState.LastBlockerWorkItemId)
            && (runState.LastExecutionGoalResolution?.LaneResolution is null
                || runState.LastExecutionGoalResolution.LaneResolution.Blocker.Code == TaskboardExecutionLaneBlockerCode.None
                || !string.Equals(runState.LastExecutionGoalResolution.LaneResolution.SourceWorkItemId, runState.LastBlockerWorkItemId, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Lane blocker: followup_resolution_blocker — {runState.LastBlockerReason} work_item={runState.LastBlockerWorkItemTitle} family={FirstNonEmpty(runState.LastBlockerWorkFamily, "(none)")} phrase_family={FirstNonEmpty(runState.LastBlockerPhraseFamily, "(none)")} operation_kind={FirstNonEmpty(runState.LastBlockerOperationKind, "(none)")} stack={FirstNonEmpty(runState.LastBlockerStackFamily, "(none)")}";
        }

        if (runState?.LastExecutionGoalResolution?.LaneResolution is null
            || runState.LastExecutionGoalResolution.LaneResolution.Blocker.Code == TaskboardExecutionLaneBlockerCode.None)
        {
            return "Lane blocker: (none)";
        }

        var lane = runState.LastExecutionGoalResolution.LaneResolution;
        return $"Lane blocker: {lane.Blocker.Code.ToString().ToLowerInvariant()} — {lane.Blocker.Message} family={FirstNonEmpty(lane.WorkFamily, runState.LastWorkFamily, "(none)")}";
    }

    private static string BuildRuntimeWorkFamilyBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (runState is null)
            return "Work family: (none)";

        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
        {
            return $"Work family: stale cached snapshot invalidated; rerun active plan to recompute current/next families ({FirstNonEmpty(assessment.InvalidationReason, assessment.Summary)}).";
        }

        var currentFamily = !string.IsNullOrWhiteSpace(runState.CurrentWorkItemId)
            ? FirstMeaningful(runState.LastWorkFamily, "(none)")
            : "(none)";
        return $"Work family: completed={FirstMeaningful(runState.LastCompletedWorkFamily, "(none)")} completed_title={FirstMeaningful(runState.LastCompletedWorkItemTitle, "(none)")} completed_phrase={FirstMeaningful(runState.LastCompletedPhraseFamily, "(none)")} completed_stack={FirstMeaningful(runState.LastCompletedStackFamily, "(none)")} current={currentFamily} source={FirstMeaningful(runState.LastWorkFamilySource, "(none)")} followup={FirstMeaningful(runState.LastFollowupWorkFamily, runState.LastNextWorkFamily, "(none)")} followup_title={FirstMeaningful(runState.LastFollowupWorkItemTitle, "(none)")} followup_phrase={FormatFollowupValue(runState.LastFollowupPhraseFamily, runState.LastFollowupPhraseFamilyReasonCode)} followup_operation={FormatFollowupValue(runState.LastFollowupOperationKind, runState.LastFollowupOperationKindReasonCode)} followup_stack={FormatFollowupValue(runState.LastFollowupStackFamily, runState.LastFollowupStackFamilyReasonCode)}";
    }

    private string BuildRuntimeCoverageBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
        {
            return $"Coverage: stale cached snapshot invalidated; current lane coverage will be recomputed on the next live run ({FirstNonEmpty(assessment.InvalidationReason, assessment.Summary)}).";
        }

        var coverage = string.IsNullOrWhiteSpace(runState?.LastCoverageMapSummary)
            ? "Coverage: (none)"
            : $"{runState.LastCoverageMapSummary} completed={BuildCompletedSummary(runState)} followup={BuildFollowupSummary(runState)}";
        var parts = new List<string> { coverage };
        if (!string.IsNullOrWhiteSpace(runState?.LastSupportCoverageSummary))
            parts.Add($"support={runState.LastSupportCoverageSummary}");
        if (!string.IsNullOrWhiteSpace(runState?.LastContradictionGuardSummary))
            parts.Add($"contradiction={runState.LastContradictionGuardSummary}");
        var csharpCoverage = _cSharpExecutionCoverageService.BuildRuntimeCoverageBanner(runState);
        if (!string.IsNullOrWhiteSpace(csharpCoverage))
            parts.Add(csharpCoverage);
        return string.Join(" ", parts);
    }

    private static string BuildRuntimeGoalBlockerBanner(TaskboardPlanRunStateRecord? runState, TaskboardRuntimeStateAssessment? assessment)
    {
        if (assessment is not null && assessment.HasSnapshot && !assessment.IsCompatible)
        {
            return $"Goal blocker: stale_cached_blocker — cached runtime snapshot predates current services ({FirstNonEmpty(assessment.InvalidationReason, assessment.Summary)}).";
        }

        if (runState is not null
            && !string.IsNullOrWhiteSpace(runState.LastBlockerReason)
            && !string.IsNullOrWhiteSpace(runState.LastBlockerWorkItemId)
            && (runState.LastExecutionGoalResolution is null
                || runState.LastExecutionGoalResolution.Blocker.Code == TaskboardExecutionGoalBlockerCode.None
                || !string.Equals(runState.LastExecutionGoalResolution.SourceWorkItemId, runState.LastBlockerWorkItemId, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Goal blocker: fresh_followup_blocker — {runState.LastBlockerReason} work_item={runState.LastBlockerWorkItemTitle} family={FirstNonEmpty(runState.LastBlockerWorkFamily, "(none)")} phrase_family={FirstNonEmpty(runState.LastBlockerPhraseFamily, "(none)")} operation_kind={FirstNonEmpty(runState.LastBlockerOperationKind, "(none)")} stack={FirstNonEmpty(runState.LastBlockerStackFamily, "(none)")}";
        }

        if (runState?.LastExecutionGoalResolution is null
            || runState.LastExecutionGoalResolution.Blocker.Code == TaskboardExecutionGoalBlockerCode.None)
        {
            return "Goal blocker: (none)";
        }

        var explanation = FirstNonEmpty(runState.LastForensicsSummary, runState.LastExecutionGoalResolution.ForensicsExplanation);
        return string.IsNullOrWhiteSpace(explanation)
            ? $"Goal blocker: {runState.LastExecutionGoalResolution.Blocker.Code.ToString().ToLowerInvariant()} — {runState.LastExecutionGoalResolution.Blocker.Message}"
            : $"Goal blocker: {runState.LastExecutionGoalResolution.Blocker.Code.ToString().ToLowerInvariant()} — {runState.LastExecutionGoalResolution.Blocker.Message} | {explanation}";
    }

    private static string ClassifyExecutionRoute(string? toolName)
    {
        return FirstMeaningful(toolName, "").ToLowerInvariant() switch
        {
            "show_artifacts" or "show_memory" => "inspect",
            "plan_repair" or "preview_patch_draft" or "apply_patch_draft" or "write_file" or "replace_in_file" => "mutate_or_repair",
            "verify_patch_draft" or "dotnet_build" or "dotnet_test" or "cmake_build" or "ctest_run" => "verify",
            _ => "unknown"
        };
    }

    private static string BuildRecentActivityLine(TaskboardRunEventRecord record)
    {
        var timestamp = DateTime.TryParse(record.CreatedUtc, out var createdUtc)
            ? createdUtc.ToLocalTime().ToString("HH:mm:ss")
            : "(time)";
        return $"[{timestamp}] {record.Message}";
    }

    private static string BuildBatchDisplayLabel(TaskboardBatch batch, TaskboardPlanRunStateRecord? runState)
    {
        var runtime = GetBatchRuntimeStatus(runState, batch.BatchId);
        return string.IsNullOrWhiteSpace(runtime)
            ? $"Batch {batch.BatchNumber}: {batch.Title}"
            : $"Batch {batch.BatchNumber}: {batch.Title} [{runtime}]";
    }

    private static string GetBatchRuntimeStatus(TaskboardPlanRunStateRecord? runState, string batchId)
    {
        var batch = runState?.Batches.FirstOrDefault(current =>
            string.Equals(current.BatchId, batchId, StringComparison.OrdinalIgnoreCase));
        if (batch is null)
            return "";

        return batch.Status.ToString().ToLowerInvariant();
    }

    private static string GetWorkItemRuntimeStatus(TaskboardPlanRunStateRecord? runState, string batchId, string workItemId)
    {
        var batch = runState?.Batches.FirstOrDefault(current =>
            string.Equals(current.BatchId, batchId, StringComparison.OrdinalIgnoreCase));
        var workItem = batch?.WorkItems.FirstOrDefault(current =>
            string.Equals(current.WorkItemId, workItemId, StringComparison.OrdinalIgnoreCase));
        return workItem?.Status.ToString().ToLowerInvariant() ?? "";
    }

    private static TaskboardBatch ResolveNextRunnableBatch(TaskboardDocument activeDocument, TaskboardPlanRunStateRecord runState)
    {
        foreach (var batchState in runState.Batches.OrderBy(batch => batch.BatchNumber))
        {
            if (batchState.WorkItems.Any(item => item.Status == TaskboardWorkItemRuntimeStatus.Pending))
            {
                var batch = activeDocument.Batches.FirstOrDefault(current =>
                    string.Equals(current.BatchId, batchState.BatchId, StringComparison.OrdinalIgnoreCase));
                if (batch is not null)
                    return batch;
            }
        }

        return activeDocument.Batches.OrderBy(current => current.BatchNumber).First();
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
            if (!string.IsNullOrWhiteSpace(value)
                && !string.Equals(value.Trim(), "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return "";
    }

    private static DateTimeOffset? ParseUtc(string? value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string FormatFollowupValue(string? value, string? reasonCode)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && !string.Equals(value.Trim(), "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return (reasonCode ?? "").Trim() switch
        {
            "phrase_family_unresolved_after_refresh" => "(phrase family unresolved after refresh)",
            "operation_kind_unresolved_after_refresh" => "(operation kind unresolved after refresh)",
            "stack_family_unresolved_after_refresh" => "(stack unresolved after refresh)",
            _ => "(none)"
        };
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

    private static string BuildCompletedSummary(TaskboardPlanRunStateRecord runState)
    {
        if (string.IsNullOrWhiteSpace(runState.LastCompletedWorkItemId))
            return "(none)";

        return $"item={FirstMeaningful(runState.LastCompletedWorkItemTitle, runState.LastCompletedWorkItemId, "(none)")} family={FirstMeaningful(runState.LastCompletedWorkFamily, "(none)")} phrase_family={FirstMeaningful(runState.LastCompletedPhraseFamily, "(none)")} operation_kind={FirstMeaningful(runState.LastCompletedOperationKind, "(none)")} stack={FirstMeaningful(runState.LastCompletedStackFamily, "(none)")}";
    }

    private static string BuildFollowupSummary(TaskboardPlanRunStateRecord runState)
    {
        if (string.IsNullOrWhiteSpace(runState.LastFollowupWorkItemId))
            return FirstMeaningful(runState.LastFollowupResolutionSummary, "(none)");

        return $"item={FirstMeaningful(runState.LastFollowupWorkItemTitle, runState.LastFollowupWorkItemId, "(none)")} batch={FirstMeaningful(runState.LastFollowupBatchTitle, "(none)")} selection={FirstMeaningful(runState.LastFollowupSelectionReason, "(none)")} family={FirstMeaningful(runState.LastFollowupWorkFamily, "(none)")} phrase_family={FormatFollowupValue(runState.LastFollowupPhraseFamily, runState.LastFollowupPhraseFamilyReasonCode)} operation_kind={FormatFollowupValue(runState.LastFollowupOperationKind, runState.LastFollowupOperationKindReasonCode)} stack={FormatFollowupValue(runState.LastFollowupStackFamily, runState.LastFollowupStackFamilyReasonCode)} summary={FirstMeaningful(runState.LastFollowupResolutionSummary, "(none)")}";
    }

    private static string BuildFailureContextSuffix(TaskboardPlanRunStateRecord? runState, bool includeRepairContext)
    {
        if (runState is null || string.IsNullOrWhiteSpace(runState.LastFailureOutcomeType))
            return "";

        var parts = new List<string>
        {
            $"failure={runState.LastFailureOutcomeType}"
        };

        if (!string.IsNullOrWhiteSpace(runState.LastFailureFamily))
            parts.Add($"failure_family={runState.LastFailureFamily}");
        if (!string.IsNullOrWhiteSpace(runState.LastFailureErrorCode))
            parts.Add($"error={runState.LastFailureErrorCode}");
        if (!string.IsNullOrWhiteSpace(runState.LastFailureTargetPath))
            parts.Add($"target={runState.LastFailureTargetPath}");
        if (!string.IsNullOrWhiteSpace(runState.LastFailureNormalizedSummary))
            parts.Add($"normalized={runState.LastFailureNormalizedSummary}");
        if (includeRepairContext && !string.IsNullOrWhiteSpace(runState.LastFailureRepairContextPath))
            parts.Add($"repair_context={runState.LastFailureRepairContextPath}");

        return parts.Count == 0 ? "" : $" {string.Join(" ", parts)}";
    }

    private static bool IsGoalStaleForCurrentBlocker(TaskboardPlanRunStateRecord runState)
    {
        if (string.IsNullOrWhiteSpace(runState.LastBlockerWorkItemId))
            return false;

        if (runState.LastExecutionGoalResolution is null
            || string.IsNullOrWhiteSpace(runState.LastExecutionGoalResolution.SourceWorkItemId))
        {
            return true;
        }

        return !string.Equals(
            runState.LastExecutionGoalResolution.SourceWorkItemId,
            runState.LastBlockerWorkItemId,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLaneStaleForCurrentBlocker(TaskboardPlanRunStateRecord runState)
    {
        if (string.IsNullOrWhiteSpace(runState.LastBlockerWorkItemId))
            return false;

        if (runState.LastExecutionGoalResolution?.LaneResolution is null
            || string.IsNullOrWhiteSpace(runState.LastExecutionGoalResolution.LaneResolution.SourceWorkItemId))
        {
            return true;
        }

        return !string.Equals(
            runState.LastExecutionGoalResolution.LaneResolution.SourceWorkItemId,
            runState.LastBlockerWorkItemId,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatList(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return "(none)";

        return string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}
