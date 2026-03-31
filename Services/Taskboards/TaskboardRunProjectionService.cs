using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardRunProjectionService
{
    private readonly TaskboardArtifactStore _artifactStore = new();

    public TaskboardRunProjection RunActivePlan(string workspaceRoot, TaskboardProjection projection, RamDbService ramDbService)
    {
        if (projection.ActiveImport is null || projection.ActiveDocument is null)
        {
            return new TaskboardRunProjection
            {
                WorkspaceRoot = workspaceRoot,
                Scope = "active_plan",
                Message = "Run Active Plan blocked: no active plan is available."
            };
        }

        var batch = projection.ActiveDocument.Batches
            .OrderBy(current => current.BatchNumber)
            .FirstOrDefault();
        if (batch is null)
        {
            return new TaskboardRunProjection
            {
                WorkspaceRoot = workspaceRoot,
                Scope = "active_plan",
                ActivePlanImportId = projection.ActiveImport.ImportId,
                ActivePlanTitle = projection.ActiveImport.Title,
                Message = $"Run Active Plan blocked: active plan `{projection.ActiveImport.Title}` has no parsed batches."
            };
        }

        return PrepareProjectionForBatch(
            workspaceRoot,
            projection.ActiveImport,
            batch,
            "active_plan",
            ramDbService);
    }

    public TaskboardRunProjection RunSelectedBatch(string workspaceRoot, TaskboardProjection projection, RamDbService ramDbService)
    {
        if (projection.ActiveImport is null || projection.ActiveDocument is null)
        {
            return new TaskboardRunProjection
            {
                WorkspaceRoot = workspaceRoot,
                Scope = "selected_batch",
                Message = "Run Selected Batch blocked: no active plan is available."
            };
        }

        var batchId = projection.SelectedBatch?.BatchId;
        if (string.IsNullOrWhiteSpace(batchId))
        {
            return new TaskboardRunProjection
            {
                WorkspaceRoot = workspaceRoot,
                Scope = "selected_batch",
                ActivePlanImportId = projection.ActiveImport.ImportId,
                ActivePlanTitle = projection.ActiveImport.Title,
                Message = "Run Selected Batch blocked: no active batch is selected."
            };
        }

        var batch = projection.ActiveDocument.Batches
            .FirstOrDefault(current => string.Equals(current.BatchId, batchId, StringComparison.OrdinalIgnoreCase));
        if (batch is null)
        {
            return new TaskboardRunProjection
            {
                WorkspaceRoot = workspaceRoot,
                Scope = "selected_batch",
                ActivePlanImportId = projection.ActiveImport.ImportId,
                ActivePlanTitle = projection.ActiveImport.Title,
                Message = "Run Selected Batch blocked: selected batch does not belong to the active plan."
            };
        }

        return PrepareProjectionForBatch(
            workspaceRoot,
            projection.ActiveImport,
            batch,
            "selected_batch",
            ramDbService);
    }

    public TaskboardRunProjection PrepareProjectionForBatch(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardBatch batch,
        string scope,
        RamDbService ramDbService,
        bool executionStarted = false,
        TaskboardBatchRunStateRecord? runStateBatch = null,
        TaskboardPlanRunStateRecord? runState = null)
    {
        return SaveProjection(
            ramDbService,
            workspaceRoot,
            BuildProjection(activeImport, batch, scope, executionStarted, runStateBatch, runState));
    }

    public List<TaskboardRunWorkItem> CreateWorkItems(TaskboardBatch batch)
    {
        if (batch.Steps.Count > 0)
        {
            return batch.Steps
                .OrderBy(step => step.Ordinal)
                .Select(step => new TaskboardRunWorkItem
                {
                    WorkItemId = step.StepId,
                    Ordinal = step.Ordinal,
                    DisplayOrdinal = step.Ordinal.ToString(),
                    Title = step.Title,
                    Summary = BuildContentSummary(step.Content),
                    PromptText = BuildPromptText(step.Title, step.Content)
                })
                .ToList();
        }

        return batch.Content.Paragraphs.Count == 0
               && batch.Content.NumberedItems.Count == 0
               && batch.Content.Subsections.Count == 0
            ? batch.Content.BulletItems
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(6)
            .Select((item, index) => new TaskboardRunWorkItem
            {
                WorkItemId = $"{batch.BatchId}:content:{index + 1}",
                Ordinal = index + 1,
                DisplayOrdinal = (index + 1).ToString(),
                Title = item,
                Summary = "Parsed from batch content.",
                PromptText = item.Trim()
            })
            .ToList()
            : [];
    }

    private TaskboardRunProjection BuildProjection(
        TaskboardImportRecord activeImport,
        TaskboardBatch batch,
        string scope,
        bool executionStarted,
        TaskboardBatchRunStateRecord? runStateBatch,
        TaskboardPlanRunStateRecord? runState)
    {
        var runProjection = new TaskboardRunProjection
        {
            RunId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = activeImport.WorkspaceRoot,
            Scope = scope,
            Success = true,
            ActivePlanImportId = activeImport.ImportId,
            ActivePlanTitle = activeImport.Title,
            BatchId = batch.BatchId,
            BatchTitle = batch.Title,
            BatchNumber = batch.BatchNumber,
            ExecutionStarted = executionStarted,
            BuildProfileSummary = BuildProfileSummary(runState),
            DecompositionSummary = BuildDecompositionSummary(runState),
            WorkFamilySummary = BuildWorkFamilySummary(runState),
            LaneCoverageSummary = BuildLaneCoverageSummary(runState),
            ExecutionLaneSummary = BuildExecutionLaneSummary(runState),
            ExecutionLaneBlockerSummary = BuildExecutionLaneBlockerSummary(runState),
            ExecutionGoalSummary = BuildExecutionGoalSummary(runState),
            ExecutionGoalBlockerSummary = BuildExecutionGoalBlockerSummary(runState)
        };

        runProjection.WorkItems = runStateBatch is null
            ? CreateWorkItems(batch)
            : runStateBatch.WorkItems.Select(item => new TaskboardRunWorkItem
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
            }).ToList();

        runProjection.Message = BuildRunMessage(runProjection);
        return runProjection;
    }

    private TaskboardRunProjection SaveProjection(RamDbService ramDbService, string workspaceRoot, TaskboardRunProjection projection)
    {
        _artifactStore.SaveRunProjectionArtifact(ramDbService, workspaceRoot, projection);
        return projection;
    }

    private static string BuildRunMessage(TaskboardRunProjection projection)
    {
        var lines = new List<string>
        {
            projection.ExecutionStarted
                ? $"Taskboard auto-run executing Batch `{projection.BatchTitle}` with {projection.WorkItems.Count} bounded work item{(projection.WorkItems.Count == 1 ? "" : "s")}."
                : projection.Scope == "selected_batch"
                ? $"Run Selected Batch prepared {projection.WorkItems.Count} bounded work item{(projection.WorkItems.Count == 1 ? "" : "s")} from `{projection.BatchTitle}`."
                : $"Run Active Plan prepared Batch `{projection.BatchTitle}` with {projection.WorkItems.Count} bounded work item{(projection.WorkItems.Count == 1 ? "" : "s")}."
        };
        lines.Add(projection.ExecutionStarted
            ? "Execution is starting through RAM's controlled taskboard auto-run bridge."
            : "No execution started. Import and activation remain separate from execution.");

        if (projection.WorkItems.Count > 0)
        {
            lines.Add("Work queue:");
            foreach (var item in projection.WorkItems.Take(6))
            {
                var summary = string.IsNullOrWhiteSpace(item.Summary) ? "" : $" — {item.Summary}";
                var origin = item.IsDecomposedItem && !string.IsNullOrWhiteSpace(item.SourceWorkItemId)
                    ? $" [decomposed from {item.SourceWorkItemId}]"
                    : "";
                var template = !string.IsNullOrWhiteSpace(item.TemplateId)
                    ? $" [template {item.TemplateId}]"
                    : !string.IsNullOrWhiteSpace(item.PhraseFamily)
                        ? $" [phrase {item.PhraseFamily}]"
                        : "";
                var family = !string.IsNullOrWhiteSpace(item.WorkFamily)
                    ? $" [family {item.WorkFamily}]"
                    : "";
                lines.Add($"- {FirstNonEmpty(item.DisplayOrdinal, item.Ordinal.ToString())}. {item.Title}{summary}{origin}{template}{family}");
            }
        }

        if (!string.IsNullOrWhiteSpace(projection.BuildProfileSummary))
            lines.Add($"Build profile: {projection.BuildProfileSummary}");
        if (!string.IsNullOrWhiteSpace(projection.DecompositionSummary))
            lines.Add($"Decomposition: {projection.DecompositionSummary}");
        if (!string.IsNullOrWhiteSpace(projection.WorkFamilySummary))
            lines.Add($"Work family: {projection.WorkFamilySummary}");
        if (!string.IsNullOrWhiteSpace(projection.LaneCoverageSummary))
            lines.Add($"Lane coverage: {projection.LaneCoverageSummary}");
        if (!string.IsNullOrWhiteSpace(projection.ExecutionLaneSummary))
            lines.Add($"Execution lane: {projection.ExecutionLaneSummary}");
        if (!string.IsNullOrWhiteSpace(projection.ExecutionLaneBlockerSummary))
            lines.Add($"Lane blocker: {projection.ExecutionLaneBlockerSummary}");
        if (!string.IsNullOrWhiteSpace(projection.ExecutionGoalSummary))
            lines.Add($"Execution goal: {projection.ExecutionGoalSummary}");
        if (!string.IsNullOrWhiteSpace(projection.ExecutionGoalBlockerSummary))
            lines.Add($"Goal blocker: {projection.ExecutionGoalBlockerSummary}");

        lines.Add("Suggested next prompts:");
        lines.Add("- summarize active batch");
        lines.Add("- next-ready batch items");
        lines.Add("- what can RAM do next");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildContentSummary(TaskboardSectionContent content)
    {
        var firstLine = EnumerateSectionLines(content)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
        firstLine = firstLine.Trim();
        return firstLine.Length <= 140 ? firstLine : firstLine[..140] + "...";
    }

    private static string BuildPromptText(string title, TaskboardSectionContent content)
    {
        if (!string.IsNullOrWhiteSpace(title) && !IsGenericPromptTitle(title))
            return title.Trim();

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(title))
            parts.Add(title.Trim());

        parts.AddRange(EnumerateSectionLines(content)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .Take(6));

        return string.Join(" ", parts).Trim();
    }

    private static string BuildProfileSummary(TaskboardPlanRunStateRecord? runState)
    {
        if (runState?.LastResolvedBuildProfile is null
            || runState.LastResolvedBuildProfile.Status != TaskboardBuildProfileResolutionStatus.Resolved)
        {
            return "";
        }

        return $"{FormatStackFamily(runState.LastResolvedBuildProfile.StackFamily)} ({runState.LastResolvedBuildProfile.Confidence.ToString().ToLowerInvariant()})";
    }

    private static string BuildDecompositionSummary(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is null || string.IsNullOrWhiteSpace(runState.LastDecompositionSummary))
            return "";

        return $"{runState.LastDecompositionSummary} raw={FirstNonEmpty(runState.LastPhraseFamilyRawPhraseText, "(none)")} normalized={FirstNonEmpty(runState.LastPhraseFamilyNormalizedPhraseText, "(none)")} closest={FirstNonEmpty(runState.LastPhraseFamilyClosestKnownFamilyGroup, "(none)")} phrase_family={FirstNonEmpty(runState.LastPhraseFamily, "(none)")} source={FirstNonEmpty(runState.LastPhraseFamilySource, "(none)")} candidates={FormatList(runState.LastPhraseFamilyCandidates)} deterministic={FirstNonEmpty(runState.LastPhraseFamilyDeterministicCandidate, "(none)")} advisory={FirstNonEmpty(runState.LastPhraseFamilyAdvisoryCandidate, "(none)")} tie_break={FirstNonEmpty(runState.LastPhraseFamilyTieBreakRuleId, "(none)")} tie_break_summary={FirstNonEmpty(runState.LastPhraseFamilyTieBreakSummary, "(none)")} blocker={FirstNonEmpty(runState.LastPhraseFamilyBlockerCode, "(none)")} terminal_stage={FirstNonEmpty(runState.LastPhraseFamilyTerminalResolverStage, "(none)")} builder_operation={FirstNonEmpty(runState.LastPhraseFamilyBuilderOperationStatus, "(none)")} lane_resolution={FirstNonEmpty(runState.LastPhraseFamilyLaneResolutionStatus, "(none)")} trace={FirstNonEmpty(runState.LastPhraseFamilyResolutionPathTrace, "(none)")} resolution={FirstNonEmpty(runState.LastPhraseFamilyResolutionSummary, "(none)")}";
    }

    private static string BuildExecutionGoalSummary(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is not null && IsGoalStaleForCurrentBlocker(runState))
            return "";

        if (runState?.LastExecutionGoalResolution is null
            || runState.LastExecutionGoalResolution.GoalKind == TaskboardExecutionGoalKind.Unknown)
        {
            return "";
        }

        var goal = runState.LastExecutionGoalResolution.Goal;
        var selected = FirstNonEmpty(goal.SelectedChainTemplateId, goal.SelectedToolId, "(none)");
        var resolvedTargetPath = FirstNonEmpty(runState.LastExecutionGoalResolution.ResolvedTargetPath, goal.ResolvedTargetPath);
        var targetSuffix = string.IsNullOrWhiteSpace(resolvedTargetPath)
            ? ""
            : $" target={resolvedTargetPath}";
        return $"{runState.LastExecutionGoalResolution.GoalKind.ToString().ToLowerInvariant()} -> {selected} work_family={FirstNonEmpty(runState.LastExecutionGoalResolution.WorkFamily, runState.LastWorkFamily, "(none)")} phrase_family={FirstNonEmpty(runState.LastExecutionGoalResolution.PhraseFamily, runState.LastPhraseFamily, "(none)")} template={FirstNonEmpty(runState.LastExecutionGoalResolution.TemplateId, runState.LastTemplateId, "(none)")}{targetSuffix}";
    }

    private static string BuildWorkFamilySummary(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is null)
            return "";

        var completed = FirstNonEmpty(runState.LastCompletedWorkFamily, "(none)");
        var followup = FirstNonEmpty(runState.LastFollowupWorkFamily, runState.LastNextWorkFamily, "(none)");
        var current = !string.IsNullOrWhiteSpace(runState.CurrentWorkItemId)
            ? FirstNonEmpty(runState.LastWorkFamily, "(none)")
            : "(none)";
        return $"completed={completed} current={current} completed_title={FirstNonEmpty(runState.LastCompletedWorkItemTitle, "(none)")} followup={followup} followup_title={FirstNonEmpty(runState.LastFollowupWorkItemTitle, "(none)")} followup_phrase={FormatFollowupValue(runState.LastFollowupPhraseFamily, runState.LastFollowupPhraseFamilyReasonCode)} followup_operation={FormatFollowupValue(runState.LastFollowupOperationKind, runState.LastFollowupOperationKindReasonCode)} followup_stack={FormatFollowupValue(runState.LastFollowupStackFamily, runState.LastFollowupStackFamilyReasonCode)}";
    }

    private static string BuildLaneCoverageSummary(TaskboardPlanRunStateRecord? runState)
    {
        return string.IsNullOrWhiteSpace(runState?.LastCoverageMapSummary)
            ? ""
            : $"{runState.LastCoverageMapSummary} followup={FirstNonEmpty(runState.LastFollowupResolutionSummary, "(none)")}";
    }

    private static string BuildExecutionLaneSummary(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is not null && IsLaneStaleForCurrentBlocker(runState))
            return "";

        if (runState?.LastExecutionGoalResolution?.LaneResolution is null
            || runState.LastExecutionGoalResolution.LaneResolution.LaneKind == TaskboardExecutionLaneKind.Unknown)
        {
            return "";
        }

        var lane = runState.LastExecutionGoalResolution.LaneResolution;
        var selected = FirstNonEmpty(lane.SelectedChainTemplateId, lane.SelectedToolId, "(none)");
        var targetSuffix = string.IsNullOrWhiteSpace(lane.ResolvedTargetPath)
            ? ""
            : $" target={lane.ResolvedTargetPath}";
        return $"{lane.LaneKind.ToString().ToLowerInvariant()} -> {selected} work_family={FirstNonEmpty(lane.WorkFamily, runState.LastWorkFamily, "(none)")} operation={FirstNonEmpty(lane.OperationKind, "(none)")} phrase_family={FirstNonEmpty(lane.PhraseFamily, runState.LastPhraseFamily, "(none)")} template={FirstNonEmpty(lane.TemplateId, runState.LastTemplateId, "(none)")}{targetSuffix}";
    }

    private static string BuildExecutionLaneBlockerSummary(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is not null
            && !string.IsNullOrWhiteSpace(runState.LastBlockerReason)
            && !string.IsNullOrWhiteSpace(runState.LastBlockerWorkItemId)
            && (runState.LastExecutionGoalResolution?.LaneResolution is null
                || runState.LastExecutionGoalResolution.LaneResolution.Blocker.Code == TaskboardExecutionLaneBlockerCode.None
                || !string.Equals(runState.LastExecutionGoalResolution.LaneResolution.SourceWorkItemId, runState.LastBlockerWorkItemId, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{runState.LastBlockerReason} work_item={runState.LastBlockerWorkItemTitle} family={FirstNonEmpty(runState.LastBlockerWorkFamily, "(none)")} phrase_family={FirstNonEmpty(runState.LastBlockerPhraseFamily, "(none)")} operation_kind={FirstNonEmpty(runState.LastBlockerOperationKind, "(none)")} stack={FirstNonEmpty(runState.LastBlockerStackFamily, "(none)")}";
        }

        if (runState?.LastExecutionGoalResolution?.LaneResolution is null
            || runState.LastExecutionGoalResolution.LaneResolution.Blocker.Code == TaskboardExecutionLaneBlockerCode.None)
        {
            return "";
        }

        var lane = runState.LastExecutionGoalResolution.LaneResolution;
        return $"{lane.Blocker.Code.ToString().ToLowerInvariant()}: {lane.Blocker.Message}";
    }

    private static string BuildExecutionGoalBlockerSummary(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is not null
            && !string.IsNullOrWhiteSpace(runState.LastBlockerReason)
            && !string.IsNullOrWhiteSpace(runState.LastBlockerWorkItemId)
            && (runState.LastExecutionGoalResolution is null
                || runState.LastExecutionGoalResolution.Blocker.Code == TaskboardExecutionGoalBlockerCode.None
                || !string.Equals(runState.LastExecutionGoalResolution.SourceWorkItemId, runState.LastBlockerWorkItemId, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{runState.LastBlockerReason} work_item={runState.LastBlockerWorkItemTitle} family={FirstNonEmpty(runState.LastBlockerWorkFamily, "(none)")} phrase_family={FirstNonEmpty(runState.LastBlockerPhraseFamily, "(none)")} operation_kind={FirstNonEmpty(runState.LastBlockerOperationKind, "(none)")} stack={FirstNonEmpty(runState.LastBlockerStackFamily, "(none)")}";
        }

        if (runState?.LastExecutionGoalResolution is null
            || runState.LastExecutionGoalResolution.Blocker.Code == TaskboardExecutionGoalBlockerCode.None)
        {
            return "";
        }

        var explanation = FirstNonEmpty(runState.LastForensicsSummary, runState.LastExecutionGoalResolution.ForensicsExplanation);
        return string.IsNullOrWhiteSpace(explanation)
            ? $"{runState.LastExecutionGoalResolution.Blocker.Code.ToString().ToLowerInvariant()}: {runState.LastExecutionGoalResolution.Blocker.Message}"
            : $"{runState.LastExecutionGoalResolution.Blocker.Code.ToString().ToLowerInvariant()}: {runState.LastExecutionGoalResolution.Blocker.Message} | {explanation}";
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

    private static IEnumerable<string> EnumerateSectionLines(TaskboardSectionContent section)
    {
        foreach (var paragraph in section.Paragraphs)
            yield return paragraph;
        foreach (var item in section.BulletItems)
            yield return item;
        foreach (var item in section.NumberedItems)
            yield return item;
        foreach (var subsection in section.Subsections)
        {
            foreach (var line in EnumerateSectionLines(subsection))
                yield return line;
        }
    }

    private static bool IsGenericPromptTitle(string? title)
    {
        return TaskboardStructuralHeadingService.IsNonActionableHeading(title);
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
}
