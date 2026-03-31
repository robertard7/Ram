using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardDocumentClassifier
{
    private static readonly Regex HeadingRegex = new(@"^\s*#{1,6}\s+", RegexOptions.CultureInvariant);
    private static readonly Regex H1HeadingRegex = new(@"^\s*#\s+(?<text>.+?)\s*$", RegexOptions.CultureInvariant);

    public bool LooksLikeStructuredDocument(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = NormalizeLines(text);
        if (!normalized.Contains('\n'))
            return false;

        return normalized.Split('\n')
            .Take(40)
            .Count(line => HeadingRegex.IsMatch(line)) >= 2;
    }

    public TaskboardClassificationResult Classify(string text)
    {
        var result = new TaskboardClassificationResult();
        var normalized = NormalizeLines(text);
        var lines = normalized.Split('\n');
        var firstHeadingText = lines
            .Select(line => H1HeadingRegex.Match(line.Trim()))
            .Where(match => match.Success)
            .Select(match => match.Groups["text"].Value.Trim())
            .FirstOrDefault() ?? "";
        var normalizedTitle = NormalizeTitle(firstHeadingText);

        var titlePatternKind = ClassifyTitlePattern(firstHeadingText);
        var startsWithCodexTaskboard = titlePatternKind == TaskboardTitlePatternKind.Preferred;
        var startsWithAcceptedTaskboard = titlePatternKind == TaskboardTitlePatternKind.Accepted;
        var containsTaskboardTitle = titlePatternKind is TaskboardTitlePatternKind.Preferred
            or TaskboardTitlePatternKind.Accepted
            or TaskboardTitlePatternKind.Candidate;
        var hasObjective = lines.Any(line => line.Trim().Equals("## Objective", StringComparison.OrdinalIgnoreCase));
        var batchCount = lines.Count(line =>
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("## ", StringComparison.Ordinal))
                return false;

            return TaskboardGrammarService.TryParseBatchHeading(trimmed[3..].Trim(), out _, out _);
        });
        var hasGuardrails = lines.Any(line => line.Trim().Contains("Guardrails", StringComparison.OrdinalIgnoreCase));
        var hasAcceptance = lines.Any(line => line.Trim().Contains("Acceptance Criteria", StringComparison.OrdinalIgnoreCase));
        var hasInvariants = lines.Any(line => line.Trim().Contains("Invariant", StringComparison.OrdinalIgnoreCase));
        var hasImplementationOrder = lines.Any(line => line.Trim().Contains("Implementation Order", StringComparison.OrdinalIgnoreCase));
        var headingCount = lines.Count(line => HeadingRegex.IsMatch(line));
        var h2Count = lines.Count(line => line.TrimStart().StartsWith("## ", StringComparison.Ordinal));
        var h3Count = lines.Count(line => line.TrimStart().StartsWith("### ", StringComparison.Ordinal));
        var strongTaskboardStructure = batchCount > 0;
        var structuralScore = 0;
        if (hasObjective)
            structuralScore++;
        if (batchCount > 0)
            structuralScore += 3;
        if (hasGuardrails)
            structuralScore++;
        if (hasAcceptance)
            structuralScore++;
        if (hasInvariants)
            structuralScore++;
        if (hasImplementationOrder)
            structuralScore++;
        if (h2Count >= 3)
            structuralScore++;
        if (h3Count > 0)
            structuralScore++;
        if (headingCount >= 4)
            structuralScore++;

        var acceptedCandidate = containsTaskboardTitle
            && (strongTaskboardStructure || (structuralScore >= 4 && headingCount >= 2));

        result.TitlePatternKind = titlePatternKind;
        result.PreferredTitlePatternMatch = startsWithCodexTaskboard;
        result.AcceptedAsTaskboardCandidate = acceptedCandidate;

        if (startsWithCodexTaskboard)
        {
            result.DocumentType = TaskboardDocumentType.CodexTaskboard;
            result.Confidence = strongTaskboardStructure
                ? TaskboardClassificationConfidence.High
                : hasObjective || batchCount > 0 || structuralScore >= 3
                    ? TaskboardClassificationConfidence.Medium
                    : TaskboardClassificationConfidence.Low;
            AddStructuralSignals(result, hasObjective, batchCount, hasGuardrails, hasAcceptance, hasInvariants, hasImplementationOrder, headingCount, h3Count);
            result.MatchedSignals.Insert(0, "preferred_codex_taskboard_title");
            result.Reason = strongTaskboardStructure
                ? "Document matched the preferred Codex taskboard title pattern and canonical batch structure."
                : "Document matched the preferred Codex taskboard title pattern and will continue to parser and validation even though canonical grammar requirements still need checking.";
            return result;
        }

        if (acceptedCandidate)
        {
            result.DocumentType = TaskboardDocumentType.TaskboardCandidate;
            result.Confidence = strongTaskboardStructure
                ? TaskboardClassificationConfidence.High
                : TaskboardClassificationConfidence.Medium;
            AddStructuralSignals(result, hasObjective, batchCount, hasGuardrails, hasAcceptance, hasInvariants, hasImplementationOrder, headingCount, h3Count);
            if (startsWithAcceptedTaskboard)
                result.MatchedSignals.Insert(0, "accepted_taskboard_title");
            else
                result.MatchedSignals.Insert(0, "candidate_taskboard_title");
            result.MissingExpectedSignals.Add("missing_preferred_codex_title_pattern");
            result.Reason = startsWithAcceptedTaskboard
                ? "Document did not match the preferred Codex title pattern, but qualified as a taskboard candidate using the accepted `TASKBOARD` heading format."
                : "Document did not match the preferred Codex title pattern, but qualified as a taskboard candidate because the title mentions `Taskboard` and canonical batch structure is present.";
            return result;
        }

        if (headingCount >= 2)
        {
            result.DocumentType = containsTaskboardTitle || hasObjective || batchCount > 0
                ? TaskboardDocumentType.UnsupportedStructuredDocument
                : TaskboardDocumentType.Unknown;
            result.Confidence = headingCount >= 4 || structuralScore >= 3
                ? TaskboardClassificationConfidence.Medium
                : TaskboardClassificationConfidence.Low;
            AddStructuralSignals(result, hasObjective, batchCount, hasGuardrails, hasAcceptance, hasInvariants, hasImplementationOrder, headingCount, h3Count);
            if (containsTaskboardTitle)
                result.MatchedSignals.Insert(0, $"title_pattern={titlePatternKind.ToString().ToLowerInvariant()}");
            else if (!string.IsNullOrWhiteSpace(normalizedTitle))
                result.MissingExpectedSignals.Add("missing_taskboard_title_signal");
            if (!startsWithCodexTaskboard)
                result.MissingExpectedSignals.Add("missing_preferred_codex_title_pattern");

            result.Reason = BuildUnsupportedReason(containsTaskboardTitle, hasObjective, batchCount, structuralScore);

            return result;
        }

        result.DocumentType = TaskboardDocumentType.PlainRequest;
        result.Confidence = TaskboardClassificationConfidence.High;
        result.Reason = "The input does not look like a supported structured taskboard document.";
        return result;
    }

    private static TaskboardTitlePatternKind ClassifyTitlePattern(string headingText)
    {
        var normalized = NormalizeTitle(headingText);
        if (string.IsNullOrWhiteSpace(normalized))
            return TaskboardTitlePatternKind.Unknown;

        if (normalized.StartsWith("CODEX TASKBOARD", StringComparison.OrdinalIgnoreCase))
            return TaskboardTitlePatternKind.Preferred;

        if (normalized.StartsWith("TASKBOARD", StringComparison.OrdinalIgnoreCase))
            return TaskboardTitlePatternKind.Accepted;

        return normalized.Contains("TASKBOARD", StringComparison.OrdinalIgnoreCase)
            ? TaskboardTitlePatternKind.Candidate
            : TaskboardTitlePatternKind.Other;
    }

    private static void AddStructuralSignals(
        TaskboardClassificationResult result,
        bool hasObjective,
        int batchCount,
        bool hasGuardrails,
        bool hasAcceptance,
        bool hasInvariants,
        bool hasImplementationOrder,
        int headingCount,
        int h3Count)
    {
        if (hasObjective)
            result.MatchedSignals.Add("has_objective_heading");

        if (batchCount > 0)
            result.MatchedSignals.Add($"batch_heading_count={batchCount}");
        else
            result.MissingExpectedSignals.Add("missing_batch_heading");

        if (hasGuardrails)
            result.MatchedSignals.Add("has_guardrails_signal");
        if (hasAcceptance)
            result.MatchedSignals.Add("has_acceptance_signal");
        if (hasInvariants)
            result.MatchedSignals.Add("has_invariants_signal");
        if (hasImplementationOrder)
            result.MatchedSignals.Add("has_implementation_order_signal");
        if (headingCount >= 3)
            result.MatchedSignals.Add($"heading_count={headingCount}");
        if (h3Count > 0)
            result.MatchedSignals.Add($"h3_heading_count={h3Count}");
    }

    private static string BuildUnsupportedReason(bool containsTaskboardTitle, bool hasObjective, int batchCount, int structuralScore)
    {
        if (containsTaskboardTitle && batchCount == 0)
            return "The document title mentions taskboard, but it does not include any canonical `## Batch N — Name` sections.";

        if (!containsTaskboardTitle && batchCount > 0)
            return "The document has batch sections, but the H1 title does not identify it as a taskboard candidate.";

        if (!containsTaskboardTitle && (hasObjective || batchCount > 0 || structuralScore >= 3))
            return "The document contains some taskboard-like structure, but it does not qualify as a supported taskboard candidate.";

        return "The document contains markdown structure, but not enough supported taskboard signals to treat it as a taskboard candidate.";
    }

    private static string NormalizeLines(string text)
    {
        return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static string NormalizeTitle(string title)
    {
        return Regex.Replace((title ?? "").Trim(), @"\s+", " ");
    }
}
