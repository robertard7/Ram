using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardFollowUpWorkItemSelectionService
{
    private static readonly FileIdentityService FileIdentityService = new();

    public TaskboardFollowUpWorkItemSelectionResult SelectNext(
        TaskboardPlanRunStateRecord runState,
        bool selectedAfterPostChainReconciliation)
    {
        var behaviorFollowUpRequired = HasBehaviorDepthFollowUpRequirement(runState);
        foreach (var batch in runState.Batches.OrderBy(current => current.BatchNumber))
        {
            var workItem = SelectPreferredPendingWorkItem(runState, batch);
            if (workItem is null)
                continue;

            var selectionReason = ResolveSelectionReason(runState, workItem, behaviorFollowUpRequired);
            var selectionSummary = BuildSelectionSummary(runState, batch, workItem, selectionReason);

            return new TaskboardFollowUpWorkItemSelectionResult
            {
                Batch = batch,
                WorkItem = workItem,
                Record = new TaskboardFollowUpWorkItemSelectionRecord
                {
                    SelectionId = Guid.NewGuid().ToString("N"),
                    WorkspaceRoot = runState.WorkspaceRoot,
                    PlanImportId = runState.PlanImportId,
                    PlanTitle = runState.PlanTitle,
                    BatchId = batch.BatchId,
                    BatchTitle = batch.Title,
                    WorkItemId = workItem.WorkItemId,
                    WorkItemTitle = workItem.Title,
                    SelectionReason = selectionReason,
                    SelectedAfterPostChainReconciliation = selectedAfterPostChainReconciliation,
                    Summary = selectionSummary,
                    CreatedUtc = DateTime.UtcNow.ToString("O")
                }
            };
        }

        var emptySelectionReason = behaviorFollowUpRequired
            ? "behavior_depth_followup_missing"
            : "no_followup_work_item_remaining";
        var emptySelectionSummary = behaviorFollowUpRequired
            ? $"Behavior-depth evidence still recommends bounded follow-up (`{FirstNonEmpty(runState.LastBehaviorDepthFollowUpRecommendation, runState.LastBehaviorDepthCompletionRecommendation, "follow-up required")}`), but no unresolved follow-up work item remains. {BuildBehaviorDepthFollowThroughDescriptor(runState)}"
            : "No unresolved follow-up work item remains after the latest successful completion.";
        return new TaskboardFollowUpWorkItemSelectionResult
        {
            Record = new TaskboardFollowUpWorkItemSelectionRecord
            {
                SelectionId = Guid.NewGuid().ToString("N"),
                WorkspaceRoot = runState.WorkspaceRoot,
                PlanImportId = runState.PlanImportId,
                PlanTitle = runState.PlanTitle,
                SelectionReason = emptySelectionReason,
                SelectedAfterPostChainReconciliation = selectedAfterPostChainReconciliation,
                Summary = emptySelectionSummary,
                CreatedUtc = DateTime.UtcNow.ToString("O")
            }
        };
    }

    public static TaskboardWorkItemRunStateRecord? SelectPreferredPendingWorkItem(
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch)
    {
        var pendingItems = batch.WorkItems
            .Where(current => current.Status == TaskboardWorkItemRuntimeStatus.Pending)
            .ToList();
        if (pendingItems.Count == 0)
            return null;

        if (!HasCompletedVerification(runState))
            return pendingItems.First();

        return pendingItems
            .OrderByDescending(item => ScorePendingWorkItem(runState, item))
            .ThenBy(item => item.Ordinal <= 0 ? int.MaxValue : item.Ordinal)
            .First();
    }

    private static bool HasCompletedVerification(TaskboardPlanRunStateRecord runState)
    {
        return !string.IsNullOrWhiteSpace(runState.LastVerificationAfterMutationOutcome)
            || string.Equals(runState.LastCompletedWorkFamily, "build_verify", StringComparison.OrdinalIgnoreCase)
            || runState.ExecutedToolCalls.Any(call =>
                string.Equals(call.Stage, "completed", StringComparison.OrdinalIgnoreCase)
                && call.ToolName is "dotnet_build" or "dotnet_test" or "verify_patch_draft" or "cmake_build");
    }

    private static int ScorePendingWorkItem(TaskboardPlanRunStateRecord runState, TaskboardWorkItemRunStateRecord item)
    {
        var family = ResolvePriorityFamily(item);
        var baseScore = family switch
        {
            "build_repair" => 100,
            "check_runner" => 95,
            "findings_pipeline" => 90,
            "core_domain_models_contracts" => 87,
            "repository_scaffold" => 85,
            "storage_bootstrap" => 84,
            "app_state_wiring" or "ui_wiring" or "add_navigation_app_state" => 82,
            "build_verify" => 70,
            "ui_shell_sections" or "viewmodel_scaffold" => 45,
            "solution_scaffold" => 35,
            _ => 20
        };

        if (string.Equals(family, "build_verify", StringComparison.OrdinalIgnoreCase)
            && HasCompletedVerification(runState)
            && !HasFreshMutationWithoutVerification(runState))
        {
            baseScore -= 35;
        }

        if (string.Equals(family, "check_runner", StringComparison.OrdinalIgnoreCase)
            && HasCurrentGreenDirectTestProof(runState)
            && !HasFreshMutationWithoutVerification(runState))
        {
            baseScore -= 45;
        }

        return baseScore
            + ScoreBehaviorDepthContinuationBoost(runState, family)
            + ScorePreciseAdjacentFollowThroughBoost(runState, item, family);
    }

    private static string ResolvePriorityFamily(TaskboardWorkItemRunStateRecord item)
    {
        if (!string.IsNullOrWhiteSpace(item.WorkFamily))
            return item.WorkFamily;

        var actionablePhrase = TaskboardStructuralHeadingService.ResolveActionableFollowupPhraseFamily(
            item.Title,
            item.Summary,
            item.PromptText);
        return actionablePhrase switch
        {
            "check_runner" => "check_runner",
            "findings_pipeline" => "findings_pipeline",
            "core_domain_models_contracts" => "core_domain_models_contracts",
            "repository_scaffold" => "repository_scaffold",
            "setup_storage_layer" => "storage_bootstrap",
            "add_navigation_app_state" => "app_state_wiring",
            "wire_dashboard" or "add_history_log_view" or "add_settings_page" or "ui_shell_sections" => "ui_shell_sections",
            _ => ""
        };
    }

    private static string ResolveSelectionReason(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord workItem,
        bool behaviorFollowUpRequired)
    {
        var family = ResolvePriorityFamily(workItem);
        if (behaviorFollowUpRequired && MatchesPreciseBehaviorDepthTarget(runState, workItem, family))
            return "next_unmet_adjacent_integration_surface";

        if (behaviorFollowUpRequired && !string.Equals(family, "build_verify", StringComparison.OrdinalIgnoreCase))
            return "behavior_depth_followup_required";

        if (string.Equals(family, "build_verify", StringComparison.OrdinalIgnoreCase)
            && HasCompletedVerification(runState)
            && !HasFreshMutationWithoutVerification(runState))
        {
            return "remaining_pending_work_item_after_prior_verification";
        }

        return "next_pending_work_item";
    }

    private static string BuildSelectionSummary(
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord workItem,
        string selectionReason)
    {
        var currentGreenTestProof = HasCurrentGreenDirectTestProof(runState);
        return selectionReason switch
        {
            "next_unmet_adjacent_integration_surface" => $"Selected follow-up work item `{workItem.Title}` from Batch {batch.BatchNumber} because the next unmet adjacent integration surface is `{FirstNonEmpty(runState.LastBehaviorDepthNextFollowThroughHint, runState.LastBehaviorDepthFollowUpRecommendation, "follow-up required")}`. {BuildBehaviorDepthFollowThroughDescriptor(runState)}",
            "behavior_depth_followup_required" => currentGreenTestProof && !string.Equals(ResolvePriorityFamily(workItem), "check_runner", StringComparison.OrdinalIgnoreCase)
                ? $"Selected follow-up work item `{workItem.Title}` from Batch {batch.BatchNumber} because the latest direct test target is already green and the next unmet adjacent integration surface is still missing: {FirstNonEmpty(runState.LastBehaviorDepthFollowUpRecommendation, runState.LastBehaviorDepthCompletionRecommendation, "follow-up required")}. {BuildBehaviorDepthFollowThroughDescriptor(runState)}"
                : $"Selected follow-up work item `{workItem.Title}` from Batch {batch.BatchNumber} because the latest successful local step still requires bounded integration follow-through: {FirstNonEmpty(runState.LastBehaviorDepthFollowUpRecommendation, runState.LastBehaviorDepthCompletionRecommendation, "follow-up required")}. {BuildBehaviorDepthFollowThroughDescriptor(runState)}",
            "remaining_pending_work_item_after_prior_verification" => $"Selected follow-up work item `{workItem.Title}` from Batch {batch.BatchNumber}; prior verification is already green, so RAM is continuing into remaining bounded work instead of repeating the same validation loop.",
            _ => $"Selected follow-up work item `{workItem.Title}` from Batch {batch.BatchNumber} after successful completion of the previous work item."
        };
    }

    private static bool HasBehaviorDepthFollowUpRequirement(TaskboardPlanRunStateRecord runState)
    {
        if (runState is null)
            return false;

        if (string.Equals(runState.LastBehaviorDepthCompletionRecommendation, "followup_required_for_behavior_depth", StringComparison.OrdinalIgnoreCase)
            || string.Equals(runState.LastBehaviorDepthCompletionRecommendation, "accepted_behavior_without_closure", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(runState.LastBehaviorDepthFollowUpRecommendation)
            && !string.Equals(runState.LastBehaviorDepthFollowUpRecommendation, "no_additional_followup_required", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFreshMutationWithoutVerification(TaskboardPlanRunStateRecord runState)
    {
        if (runState is null || string.IsNullOrWhiteSpace(runState.LastMutationUtc))
            return false;

        if (string.IsNullOrWhiteSpace(runState.LastVerificationAfterMutationUtc))
            return true;

        return ParseUtc(runState.LastMutationUtc) > ParseUtc(runState.LastVerificationAfterMutationUtc);
    }

    private static bool HasCurrentGreenDirectTestProof(TaskboardPlanRunStateRecord runState)
    {
        if (runState is null)
            return false;

        return runState.ExecutedToolCalls.Any(call =>
            string.Equals(call.Stage, "completed", StringComparison.OrdinalIgnoreCase)
            && string.Equals(call.ToolName, "dotnet_test", StringComparison.OrdinalIgnoreCase)
            && string.Equals(call.ResultClassification, "success", StringComparison.OrdinalIgnoreCase));
    }

    private static int ScoreBehaviorDepthContinuationBoost(TaskboardPlanRunStateRecord runState, string family)
    {
        if (!HasBehaviorDepthFollowUpRequirement(runState))
            return 0;

        var recommendation = FirstNonEmpty(runState.LastBehaviorDepthFollowUpRecommendation, runState.LastBehaviorDepthCompletionRecommendation);
        if (string.Equals(family, "build_verify", StringComparison.OrdinalIgnoreCase))
            return -20;

        if (ContainsAny(recommendation, "consumer", "registration", "service"))
        {
            return family switch
            {
                "core_domain_models_contracts" or "repository_scaffold" or "storage_bootstrap" => 25,
                "app_state_wiring" or "ui_wiring" => 18,
                _ => 0
            };
        }

        if (ContainsAny(recommendation, "binding", "viewmodel", "navigation", "shell", "state", "ui"))
        {
            return family switch
            {
                "app_state_wiring" or "ui_wiring" or "viewmodel_scaffold" => 25,
                "ui_shell_sections" => 18,
                _ => 0
            };
        }

        if (ContainsAny(recommendation, "caller path", "behavior-path test", "test"))
        {
            return family switch
            {
                "check_runner" or "findings_pipeline" => 25,
                _ => 0
            };
        }

        return family switch
        {
            "core_domain_models_contracts" or "repository_scaffold" or "storage_bootstrap" or "app_state_wiring" or "ui_wiring" or "viewmodel_scaffold" or "check_runner" or "findings_pipeline" => 10,
            _ => 0
        };
    }

    private static int ScorePreciseAdjacentFollowThroughBoost(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord item,
        string family)
    {
        if (!HasBehaviorDepthFollowUpRequirement(runState))
            return 0;

        var hints = runState.LastBehaviorDepthCandidateSurfaceHints ?? [];
        for (var index = 0; index < hints.Count; index++)
        {
            if (MatchesHint(item, family, hints[index]))
                return Math.Max(30 - (index * 3), 18);
        }

        return MatchesBroadGapTarget(runState.LastBehaviorDepthIntegrationGapKind, family)
            ? 12
            : 0;
    }

    private static bool MatchesPreciseBehaviorDepthTarget(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord item,
        string family)
    {
        var hints = runState.LastBehaviorDepthCandidateSurfaceHints ?? [];
        return hints.Any(hint => MatchesHint(item, family, hint));
    }

    private static bool MatchesHint(
        TaskboardWorkItemRunStateRecord item,
        string family,
        string hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return false;

        var normalizedHint = NormalizeToken(hint);
        if (string.IsNullOrWhiteSpace(normalizedHint))
            return false;

        if (string.Equals(NormalizeToken(item.OperationKind), normalizedHint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeToken(item.WorkFamily), normalizedHint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeToken(family), normalizedHint, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var identity = FileIdentityService.Identify(FirstNonEmpty(
            item.LastExecutionGoalResolution.ResolvedTargetPath,
            item.ExpectedArtifact,
            item.ValidationHint));
        if (string.Equals(NormalizeToken(identity.Role), normalizedHint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeToken(identity.FileType), normalizedHint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeToken(identity.ProjectName), normalizedHint, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var combinedText = NormalizeToken($"{item.Title} {item.Summary} {item.PromptText}");
        return ContainsAllTokens(combinedText, normalizedHint);
    }

    private static bool MatchesBroadGapTarget(string? integrationGapKind, string family)
    {
        if (string.IsNullOrWhiteSpace(integrationGapKind))
            return false;

        return integrationGapKind switch
        {
            "missing_service_registration" or "missing_repository_consumer" or "missing_repository_store_consumer" => family is "core_domain_models_contracts" or "repository_scaffold" or "storage_bootstrap",
            "missing_viewmodel_consumer" or "missing_binding_surface" or "missing_navigation_use_site" => family is "app_state_wiring" or "ui_wiring" or "ui_shell_sections" or "viewmodel_scaffold",
            "missing_helper_caller_path" or "missing_behavior_path_test" => family is "check_runner" or "findings_pipeline",
            _ => false
        };
    }

    private static string BuildBehaviorDepthFollowThroughDescriptor(TaskboardPlanRunStateRecord runState)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(runState.LastBehaviorDepthNextFollowThroughHint))
            parts.Add($"next_followthrough={runState.LastBehaviorDepthNextFollowThroughHint}");
        if (!string.IsNullOrWhiteSpace(runState.LastBehaviorDepthIntegrationGapKind))
            parts.Add($"gap={runState.LastBehaviorDepthIntegrationGapKind}");
        if (!string.IsNullOrWhiteSpace(runState.LastBehaviorDepthTargetPath))
            parts.Add($"source={runState.LastBehaviorDepthTargetPath}");
        if (runState.LastBehaviorDepthCandidateSurfaceHints.Count > 0)
            parts.Add($"candidates={string.Join(", ", runState.LastBehaviorDepthCandidateSurfaceHints)}");

        return parts.Count == 0
            ? ""
            : $"Follow-through detail: {string.Join(" ", parts)}.";
    }

    private static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');

        return string.Join(" ", builder
            .ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool ContainsAllTokens(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle))
            return false;

        var tokens = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.All(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static DateTime ParseUtc(string? value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.MinValue;
    }
}
