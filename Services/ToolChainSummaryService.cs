using System.Text;
using RAM.Models;

namespace RAM.Services;

public sealed class ToolChainSummaryService
{
    public SummaryAgentResponsePayload BuildDeterministicSummarySection(ToolChainSummaryInput input)
    {
        var payload = new SummaryAgentResponsePayload
        {
            SummaryTitle = $"Controlled chain: {DisplayValue(input.UserGoal)}",
            StatusLine = $"type={FormatChainType(input.ChainType)} template={DisplayValue(input.TemplateName)} status={FormatStatus(input.Status)} stop={FormatStopReason(input.StopReason)}",
            SummaryLines =
            [
                $"Outcome: {DisplayValue(input.FinalOutcomeSummary)}",
                input.ExecutionOccurred
                    ? "Execution happened for at least one recorded step."
                    : (input.ExecutionBlocked || IsBlockedOrManualStop(input.StopReason))
                        ? "No external command ran; the chain stopped on a blocked or manual-only path."
                        : "No external command was needed for this chain."
            ]
        };

        if (input.Steps.Count == 0)
        {
            payload.SummaryLines.Add("Steps: none recorded.");
        }
        else
        {
            foreach (var step in input.Steps)
            {
                var executionSuffix = step.ExecutionAttempted
                    ? "execution attempted"
                    : string.IsNullOrWhiteSpace(step.ExecutionBlockedReason)
                        ? "no external execution"
                        : $"blocked: {step.ExecutionBlockedReason}";
                payload.SummaryLines.Add($"{step.StepIndex}. {step.ToolName} -> {DisplayValue(step.ResultClassification)}. {DisplayValue(step.ResultSummary)} ({executionSuffix})");
            }
        }

        if (!string.IsNullOrWhiteSpace(input.SuggestedNextAction))
            payload.Warnings.Add($"Next safest step: {input.SuggestedNextAction}");

        return payload;
    }

    public string BuildDeterministicFallbackSummary(ToolChainSummaryInput input, SuggestionPresentationResult? suggestionPresentation = null)
    {
        var section = BuildDeterministicSummarySection(input);
        var presentation = suggestionPresentation ?? BuildDeterministicSuggestionFallback(input.ActionableSuggestions);
        return RenderSummary(input, section, presentation);
    }

    public string RenderSummary(
        ToolChainSummaryInput input,
        SummaryAgentResponsePayload summarySection,
        SuggestionPresentationResult suggestionPresentation)
    {
        var lines = new List<string>
        {
            DisplayValue(summarySection.SummaryTitle),
            DisplayValue(summarySection.StatusLine)
        };

        foreach (var line in summarySection.SummaryLines.Where(line => !string.IsNullOrWhiteSpace(line)))
            lines.Add(line);

        foreach (var warning in summarySection.Warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)))
            lines.Add(warning);

        AppendSuggestionSection(lines, suggestionPresentation, input.ActionableSuggestions);
        return string.Join(Environment.NewLine, lines);
    }

    public SuggestionPresentationResult BuildDeterministicSuggestionFallback(IReadOnlyList<ActionableSuggestionRecord> suggestions)
    {
        var groups = new List<SuggestionPresentationGroup>();
        AddSuggestionGroup(groups, "Ready now", suggestions, SuggestionReadiness.ReadyNow);
        AddSuggestionGroup(groups, "Needs prerequisite", suggestions, SuggestionReadiness.NeedsPrerequisite);
        AddSuggestionGroup(groups, "Manual-only", suggestions, SuggestionReadiness.ManualOnly);
        AddSuggestionGroup(groups, "Blocked", suggestions, SuggestionReadiness.Blocked);
        AddSuggestionGroup(groups, "Informational", suggestions, SuggestionReadiness.InformationalOnly);

        return new SuggestionPresentationResult
        {
            FallbackUsed = true,
            Groups = groups
        };
    }

    private static void AppendSuggestionSection(
        List<string> lines,
        SuggestionPresentationResult suggestionPresentation,
        IReadOnlyList<ActionableSuggestionRecord> fallbackSuggestions)
    {
        var groups = suggestionPresentation.Groups.Count > 0
            ? suggestionPresentation.Groups
            : BuildFallbackGroups(fallbackSuggestions);
        if (groups.Count == 0)
            return;

        lines.Add("What RAM can do next:");
        foreach (var group in groups)
        {
            lines.Add($"{group.Title}:");
            foreach (var suggestion in group.Suggestions)
            {
                lines.Add($"- {DisplayValue(suggestion.Title)}");
                if (!string.IsNullOrWhiteSpace(suggestion.PromptText))
                    lines.Add($"  prompt={suggestion.PromptText}");

                var reason = suggestion.Readiness switch
                {
                    SuggestionReadiness.ReadyNow or SuggestionReadiness.InformationalOnly => suggestion.ShortRationale,
                    _ => FirstNonEmpty(suggestion.BlockedReason, suggestion.ShortRationale)
                };

                if (!string.IsNullOrWhiteSpace(reason))
                    lines.Add($"  reason={reason}");
            }
        }

        foreach (var note in suggestionPresentation.PresentationNotes.Where(note => !string.IsNullOrWhiteSpace(note)))
            lines.Add($"Note: {note}");
    }

    private static List<SuggestionPresentationGroup> BuildFallbackGroups(IReadOnlyList<ActionableSuggestionRecord> suggestions)
    {
        var groups = new List<SuggestionPresentationGroup>();
        AddSuggestionGroup(groups, "Ready now", suggestions, SuggestionReadiness.ReadyNow);
        AddSuggestionGroup(groups, "Needs prerequisite", suggestions, SuggestionReadiness.NeedsPrerequisite);
        AddSuggestionGroup(groups, "Manual-only", suggestions, SuggestionReadiness.ManualOnly);
        AddSuggestionGroup(groups, "Blocked", suggestions, SuggestionReadiness.Blocked);
        AddSuggestionGroup(groups, "Informational", suggestions, SuggestionReadiness.InformationalOnly);
        return groups;
    }

    private static void AddSuggestionGroup(
        List<SuggestionPresentationGroup> groups,
        string title,
        IReadOnlyList<ActionableSuggestionRecord> suggestions,
        SuggestionReadiness readiness)
    {
        var matches = suggestions
            .Where(suggestion => suggestion.Readiness == readiness)
            .OrderBy(suggestion => suggestion.Priority)
            .ThenBy(suggestion => suggestion.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matches.Count == 0)
            return;

        groups.Add(new SuggestionPresentationGroup
        {
            Title = title,
            Suggestions = matches
        });
    }

    private static bool IsBlockedOrManualStop(ToolChainStopReason stopReason)
    {
        return stopReason is ToolChainStopReason.ManualOnly
            or ToolChainStopReason.ScopeBlocked
            or ToolChainStopReason.SafetyBlocked
            or ToolChainStopReason.PolicyBlockedNextStep
            or ToolChainStopReason.NoFurtherStepAllowed;
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

    private static string FormatChainType(ToolChainType chainType)
    {
        return chainType switch
        {
            ToolChainType.FileEdit => "file_edit",
            ToolChainType.Repair => "repair",
            ToolChainType.Build => "build",
            ToolChainType.Verification => "verification",
            ToolChainType.ArtifactInspection => "artifact_inspection",
            ToolChainType.AutoValidation => "auto_validation",
            ToolChainType.CustomControlled => "custom_controlled",
            _ => "none"
        };
    }

    private static string FormatStatus(ToolChainStatus status)
    {
        return status switch
        {
            ToolChainStatus.Planned => "planned",
            ToolChainStatus.Running => "running",
            ToolChainStatus.Completed => "completed",
            ToolChainStatus.Blocked => "blocked",
            ToolChainStatus.Failed => "failed",
            ToolChainStatus.Cancelled => "cancelled",
            _ => "unknown"
        };
    }

    private static string FormatStopReason(ToolChainStopReason stopReason)
    {
        return stopReason switch
        {
            ToolChainStopReason.GoalCompleted => "goal_completed",
            ToolChainStopReason.PolicyBlockedNextStep => "policy_blocked_next_step",
            ToolChainStopReason.ManualOnly => "manual_only",
            ToolChainStopReason.SafetyBlocked => "safety_blocked",
            ToolChainStopReason.ScopeBlocked => "scope_blocked",
            ToolChainStopReason.ToolFailed => "tool_failed",
            ToolChainStopReason.InvalidModelStep => "invalid_model_step",
            ToolChainStopReason.ChainLimitReached => "chain_limit_reached",
            ToolChainStopReason.NoFurtherStepAllowed => "no_further_step_allowed",
            _ => "unknown"
        };
    }
}
