using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class VerificationOutcomeComparer
{
    public VerificationOutcomeRecord Compare(
        string workspaceRoot,
        VerificationPlanRecord plan,
        PatchApplyResultRecord applyRecord,
        WorkspaceExecutionStateRecord beforeState,
        ToolResult? verificationResult)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (plan is null)
            throw new ArgumentNullException(nameof(plan));

        if (applyRecord is null)
            throw new ArgumentNullException(nameof(applyRecord));

        var outcome = new VerificationOutcomeRecord
        {
            VerificationOutcomeId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            VerificationPlanId = plan.VerificationPlanId,
            SourcePatchDraftId = applyRecord.Draft.DraftId,
            SourceRepairProposalId = applyRecord.Draft.SourceProposalId,
            ExecutedTool = plan.VerificationTool,
            ResolvedTarget = plan.TargetPath,
            PatchContractId = FirstNonEmpty(applyRecord.PatchContractId, applyRecord.Draft.PatchContractId, plan.SourcePatchContractId),
            PatchPlanId = FirstNonEmpty(applyRecord.PatchPlanId, applyRecord.Draft.PatchPlanId, plan.SourcePatchPlanId),
            MutationFamily = applyRecord.Draft.MutationFamily,
            AllowedEditScope = applyRecord.Draft.AllowedEditScope,
            WarningPolicyMode = FirstNonEmpty(plan.WarningPolicyMode, applyRecord.Draft.WarningPolicyMode, "track_only"),
            BeforeSummary = FirstNonEmpty(applyRecord.Draft.FailureSummary, beforeState.LastFailureSummary),
            AfterSummary = "",
            Explanation = ""
        };

        if (string.Equals(plan.VerificationTool, "read_only_check", StringComparison.OrdinalIgnoreCase)
            || verificationResult is null)
        {
            outcome.OutcomeClassification = "not_verifiable";
            outcome.AfterSummary = "No executable verification step was run.";
            outcome.Explanation = plan.Rationale;
            return outcome;
        }

        outcome.ExecutedTool = FirstNonEmpty(verificationResult.ToolName, plan.VerificationTool);
        outcome.ResolvedTarget = FirstNonEmpty(plan.TargetPath, ExtractTargetPath(verificationResult.StructuredDataJson));
        outcome.AfterSummary = verificationResult.Success
            ? FirstNonEmpty(verificationResult.Summary, verificationResult.Output)
            : FirstNonEmpty(verificationResult.Summary, verificationResult.ErrorMessage);

        if (verificationResult.OutcomeType is "execution_failure" or "resolution_failure" or "validation_failure" or "timed_out" or "output_limit_exceeded" or "safety_blocked")
        {
            outcome.OutcomeClassification = "verification_failed";
            outcome.Explanation = FirstNonEmpty(
                verificationResult.Summary,
                verificationResult.ErrorMessage,
                "The verification command did not complete successfully.");
            return outcome;
        }

        if (string.Equals(plan.FailureKind, "build_failure", StringComparison.OrdinalIgnoreCase))
            return CompareBuildVerification(outcome, beforeState, verificationResult);

        if (string.Equals(plan.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase))
            return CompareTestVerification(outcome, beforeState, verificationResult);

        outcome.OutcomeClassification = verificationResult.Success ? "verified_fixed" : "verification_failed";
        outcome.Explanation = verificationResult.Success
            ? "The verification step completed successfully after the patch was applied."
            : "The verification step did not produce a usable result.";
        return outcome;
    }

    private static VerificationOutcomeRecord CompareBuildVerification(
        VerificationOutcomeRecord outcome,
        WorkspaceExecutionStateRecord beforeState,
        ToolResult verificationResult)
    {
        var beforeParsed = DeserializeParsedSection<DotnetBuildParseResult>(beforeState.LastFailureDataJson);
        var afterParsed = DeserializeParsedSection<DotnetBuildParseResult>(verificationResult.StructuredDataJson);

        outcome.BeforeFailureCount = beforeParsed?.ErrorCount;
        outcome.AfterFailureCount = verificationResult.Success ? 0 : afterParsed?.ErrorCount;
        outcome.FailureCountDelta = BuildDelta(outcome.BeforeFailureCount, outcome.AfterFailureCount);
        outcome.BeforeWarningCount = beforeParsed?.WarningCount;
        outcome.AfterWarningCount = afterParsed?.WarningCount ?? (verificationResult.Success ? 0 : null);
        outcome.WarningCountDelta = BuildDelta(outcome.BeforeWarningCount, outcome.AfterWarningCount);
        outcome.WarningCodes = ExtractWarningCodes(afterParsed);
        outcome.TopRemainingFailures = afterParsed?.TopErrors
            .Where(error => string.Equals(error.Severity, "error", StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .Select(error =>
            {
                var location = string.IsNullOrWhiteSpace(error.FilePath) ? DisplayValue(error.RawPath) : error.FilePath;
                return $"{location}:{error.LineNumber} {error.Code} {error.Message}".Trim();
            })
            .ToList() ?? [];

        if (verificationResult.Success)
        {
            outcome.OutcomeClassification = "verified_fixed";
            outcome.Explanation = "The selected build target succeeded after the patch was applied.";
            return outcome;
        }

        outcome.OutcomeClassification = IsImproved(outcome.BeforeFailureCount, outcome.AfterFailureCount)
            ? "partially_improved"
            : "still_failing";
        outcome.Explanation = outcome.OutcomeClassification == "partially_improved"
            ? "The build still fails, but the number of compiler errors dropped after the patch."
            : "The build still fails on the selected verification target after the patch.";
        return outcome;
    }

    private static VerificationOutcomeRecord CompareTestVerification(
        VerificationOutcomeRecord outcome,
        WorkspaceExecutionStateRecord beforeState,
        ToolResult verificationResult)
    {
        var beforeParsed = DeserializeParsedSection<DotnetTestParseResult>(beforeState.LastFailureDataJson);
        var afterParsed = DeserializeParsedSection<DotnetTestParseResult>(verificationResult.StructuredDataJson);

        outcome.BeforeFailureCount = beforeParsed?.FailedCount;
        outcome.AfterFailureCount = verificationResult.Success ? 0 : afterParsed?.FailedCount;
        outcome.FailureCountDelta = BuildDelta(outcome.BeforeFailureCount, outcome.AfterFailureCount);
        outcome.TopRemainingFailures = afterParsed?.FailingTests
            .Take(5)
            .Select(failure => string.IsNullOrWhiteSpace(failure.ResolvedSourcePath)
                ? failure.TestName
                : $"{failure.TestName} [{failure.ResolvedSourcePath}:{failure.SourceLine}]")
            .ToList() ?? [];

        if (verificationResult.Success)
        {
            outcome.OutcomeClassification = "verified_fixed";
            outcome.Explanation = "The selected test target passed after the patch was applied.";
            return outcome;
        }

        outcome.OutcomeClassification = IsImproved(outcome.BeforeFailureCount, outcome.AfterFailureCount)
            ? "partially_improved"
            : "still_failing";
        outcome.Explanation = outcome.OutcomeClassification == "partially_improved"
            ? "The test target still fails, but fewer tests are failing after the patch."
            : "The test target still fails after the patch.";
        return outcome;
    }

    private static List<string> ExtractWarningCodes(DotnetBuildParseResult? parsed)
    {
        if (parsed is null)
            return [];

        return parsed.BuildLocations
            .Where(location =>
                string.Equals(location.Severity, "warning", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(location.Code))
            .Select(location => location.Code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static int? BuildDelta(int? beforeCount, int? afterCount)
    {
        return beforeCount.HasValue && afterCount.HasValue
            ? afterCount.Value - beforeCount.Value
            : null;
    }

    private static bool IsImproved(int? beforeCount, int? afterCount)
    {
        return beforeCount.HasValue
            && afterCount.HasValue
            && afterCount.Value > 0
            && afterCount.Value < beforeCount.Value;
    }

    private static string ExtractTargetPath(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("target_path", out var targetPath))
                return targetPath.GetString() ?? "";
        }
        catch
        {
            return "";
        }

        return "";
    }

    private static T? DeserializeParsedSection<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("parsed", out var parsedElement))
                return parsedElement.Deserialize<T>();
        }
        catch
        {
            return default;
        }

        return default;
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

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(unknown)" : value;
    }
}
