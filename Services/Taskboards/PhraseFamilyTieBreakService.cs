using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class PhraseFamilyTieBreakService
{
    public PhraseFamilyTieBreakResult Resolve(
        TaskboardDocument activeDocument,
        TaskboardBatch batch,
        TaskboardRunWorkItem workItem,
        IReadOnlyList<string> candidatePhraseFamilies)
    {
        var candidates = candidatePhraseFamilies
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count <= 1)
        {
            return PhraseFamilyTieBreakResult.Unresolved(
                TaskboardPhraseFamilyBlockerCode.TieBreakRuleNotApplicable,
                "phrase_family_tie_not_required",
                "Phrase-family tie-break was not required because only one deterministic candidate remained.");
        }

        var workItemText = NormalizeText(workItem.Title, workItem.Summary, workItem.PromptText);
        var batchText = NormalizeText(batch.Title);
        var documentText = NormalizeText(activeDocument.Title, activeDocument.ObjectiveText);
        if (IsGroupedShellSectionsPhrase(workItemText, batchText))
        {
            return PhraseFamilyTieBreakResult.Resolved(
                "ui_shell_sections",
                "grouped_shell_sections_keywords",
                $"Deterministic tie-break promoted `{workItem.Title}` to grouped family `ui_shell_sections` from candidates [{string.Join(", ", candidates)}].");
        }

        if (IsGroupedShellPageList(workItemText, batchText))
        {
            return PhraseFamilyTieBreakResult.Resolved(
                "ui_shell_sections",
                "grouped_shell_sections_page_list",
                $"Deterministic tie-break recognized grouped shell pages for `{workItem.Title}` and selected `ui_shell_sections`.");
        }

        if (LooksLikeSingularSettingsPage(workItemText))
        {
            return PhraseFamilyTieBreakResult.Resolved(
                "add_settings_page",
                "single_page_settings_focus",
                $"Deterministic tie-break selected singular page family `add_settings_page` for `{workItem.Title}`.");
        }

        if (LooksLikeSingularHistoryView(workItemText))
        {
            return PhraseFamilyTieBreakResult.Resolved(
                "add_history_log_view",
                "single_page_history_focus",
                $"Deterministic tie-break selected singular page family `add_history_log_view` for `{workItem.Title}`.");
        }

        if (LooksLikeSingularDashboard(workItemText))
        {
            return PhraseFamilyTieBreakResult.Resolved(
                "wire_dashboard",
                "single_page_dashboard_focus",
                $"Deterministic tie-break selected singular page family `wire_dashboard` for `{workItem.Title}`.");
        }

        if (MentionsShellContext(batchText, documentText) && CountNamedPages(workItemText) >= 2)
        {
            return PhraseFamilyTieBreakResult.Resolved(
                "ui_shell_sections",
                "shell_batch_grouped_pages",
                $"Deterministic tie-break used batch shell context to classify `{workItem.Title}` as `ui_shell_sections`.");
        }

        return PhraseFamilyTieBreakResult.Unresolved(
            TaskboardPhraseFamilyBlockerCode.PhraseFamilyTieUnresolved,
            "tie_break_rule_not_applicable",
            $"Deterministic phrase-family tie-break could not safely resolve `{workItem.Title}` from candidates [{string.Join(", ", candidates)}].");
    }

    private static bool IsGroupedShellSectionsPhrase(string workItemText, string batchText)
    {
        return MentionsAny(
                workItemText,
                "required ui shell sections",
                "ui shell sections",
                "initial pages",
                "top-level shell pages",
                "top level shell pages",
                "required shell pages",
                "top-level app pages",
                "top level app pages")
            || (MentionsAny(workItemText, "shell sections", "shell pages", "app pages")
                && MentionsShellContext(batchText, workItemText));
    }

    private static bool IsGroupedShellPageList(string workItemText, string batchText)
    {
        return CountNamedPages(workItemText) >= 3
            && MentionsShellContext(workItemText, batchText);
    }

    private static bool LooksLikeSingularSettingsPage(string workItemText)
    {
        return MentionsAny(workItemText, "settings page", "settings view")
            && CountNamedPages(workItemText) <= 1;
    }

    private static bool LooksLikeSingularHistoryView(string workItemText)
    {
        return MentionsAny(workItemText, "history page", "history view", "log view", "history/log")
            && CountNamedPages(workItemText) <= 1;
    }

    private static bool LooksLikeSingularDashboard(string workItemText)
    {
        return (MentionsAny(workItemText, "wire dashboard", "dashboard wireup", "dashboard page")
                || (workItemText.Contains("dashboard", StringComparison.OrdinalIgnoreCase)
                    && !workItemText.Contains("findings", StringComparison.OrdinalIgnoreCase)
                    && !workItemText.Contains("history", StringComparison.OrdinalIgnoreCase)
                    && !workItemText.Contains("settings", StringComparison.OrdinalIgnoreCase)))
            && CountNamedPages(workItemText) <= 1;
    }

    private static int CountNamedPages(string text)
    {
        var count = 0;
        if (text.Contains("dashboard", StringComparison.OrdinalIgnoreCase))
            count++;
        if (text.Contains("findings", StringComparison.OrdinalIgnoreCase))
            count++;
        if (text.Contains("history", StringComparison.OrdinalIgnoreCase))
            count++;
        if (text.Contains("settings", StringComparison.OrdinalIgnoreCase))
            count++;

        return count;
    }

    private static bool MentionsShellContext(params string[] values)
    {
        return values.Any(value => MentionsAny(
            value,
            "shell",
            "page",
            "pages",
            "screen",
            "screens",
            "section",
            "sections",
            "window",
            "view set"));
    }

    private static bool MentionsAny(string value, params string[] patterns)
    {
        return patterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeText(params string?[] parts)
    {
        return Regex.Replace(
                string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part))),
                "\\s+",
                " ")
            .Trim()
            .ToLowerInvariant();
    }
}

public sealed class PhraseFamilyTieBreakResult
{
    public bool IsResolved { get; init; }
    public string SelectedPhraseFamily { get; init; } = "";
    public string RuleId { get; init; } = "";
    public string Summary { get; init; } = "";
    public TaskboardPhraseFamilyBlockerCode BlockerCode { get; init; } = TaskboardPhraseFamilyBlockerCode.None;

    public static PhraseFamilyTieBreakResult Resolved(string selectedPhraseFamily, string ruleId, string summary)
    {
        return new PhraseFamilyTieBreakResult
        {
            IsResolved = true,
            SelectedPhraseFamily = selectedPhraseFamily,
            RuleId = ruleId,
            Summary = summary
        };
    }

    public static PhraseFamilyTieBreakResult Unresolved(TaskboardPhraseFamilyBlockerCode blockerCode, string ruleId, string summary)
    {
        return new PhraseFamilyTieBreakResult
        {
            IsResolved = false,
            RuleId = ruleId,
            Summary = summary,
            BlockerCode = blockerCode
        };
    }
}
