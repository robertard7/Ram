using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class ExecutionFeedbackService
{
    private readonly ActionableSuggestionService _actionableSuggestionService = new();
    private readonly ArtifactClassificationService _artifactClassificationService = new();
    private readonly BuildScopeAssessmentService _buildScopeAssessmentService = new();
    private readonly BuildSystemDetectionService _buildSystemDetectionService = new();
    private readonly LatestActionableStateService _latestActionableStateService;

    public ExecutionFeedbackService()
    {
        _latestActionableStateService = new LatestActionableStateService(_artifactClassificationService);
    }

    public string? BuildResponse(string prompt, string workspaceRoot, RamDbService ramDbService)
    {
        if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(workspaceRoot))
            return null;

        var normalized = Normalize(prompt);
        if (!IsFailureQuestion(normalized)
            && !IsNextActionQuestion(normalized)
            && !IsVerificationOutcomeQuestion(normalized)
            && !IsAutoValidationQuestion(normalized)
            && !IsBuildScopeQuestion(normalized))
            return null;

        var snapshot = _latestActionableStateService.GetLatestState(workspaceRoot, ramDbService);
        if (IsAutoValidationQuestion(normalized))
        {
            var autoValidationResponse = BuildAutoValidationResponse(workspaceRoot, ramDbService, snapshot);
            if (!string.IsNullOrWhiteSpace(autoValidationResponse))
                return autoValidationResponse;
        }

        if (IsBuildScopeQuestion(normalized))
            return BuildBuildScopeResponse(workspaceRoot, snapshot);

        if (IsVerificationOutcomeQuestion(normalized))
            return BuildVerificationResponse(workspaceRoot, ramDbService, snapshot);

        if (IsFailureQuestion(normalized))
            return BuildFailureResponse(workspaceRoot, ramDbService, snapshot);

        return BuildNextActionResponse(workspaceRoot, snapshot, ramDbService);
    }

    private string BuildFailureResponse(string workspaceRoot, RamDbService ramDbService, LatestActionableStateRecord snapshot)
    {
        if (!snapshot.HasFailureContext)
        {
            if (snapshot.LatestResultKind == "success")
                return BuildSuccessContextRefusal(snapshot);

            if (snapshot.LatestResultKind == "safety_abort")
                return BuildSafetyAbortContextResponse(snapshot);

            return "No recorded build or test failure is currently stored for this workspace.";
        }

        var state = snapshot.ExecutionState;
        var lines = new List<string>
        {
            "Recorded failure:",
            $"Build family: {DisplayValue(snapshot.LatestBuildFamily)}",
            $"Tool: {DisplayValue(state.LastFailureToolName)}",
            $"Outcome: {DisplayValue(state.LastFailureOutcomeType)}",
            $"Target: {DisplayValue(state.LastFailureTargetPath)}",
            $"When: {DisplayValue(state.LastFailureUtc)}",
            $"Summary: {DisplayValue(state.LastFailureSummary)}",
            $"File context: {BuildFileContextSummary(workspaceRoot, ramDbService)}",
            $"Artifact chain: failure={YesNo(snapshot.LatestFailureArtifact is not null)} repair={YesNo(snapshot.LatestRepairArtifact is not null)} patch={YesNo(snapshot.LatestPatchArtifact is not null)} verification={YesNo(snapshot.LatestVerificationArtifact is not null)}"
        };

        AppendSuggestionSection(lines, GetActionableSuggestions(workspaceRoot, snapshot, ramDbService));

        return string.Join(Environment.NewLine, lines);
    }

    private string BuildVerificationResponse(string workspaceRoot, RamDbService ramDbService, LatestActionableStateRecord snapshot)
    {
        var state = snapshot.ExecutionState;
        if (string.IsNullOrWhiteSpace(state.LastVerificationUtc))
        {
            if (snapshot.LatestResultKind == "success")
            {
                return "No verification result is stored for this workspace."
                    + Environment.NewLine
                    + BuildRecentResultLine(snapshot)
                    + Environment.NewLine
                    + "No patch has been verified yet.";
            }

            if (snapshot.LatestResultKind == "safety_abort")
            {
                return "No verification result is stored for this workspace."
                    + Environment.NewLine
                    + BuildRecentResultLine(snapshot)
                    + Environment.NewLine
                    + "The latest run was safety-aborted, so there is no verification chain to compare.";
            }

            return "No recent patch verification result is stored for this workspace.";
        }

        var outcome = DeserializeVerificationOutcome(state.LastVerificationDataJson);
        var lines = new List<string>
        {
            "Recent verification:",
            $"Tool: {DisplayValue(state.LastVerificationToolName)}",
            $"Target: {DisplayValue(state.LastVerificationTargetPath)}",
            $"Outcome: {DisplayValue(state.LastVerificationOutcomeType)}",
            $"When: {DisplayValue(state.LastVerificationUtc)}",
            $"Summary: {DisplayValue(state.LastVerificationSummary)}"
        };

        if (outcome is not null)
        {
            if (outcome.BeforeFailureCount.HasValue || outcome.AfterFailureCount.HasValue)
            {
                lines.Add(
                    $"Failure counts: before={DisplayCount(outcome.BeforeFailureCount)} after={DisplayCount(outcome.AfterFailureCount)} delta={DisplayCount(outcome.FailureCountDelta)}");
            }

            if (outcome.TopRemainingFailures.Count > 0)
            {
                lines.Add("Top remaining failures:");
                foreach (var remaining in outcome.TopRemainingFailures.Take(5))
                    lines.Add($"- {remaining}");
            }

            if (!string.IsNullOrWhiteSpace(outcome.Explanation))
                lines.Add($"Explanation: {outcome.Explanation}");
        }

        lines.Add($"Artifact chain: patch={YesNo(snapshot.LatestPatchArtifact is not null)} verification={YesNo(snapshot.LatestVerificationArtifact is not null)}");
        lines.Add($"File context: {BuildFileContextSummary(workspaceRoot, ramDbService)}");
        AppendSuggestionSection(lines, GetActionableSuggestions(workspaceRoot, snapshot, ramDbService));
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildSuccessContextRefusal(LatestActionableStateRecord snapshot)
    {
        return "No build or test failure is currently recorded for this workspace."
            + Environment.NewLine
            + BuildRecentResultLine(snapshot)
            + Environment.NewLine
            + "No failure artifacts were recorded, so repair actions are unavailable until a real failure exists.";
    }

    private string BuildSafetyAbortContextResponse(LatestActionableStateRecord snapshot)
    {
        if (string.Equals(snapshot.LatestResultOutcomeType, "safety_blocked_scope", StringComparison.OrdinalIgnoreCase))
        {
            var assessment = DeserializeAssessment(snapshot.ExecutionState.LastFailureDataJson);
            var lines = new List<string>
            {
                "The latest run was blocked before launch, not after a code failure.",
                BuildRecentResultLine(snapshot)
            };

            if (assessment is not null)
            {
                lines.Add($"Target scope: {DisplayValue(assessment.TargetKind)}");
                lines.Add($"Resolved target: {DisplayValue(assessment.ResolvedTargetPath)}");
                lines.Add($"Reason: {DisplayValue(assessment.Reason)}");
                if (!string.IsNullOrWhiteSpace(assessment.RecommendedSaferAlternative))
                    lines.Add($"Safer next step: {assessment.RecommendedSaferAlternative}");
            }

            lines.Add("No repair chain was created because RAM blocked a broad native build scope before execution.");
            return string.Join(Environment.NewLine, lines);
        }

        return "The latest run did not record a code failure."
            + Environment.NewLine
            + BuildRecentResultLine(snapshot)
            + Environment.NewLine
            + "This workspace is currently in a safety-aborted state, so no repair chain was created.";
    }

    private string BuildBuildScopeResponse(string workspaceRoot, LatestActionableStateRecord snapshot)
    {
        if (string.Equals(snapshot.LatestResultOutcomeType, "safety_blocked_scope", StringComparison.OrdinalIgnoreCase))
        {
            var assessment = DeserializeAssessment(snapshot.ExecutionState.LastFailureDataJson);
            var lines = new List<string>
            {
                "Latest build-scope block:",
                BuildRecentResultLine(snapshot)
            };

            if (assessment is not null)
            {
                lines.Add($"Build family: {DisplayValue(assessment.BuildFamily.ToString().ToLowerInvariant())}");
                lines.Add($"Target scope: {DisplayValue(assessment.TargetKind)}");
                lines.Add($"Resolved target: {DisplayValue(assessment.ResolvedTargetPath)}");
                lines.Add($"Risk: {FormatRiskLevel(assessment.RiskLevel)}");
                lines.Add($"Reason: {DisplayValue(assessment.Reason)}");
                if (!string.IsNullOrWhiteSpace(assessment.RecommendedSaferAlternative))
                    lines.Add($"Safer next step: {assessment.RecommendedSaferAlternative}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        var detection = _buildSystemDetectionService.Detect(workspaceRoot);
        var linesForGuidance = new List<string>();
        if (snapshot.LatestResultKind == "success")
        {
            linesForGuidance.Add("No build scope block is currently recorded.");
            linesForGuidance.Add(BuildRecentResultLine(snapshot));
        }
        else if (snapshot.LatestResultKind == "safety_abort")
        {
            linesForGuidance.Add("No scope-risk block is currently recorded.");
            linesForGuidance.Add(BuildRecentResultLine(snapshot));
            linesForGuidance.Add("The latest run was stopped by runtime safety policy, not by pre-launch scope assessment.");
        }
        else if (snapshot.LatestResultKind == "failure")
        {
            linesForGuidance.Add("The latest run reached a real build or test failure.");
            linesForGuidance.Add(BuildRecentResultLine(snapshot));
        }
        else
        {
            linesForGuidance.Add("No build scope block is currently recorded for this workspace.");
        }

        linesForGuidance.Add(_buildScopeAssessmentService.BuildGuidance(workspaceRoot, detection));
        return string.Join(Environment.NewLine, linesForGuidance);
    }

    private string? BuildAutoValidationResponse(string workspaceRoot, RamDbService ramDbService, LatestActionableStateRecord snapshot)
    {
        var artifact = snapshot.LatestAutoValidationArtifact;
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.Content))
        {
            if (string.Equals(snapshot.LatestResultToolName, "apply_patch_draft", StringComparison.OrdinalIgnoreCase)
                || string.Equals(snapshot.LatestResultToolName, "write_file", StringComparison.OrdinalIgnoreCase)
                || string.Equals(snapshot.LatestResultToolName, "replace_in_file", StringComparison.OrdinalIgnoreCase)
                || string.Equals(snapshot.LatestResultToolName, "save_output", StringComparison.OrdinalIgnoreCase))
            {
                return "No auto-validation result is stored for the latest change yet."
                    + Environment.NewLine
                    + BuildRecentResultLine(snapshot);
            }

            return null;
        }

        AutoValidationResultRecord? result;
        try
        {
            result = JsonSerializer.Deserialize<AutoValidationResultRecord>(artifact.Content);
        }
        catch
        {
            return "RAM found an auto-validation artifact, but it could not read the stored result payload.";
        }

        if (result is null)
            return "RAM found an auto-validation artifact, but it did not contain a readable result.";

        var lines = new List<string>
        {
            "Latest auto-validation:",
            $"Source: {DisplayValue(result.SourceActionType)}",
            $"Build family: {DisplayValue(result.BuildFamily)}",
            $"Outcome: {DisplayValue(result.OutcomeClassification)}",
            $"Tool: {DisplayValue(result.ExecutedTool)}",
            $"Target: {DisplayValue(result.ResolvedTarget)}",
            $"When: {DisplayValue(result.CreatedUtc)}",
            $"Summary: {DisplayValue(result.Summary)}",
            $"Execution attempted: {YesNo(result.ExecutionAttempted)}"
        };

        if (!result.ExecutionAttempted)
            lines.Add("No external validation command was launched for this result.");

        if (result.ChangedFilePaths.Count > 0)
            lines.Add($"Changed files: {string.Join(", ", result.ChangedFilePaths.Take(3))}");

        if (!string.IsNullOrWhiteSpace(result.LinkedOutcomeType))
            lines.Add($"Linked outcome: {result.LinkedOutcomeType}");

        if (!string.IsNullOrWhiteSpace(result.SafetyTrigger))
            lines.Add($"Safety trigger: {result.SafetyTrigger}");

        if (result.TopFailures.Count > 0)
        {
            lines.Add("Top failures:");
            foreach (var failure in result.TopFailures.Take(5))
                lines.Add($"- {failure}");
        }

        if (!string.IsNullOrWhiteSpace(result.Explanation))
            lines.Add($"Explanation: {result.Explanation}");

        if (!string.IsNullOrWhiteSpace(result.SuggestedNextStep))
            lines.Add($"Suggested next step: {result.SuggestedNextStep}");

        lines.Add($"Artifact: {artifact.RelativePath}");
        AppendSuggestionSection(lines, GetActionableSuggestions(workspaceRoot, snapshot, ramDbService));
        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsFailureQuestion(string normalizedPrompt)
    {
        return normalizedPrompt.Contains("why did tests fail", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("why did the tests fail", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("why did build fail", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("why did the build fail", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt == "what broke"
            || normalizedPrompt.Contains("what broke", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNextActionQuestion(string normalizedPrompt)
    {
        return normalizedPrompt == "what should i do next"
            || normalizedPrompt.Contains("what should i do next", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what can ram do next", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt == "what next"
            || normalizedPrompt.Contains("what next", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what do i do next", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("suggest next step", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVerificationOutcomeQuestion(string normalizedPrompt)
    {
        return normalizedPrompt.Contains("what changed after the fix", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("is it still broken", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("did errors go down", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what is still failing", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what changed after verify", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBuildScopeQuestion(string normalizedPrompt)
    {
        return normalizedPrompt.Contains("what safe build target can i run", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("why was build blocked", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("why was the build blocked", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("why did ram block the build", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAutoValidationQuestion(string normalizedPrompt)
    {
        return normalizedPrompt.Contains("did that change pass", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("did that edit pass", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("was that edit validated", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what happened after the patch", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what failed after my edit", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("was validation blocked", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string prompt)
    {
        return (prompt ?? "").Trim().ToLowerInvariant();
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static string DisplayCount(int? value)
    {
        return value.HasValue ? value.Value.ToString() : "(unknown)";
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

    private static string YesNo(bool value)
    {
        return value ? "yes" : "no";
    }

    private static string BuildRecentResultLine(LatestActionableStateRecord snapshot)
    {
        return $"Latest result: {DisplayValue(snapshot.LatestResultKind)}"
            + $" tool={DisplayValue(snapshot.LatestResultToolName)}"
            + $" target={DisplayValue(snapshot.LatestResultTargetPath)}"
            + $" outcome={DisplayValue(snapshot.LatestResultOutcomeType)}"
            + $" summary={DisplayValue(snapshot.LatestResultSummary)}";
    }

    private static List<string> BuildNextActionHeader(LatestActionableStateRecord snapshot)
    {
        return
        [
            $"Most recent result: {DisplayValue(snapshot.LatestResultKind)}",
            $"Build family: {DisplayValue(snapshot.LatestBuildFamily)}",
            $"Tool: {DisplayValue(snapshot.LatestResultToolName)}",
            $"Target: {DisplayValue(snapshot.LatestResultTargetPath)}",
            $"Outcome: {DisplayValue(snapshot.LatestResultOutcomeType)}",
            $"Summary: {DisplayValue(snapshot.LatestResultSummary)}"
        ];
    }

    private static string BuildFileContextSummary(string workspaceRoot, RamDbService ramDbService)
    {
        var artifact = ramDbService.LoadLatestArtifactByType(workspaceRoot, "repair_context");
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.Content))
            return "not yet available";

        try
        {
            var repairContext = JsonSerializer.Deserialize<RepairContextRecord>(artifact.Content);
            var item = repairContext?.Items.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.RelativePath));
            if (item is null)
                return "not yet available";

            var lineSuffix = item.LineNumber > 0 ? $":{item.LineNumber}" : "";
            return $"{item.RelativePath}{lineSuffix}";
        }
        catch
        {
            return "not yet available";
        }
    }

    private static VerificationOutcomeRecord? DeserializeVerificationOutcome(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("outcome", out var outcomeElement))
                return outcomeElement.Deserialize<VerificationOutcomeRecord>();
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static BuildScopeAssessmentRecord? DeserializeAssessment(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("assessment", out var assessmentElement))
                return assessmentElement.Deserialize<BuildScopeAssessmentRecord>();
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static AutoValidationResultRecord? DeserializeAutoValidationResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AutoValidationResultRecord>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatRiskLevel(BuildScopeRiskLevel riskLevel)
    {
        return riskLevel switch
        {
            BuildScopeRiskLevel.SafeNarrow => "safe_narrow",
            BuildScopeRiskLevel.MediumNarrowable => "medium_narrowable",
            BuildScopeRiskLevel.HighBroad => "high_broad",
            _ => "blocked_unknown"
        };
    }

    private string BuildNextActionResponse(string workspaceRoot, LatestActionableStateRecord snapshot, RamDbService ramDbService)
    {
        var latestAutoValidation = DeserializeAutoValidationResult(snapshot.LatestAutoValidationArtifact?.Content ?? "");
        var suggestions = GetActionableSuggestions(workspaceRoot, snapshot, ramDbService);

        if (snapshot.LatestResultKind == "none" && latestAutoValidation is null)
            return "No recent build, test, safety-abort, or verification result is stored for this workspace.";

        var lines = snapshot.LatestResultKind == "none"
            ? new List<string> { "No manual build, test, safety-abort, or verification result is stored for this workspace." }
            : BuildNextActionHeader(snapshot);

        if (latestAutoValidation is not null)
        {
            lines.Add($"Latest auto-validation: outcome={latestAutoValidation.OutcomeClassification}");
            lines.Add($"Auto-validation target: {DisplayValue(latestAutoValidation.ResolvedTarget)}");
            lines.Add($"Auto-validation summary: {DisplayValue(latestAutoValidation.Summary)}");
        }

        lines.Add($"Repair context available: {YesNo(snapshot.HasRepairContext)}");
        lines.Add($"Safety result available: {YesNo(snapshot.LatestSafetyArtifact is not null || snapshot.HasSafetyAbort)}");

        if (suggestions.Count == 0)
        {
            lines.Add("No deterministic next action is currently available from the recorded workspace state.");
            return string.Join(Environment.NewLine, lines);
        }

        AppendSuggestionSection(lines, suggestions);
        return string.Join(Environment.NewLine, lines);
    }

    private List<ActionableSuggestionRecord> GetActionableSuggestions(
        string workspaceRoot,
        LatestActionableStateRecord snapshot,
        RamDbService ramDbService)
    {
        return _actionableSuggestionService.BuildSuggestions(workspaceRoot, snapshot, ramDbService);
    }

    private static void AppendSuggestionSection(List<string> lines, IReadOnlyList<ActionableSuggestionRecord> suggestions)
    {
        if (suggestions.Count == 0)
            return;

        lines.Add("What RAM can do next:");
        AppendSuggestionGroup(lines, suggestions, SuggestionReadiness.ReadyNow, "Ready now");
        AppendSuggestionGroup(lines, suggestions, SuggestionReadiness.NeedsPrerequisite, "Needs prerequisite");
        AppendSuggestionGroup(lines, suggestions, SuggestionReadiness.ManualOnly, "Manual-only");
        AppendSuggestionGroup(lines, suggestions, SuggestionReadiness.Blocked, "Blocked");
        AppendSuggestionGroup(lines, suggestions, SuggestionReadiness.InformationalOnly, "Informational");
    }

    private static void AppendSuggestionGroup(
        List<string> lines,
        IReadOnlyList<ActionableSuggestionRecord> suggestions,
        SuggestionReadiness readiness,
        string heading)
    {
        var matches = suggestions.Where(suggestion => suggestion.Readiness == readiness).ToList();
        if (matches.Count == 0)
            return;

        lines.Add($"{heading}:");
        foreach (var suggestion in matches)
        {
            lines.Add($"- {DisplayValue(suggestion.Title)}");
            if (!string.IsNullOrWhiteSpace(suggestion.PromptText))
                lines.Add($"  prompt={suggestion.PromptText}");

            var reason = readiness switch
            {
                SuggestionReadiness.ReadyNow or SuggestionReadiness.InformationalOnly => suggestion.ShortRationale,
                _ => FirstNonEmpty(suggestion.BlockedReason, suggestion.ShortRationale)
            };

            if (!string.IsNullOrWhiteSpace(reason))
                lines.Add($"  reason={reason}");
        }
    }
}
