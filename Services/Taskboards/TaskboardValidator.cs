using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardValidator
{
    public const string ValidatorVersion = "3";

    private const int MaxBatchCount = 40;
    private const int MaxStepCount = 240;
    private const int MaxFlattenedSectionLength = 12000;

    private static readonly Regex UnsafeAuthorityRegex = new(
        @"^\s*(?:bypass|override|disable|ignore|remove)\s+(?:the\s+)?(?:execution\s+gate|safety\s+policy|manual-only\s+rules|response-mode\s+enforcement|ram-owned\s+routing)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public TaskboardValidationReport Validate(TaskboardDocument? document, TaskboardParseResult? parseResult)
    {
        var report = new TaskboardValidationReport
        {
            ValidatorVersion = ValidatorVersion,
            GrammarVersion = TaskboardGrammarService.GrammarVersion,
            CanonicalFormHint = TaskboardGrammarService.BuildCanonicalFormHint(),
            CanonicalExamples = TaskboardGrammarService.BuildCanonicalExamples()
        };

        try
        {
            foreach (var diagnostic in parseResult?.Diagnostics ?? [])
            {
                AddMessage(
                    report,
                    diagnostic.Severity == TaskboardParserDiagnosticSeverity.Error
                        ? TaskboardValidationSeverity.Error
                        : TaskboardValidationSeverity.Warning,
                    diagnostic.Code,
                    "",
                    diagnostic.Message,
                    diagnostic.LineNumber,
                    diagnostic.OffendingText,
                    diagnostic.LineClassification.ToString().ToLowerInvariant(),
                    diagnostic.ExpectedGrammar);
            }

            if (document is null)
            {
                var parserHadErrors = parseResult?.Diagnostics.Any(diagnostic => diagnostic.Severity == TaskboardParserDiagnosticSeverity.Error) == true;
                if (!parserHadErrors)
                {
                    AddMessage(report, TaskboardValidationSeverity.Error, "missing_document", "", "No parsed taskboard document was available for validation.");
                }
                FinalizeOutcome(report);
                return report;
            }

            if (string.IsNullOrWhiteSpace(document.Title))
            {
                AddMessage(
                    report,
                    TaskboardValidationSeverity.Error,
                    "missing_h1_title",
                    document.DocumentId,
                    "The taskboard is missing the required H1 title.");
            }

            if (document.TitlePatternKind != TaskboardTitlePatternKind.Preferred)
            {
                AddMessage(
                    report,
                    TaskboardValidationSeverity.Warning,
                    "preferred_title_pattern_recommended",
                    document.DocumentId,
                    "The taskboard title does not match the preferred `CODEX TASKBOARD` format, but the structure is accepted.");
            }

            if (document.Batches.Count == 0)
            {
                AddMessage(
                    report,
                    TaskboardValidationSeverity.Error,
                    "missing_batch_section",
                    document.DocumentId,
                    "The taskboard must contain at least one `## Batch N — Name` section.");
            }

            if (document.Batches.Count > MaxBatchCount)
            {
                AddMessage(report, TaskboardValidationSeverity.Error, "text_limit_exceeded", document.DocumentId, $"The taskboard exceeds the supported batch limit of {MaxBatchCount}.");
            }

            var duplicateNumbers = document.Batches
                .GroupBy(batch => batch.BatchNumber)
                .Where(group => group.Key > 0 && group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            foreach (var duplicate in duplicateNumbers)
            {
                AddMessage(
                    report,
                    TaskboardValidationSeverity.Error,
                    "duplicate_batch",
                    document.DocumentId,
                    $"Duplicate batch number detected: Batch {duplicate}.");
            }

            var duplicateTitles = document.Batches
                .GroupBy(batch => batch.Title.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            foreach (var duplicateTitle in duplicateTitles)
            {
                AddMessage(
                    report,
                    TaskboardValidationSeverity.Error,
                    "duplicate_batch",
                    document.DocumentId,
                    $"Duplicate batch title detected: {duplicateTitle}.");
            }

            var stepCount = document.Batches.Sum(batch => batch.Steps.Count);
            if (stepCount > MaxStepCount)
            {
                AddMessage(report, TaskboardValidationSeverity.Error, "text_limit_exceeded", document.DocumentId, $"The taskboard exceeds the supported step limit of {MaxStepCount}.");
            }

            foreach (var batch in document.Batches)
            {
                if (batch.Steps.Count == 0)
                {
                    AddMessage(
                        report,
                        TaskboardValidationSeverity.Error,
                        "missing_executable_step",
                        batch.BatchId,
                        $"Batch {batch.BatchNumber} does not contain any executable `- command` bullet steps.",
                        batch.Content.SourceLineStart,
                        batch.Title,
                        "batch_heading",
                        "After each `## Batch N — Name` heading, use one or more `- command` bullet steps.");
                }

                if (batch.Content.Paragraphs.Count > 0)
                {
                    AddMessage(
                        report,
                        TaskboardValidationSeverity.Error,
                        "batch_contains_non_executable_prose",
                        batch.BatchId,
                        $"Batch {batch.BatchNumber} contains prose paragraphs mixed into executable content.",
                        batch.Content.SourceLineStart,
                        batch.Title,
                        "batch_heading",
                        TaskboardGrammarService.BuildExpectedBatchContentGrammar());
                }

                if (batch.Content.NumberedItems.Count > 0)
                {
                    AddMessage(
                        report,
                        TaskboardValidationSeverity.Error,
                        "unsupported_executable_step_format",
                        batch.BatchId,
                        $"Batch {batch.BatchNumber} contains numbered list items. Executable steps must use `- command` bullets.",
                        batch.Content.SourceLineStart,
                        batch.Title,
                        "batch_heading",
                        "Inside a batch, use `- command` bullet steps only.");
                }

                if (batch.Content.Subsections.Count > 0)
                {
                    AddMessage(
                        report,
                        TaskboardValidationSeverity.Error,
                        "unsupported_nested_section_under_batch",
                        batch.BatchId,
                        $"Batch {batch.BatchNumber} contains nested sections. Nested headings are not executable batch content.",
                        batch.Content.SourceLineStart,
                        batch.Title,
                        "batch_heading",
                        TaskboardGrammarService.BuildExpectedBatchContentGrammar());
                }

                ValidateSectionLengths(report, batch.BatchId, batch.Content);
                foreach (var step in batch.Steps)
                {
                    if (string.IsNullOrWhiteSpace(step.Title))
                    {
                        AddMessage(report, TaskboardValidationSeverity.Error, "empty_step_title", step.StepId, "Executable batch steps must not be empty.");
                    }

                    ValidateSectionLengths(report, step.StepId, step.Content);
                }
            }

            foreach (var section in document.AdditionalSections)
                ValidateSectionLengths(report, section.SectionId, section);

            foreach (var unsafeFinding in EnumerateUnsafeLines(document))
            {
                AddMessage(report, TaskboardValidationSeverity.Error, "unsafe_content_detected", unsafeFinding.SectionId, unsafeFinding.Message);
            }

            FinalizeOutcome(report);
            return report;
        }
        catch (Exception ex)
        {
            AddMessage(report, TaskboardValidationSeverity.Error, "validation_exception", "", ex.Message);
            FinalizeOutcome(report);
            return report;
        }
    }

    private static void ValidateSectionLengths(TaskboardValidationReport report, string sectionId, TaskboardSectionContent section)
    {
        var flattened = FlattenSectionText(section);
        if (flattened.Length > MaxFlattenedSectionLength)
            AddMessage(report, TaskboardValidationSeverity.Error, "text_limit_exceeded", sectionId, $"Section `{section.Title}` exceeds the supported text limit.");

        foreach (var subsection in section.Subsections)
            ValidateSectionLengths(report, subsection.SectionId, subsection);
    }

    private static IEnumerable<(string SectionId, string Message)> EnumerateUnsafeLines(TaskboardDocument document)
    {
        foreach (var section in EnumerateSections(document))
        {
            foreach (var line in EnumerateLines(section))
            {
                if (UnsafeAuthorityRegex.IsMatch(line))
                    yield return (section.SectionId, $"Unsafe authority override text cannot be promoted into trusted plan state: {line}");
            }
        }
    }

    private static IEnumerable<TaskboardSectionContent> EnumerateSections(TaskboardDocument document)
    {
        foreach (var bucket in document.Guardrails.Buckets)
            yield return bucket;
        foreach (var section in document.AcceptanceCriteria.Sections)
            yield return section;
        foreach (var section in document.Invariants.Sections)
            yield return section;
        foreach (var batch in document.Batches)
        {
            yield return batch.Content;
            foreach (var step in batch.Steps)
                yield return step.Content;
        }
        foreach (var section in document.AdditionalSections)
            yield return section;
    }

    private static IEnumerable<string> EnumerateLines(TaskboardSectionContent section)
    {
        foreach (var paragraph in section.Paragraphs)
            yield return paragraph;
        foreach (var item in section.BulletItems)
            yield return item;
        foreach (var item in section.NumberedItems)
            yield return item;
        foreach (var subsection in section.Subsections)
        {
            foreach (var line in EnumerateLines(subsection))
                yield return line;
        }
    }

    private static string FlattenSectionText(TaskboardSectionContent section)
    {
        return string.Join(Environment.NewLine, EnumerateLines(section).Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static void FinalizeOutcome(TaskboardValidationReport report)
    {
        if (report.Errors.Count == 0 && report.Warnings.Count == 0)
        {
            report.Outcome = TaskboardValidationOutcome.Valid;
            return;
        }

        if (report.Errors.Count == 0)
        {
            report.Outcome = TaskboardValidationOutcome.ValidWithWarnings;
            return;
        }

        if (report.Errors.Any(error => string.Equals(error.Code, "duplicate_batch", StringComparison.OrdinalIgnoreCase)))
        {
            report.Outcome = TaskboardValidationOutcome.DuplicateBatch;
            return;
        }

        if (report.Errors.Any(error =>
                string.Equals(error.Code, "missing_h1_title", StringComparison.OrdinalIgnoreCase)
                || string.Equals(error.Code, "missing_batch_section", StringComparison.OrdinalIgnoreCase)))
        {
            report.Outcome = TaskboardValidationOutcome.MissingRequiredSection;
            return;
        }

        if (report.Errors.Any(error => string.Equals(error.Code, "unsafe_content_detected", StringComparison.OrdinalIgnoreCase)))
        {
            report.Outcome = TaskboardValidationOutcome.UnsafeContentDetected;
            return;
        }

        if (report.Errors.Any(error => string.Equals(error.Code, "text_limit_exceeded", StringComparison.OrdinalIgnoreCase)))
        {
            report.Outcome = TaskboardValidationOutcome.TextLimitExceeded;
            return;
        }

        if (report.Errors.Any(error => string.Equals(error.Code, "validation_exception", StringComparison.OrdinalIgnoreCase)))
        {
            report.Outcome = TaskboardValidationOutcome.ValidationException;
            return;
        }

        report.Outcome = TaskboardValidationOutcome.UnsupportedStructure;
    }

    private static void AddMessage(
        TaskboardValidationReport report,
        TaskboardValidationSeverity severity,
        string code,
        string sectionId,
        string message,
        int lineNumber = 0,
        string offendingText = "",
        string lineClassification = "",
        string expectedGrammar = "")
    {
        var entry = new TaskboardValidationMessage
        {
            Severity = severity,
            Code = code,
            SectionId = sectionId ?? "",
            LineNumber = lineNumber,
            OffendingText = offendingText ?? "",
            LineClassification = lineClassification ?? "",
            ExpectedGrammar = expectedGrammar ?? "",
            Message = message ?? ""
        };

        if (severity == TaskboardValidationSeverity.Error)
            report.Errors.Add(entry);
        else
            report.Warnings.Add(entry);
    }
}
