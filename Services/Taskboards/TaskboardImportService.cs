using System.Security.Cryptography;
using System.Text;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardImportService
{
    private const int MaxCharacterCount = 120_000;

    private readonly TaskboardArtifactStore _artifactStore = new();
    private readonly TaskboardDocumentClassifier _classifier = new();
    private readonly TaskboardMarkdownParser _markdownParser = new();
    private readonly TaskboardModelBuilder _modelBuilder = new();
    private readonly TaskboardValidator _validator = new();

    public bool LooksLikeStructuredDocument(string text)
    {
        return _classifier.LooksLikeStructuredDocument(text);
    }

    public TaskboardIntakeResult ImportFromText(string workspaceRoot, string rawText, string sourceType, RamDbService ramDbService)
    {
        var result = new TaskboardIntakeResult();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            result.Category = TaskboardIntakeResultCategory.EmptyInput;
            result.Message = "Taskboard import rejected because the input was empty.";
            return result;
        }

        if (rawText.Length > MaxCharacterCount)
        {
            result.Category = TaskboardIntakeResultCategory.TooLarge;
            result.Message = $"Taskboard import rejected because the input exceeds the {MaxCharacterCount}-character limit.";
            return result;
        }

        try
        {
            var contentHash = ComputeContentHash(rawText);
            var importId = Guid.NewGuid().ToString("N");
            var classification = _classifier.Classify(rawText);
            result.Category = TaskboardIntakeResultCategory.AcceptedForClassification;
            result.Classification = classification;

            var record = new TaskboardImportRecord
            {
                ImportId = importId,
                WorkspaceRoot = workspaceRoot,
                SourceType = sourceType ?? "paste",
                ContentHash = contentHash,
                DocumentType = classification.DocumentType,
                TitlePatternKind = classification.TitlePatternKind,
                PreferredTitlePatternMatch = classification.PreferredTitlePatternMatch,
                AcceptedAsTaskboardCandidate = classification.AcceptedAsTaskboardCandidate,
                ClassificationConfidence = classification.Confidence,
                MatchedSignals = [.. classification.MatchedSignals],
                MissingExpectedSignals = [.. classification.MissingExpectedSignals],
                ClassificationReason = classification.Reason,
                State = TaskboardImportState.Imported,
                ValidationOutcome = TaskboardValidationOutcome.ValidationException,
                ValidationSummary = "Taskboard intake started.",
                ParserVersion = TaskboardMarkdownParser.ParserVersion,
                ModelBuilderVersion = TaskboardModelBuilder.ModelBuilderVersion,
                ValidatorVersion = TaskboardValidator.ValidatorVersion,
                CreatedUtc = DateTime.UtcNow.ToString("O"),
                UpdatedUtc = DateTime.UtcNow.ToString("O"),
                Title = ExtractCandidateTitle(rawText)
            };

            var rawArtifact = _artifactStore.SaveRawArtifact(ramDbService, workspaceRoot, importId, record.Title, rawText, contentHash, record.SourceType);
            record.RawArtifactId = rawArtifact.Id;

            if (!classification.IsSupportedTaskboard)
            {
                record.State = TaskboardImportState.Rejected;
                record.ValidationOutcome = TaskboardValidationOutcome.UnsupportedStructure;
                record.ValidationSummary = classification.Reason;
                _artifactStore.SaveImportRecordArtifact(ramDbService, workspaceRoot, record);

                result.ImportRecord = record;
                result.Category = classification.DocumentType == TaskboardDocumentType.PlainRequest
                    ? TaskboardIntakeResultCategory.UnsupportedDocument
                    : TaskboardIntakeResultCategory.MalformedInput;
                result.Message = classification.Reason;
                return result;
            }

            var parseResult = _markdownParser.Parse(rawText);
            result.ParseResult = parseResult;
            ApplyClassificationMetadata(parseResult.Document, classification);
            record.State = TaskboardImportState.Parsed;
            record.ParsedArtifactId = _artifactStore.SaveParsedArtifact(ramDbService, workspaceRoot, importId, record.Title, parseResult).Id;

            TaskboardDocument? document = null;
            if (parseResult.Success && parseResult.Document is not null)
            {
                record.Title = string.IsNullOrWhiteSpace(parseResult.Document.Title)
                    ? record.Title
                    : parseResult.Document.Title;
                document = _modelBuilder.Build(importId, workspaceRoot, contentHash, parseResult.Document, parseResult.ParserVersion);
                result.PlanDocument = document;
                record.PlanArtifactId = _artifactStore.SavePlanArtifact(ramDbService, workspaceRoot, importId, document).Id;
            }

            var validation = _validator.Validate(document, parseResult);
            result.ValidationReport = validation;
            record.State = TaskboardImportState.Validated;
            record.ValidationOutcome = validation.Outcome;
            record.ValidationSummary = BuildValidationSummary(validation);
            record.ValidationErrorCount = validation.Errors.Count;
            record.ValidationWarningCount = validation.Warnings.Count;
            record.ValidationArtifactId = _artifactStore.SaveValidationArtifact(ramDbService, workspaceRoot, importId, record.Title, validation).Id;
            record.UpdatedUtc = DateTime.UtcNow.ToString("O");

            if (validation.CanPromote)
            {
                record.State = TaskboardImportState.ReadyForPromotion;
                var headingNormalizationMessage = parseResult.Diagnostics
                    .FirstOrDefault(diagnostic => IsHeadingNormalizationDiagnostic(diagnostic.Code))?
                    .Message;
                result.Message = BuildSuccessMessage(record.Title, classification, headingNormalizationMessage);
            }
            else
            {
                record.State = TaskboardImportState.Rejected;
                result.Category = TaskboardIntakeResultCategory.MalformedInput;
                result.Message = BuildFailureMessage(record.Title, classification, parseResult, validation);
            }

            _artifactStore.SaveImportRecordArtifact(ramDbService, workspaceRoot, record);
            result.ImportRecord = record;
            return result;
        }
        catch (Exception ex)
        {
            result.Category = TaskboardIntakeResultCategory.IntakeException;
            result.Message = $"Taskboard intake failed: {ex.Message}";
            return result;
        }
    }

    private static string ComputeContentHash(string rawText)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawText ?? ""));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ExtractCandidateTitle(string rawText)
    {
        var normalized = (rawText ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var firstHeading = normalized.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("#", StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(firstHeading)
            ? "Imported taskboard"
            : firstHeading.TrimStart('#').Trim();
    }

    private static void ApplyClassificationMetadata(TaskboardMarkdownDocument? document, TaskboardClassificationResult classification)
    {
        if (document is null)
            return;

        document.TitlePatternKind = classification.TitlePatternKind;
        document.RawTitle = string.IsNullOrWhiteSpace(document.RawTitle) ? document.Title : document.RawTitle;
        document.NormalizedTitle = string.IsNullOrWhiteSpace(document.NormalizedTitle)
            ? NormalizeTitle(document.Title)
            : NormalizeTitle(document.NormalizedTitle);
        document.Title = string.IsNullOrWhiteSpace(document.NormalizedTitle)
            ? document.Title
            : document.NormalizedTitle;
    }

    private static string BuildValidationSummary(TaskboardValidationReport validation)
    {
        return validation.CanPromote
            ? $"validation={validation.Outcome.ToString().ToLowerInvariant()} warnings={validation.Warnings.Count}"
            : $"validation={validation.Outcome.ToString().ToLowerInvariant()} errors={validation.Errors.Count}";
    }

    private static string BuildFailureMessage(
        string title,
        TaskboardClassificationResult classification,
        TaskboardParseResult parseResult,
        TaskboardValidationReport validation)
    {
        var parserError = parseResult.Diagnostics
            .FirstOrDefault(diagnostic => diagnostic.Severity == TaskboardParserDiagnosticSeverity.Error);
        if (parserError is not null)
        {
            var lineSuffix = parserError.LineNumber > 0 ? $" line {parserError.LineNumber}" : "";
            var codeSuffix = string.IsNullOrWhiteSpace(parserError.Code) ? "" : $" [{parserError.Code}]";
            return $"Taskboard candidate `{title}` reached parser validation, but parsing failed{lineSuffix}{codeSuffix}: {parserError.Message}";
        }

        var validationEntry = validation.Errors.FirstOrDefault();
        var validationError = validationEntry?.Message ?? validation.Outcome.ToString();
        var validationLineSuffix = validationEntry is not null && validationEntry.LineNumber > 0
            ? $" line {validationEntry.LineNumber}"
            : "";
        var validationCodeSuffix = validationEntry is not null && !string.IsNullOrWhiteSpace(validationEntry.Code)
            ? $" [{validationEntry.Code}]"
            : "";
        return classification.AcceptedAsTaskboardCandidate
            ? $"Taskboard candidate `{title}` was accepted by the classifier, but validation rejected it{validationLineSuffix}{validationCodeSuffix}: {validationError}"
            : $"Imported taskboard `{title}` but validation rejected it{validationLineSuffix}{validationCodeSuffix}: {validationError}";
    }

    private static string BuildSuccessMessage(
        string title,
        TaskboardClassificationResult classification,
        string? headingNormalizationMessage)
    {
        var headingNote = string.IsNullOrWhiteSpace(headingNormalizationMessage)
            ? ""
            : $" {headingNormalizationMessage}";

        return classification.PreferredTitlePatternMatch
            ? $"Imported taskboard `{title}` and validated it for promotion.{headingNote}".Trim()
            : $"Imported taskboard `{title}` and validated it for promotion using an accepted non-preferred heading format.{headingNote}".Trim();
    }

    private static bool IsHeadingNormalizationDiagnostic(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        return string.Equals(code, "detail_heading_normalized", StringComparison.OrdinalIgnoreCase)
            || (code.StartsWith("heading_level_", StringComparison.OrdinalIgnoreCase)
                && code.EndsWith("_normalized", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeTitle(string title)
    {
        return string.Join(" ", (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
