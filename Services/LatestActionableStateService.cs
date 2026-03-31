using RAM.Models;

namespace RAM.Services;

public sealed class LatestActionableStateService
{
    private readonly ArtifactClassificationService _artifactClassificationService;

    public LatestActionableStateService(ArtifactClassificationService artifactClassificationService)
    {
        _artifactClassificationService = artifactClassificationService;
    }

    public LatestActionableStateRecord GetLatestState(string workspaceRoot, RamDbService ramDbService)
    {
        var state = ramDbService.LoadExecutionState(workspaceRoot);
        var recentArtifacts = ramDbService.LoadLatestArtifacts(workspaceRoot, 40);
        var latestFailureArtifact = recentArtifacts.FirstOrDefault(_artifactClassificationService.IsFailureArtifact);
        var latestRepairArtifact = recentArtifacts.FirstOrDefault(_artifactClassificationService.IsRepairArtifact);
        var latestPatchArtifact = recentArtifacts.FirstOrDefault(_artifactClassificationService.IsPatchArtifact);
        var latestVerificationArtifact = recentArtifacts.FirstOrDefault(_artifactClassificationService.IsVerificationArtifact);
        var latestAutoValidationArtifact = recentArtifacts.FirstOrDefault(_artifactClassificationService.IsAutoValidationArtifact);
        var latestSafetyArtifact = recentArtifacts.FirstOrDefault(_artifactClassificationService.IsSafetyArtifact);

        var record = new LatestActionableStateRecord
        {
            WorkspaceRoot = workspaceRoot,
            ExecutionState = state,
            LatestFailureArtifact = latestFailureArtifact,
            LatestRepairArtifact = latestRepairArtifact,
            LatestPatchArtifact = latestPatchArtifact,
            LatestVerificationArtifact = latestVerificationArtifact,
            LatestAutoValidationArtifact = latestAutoValidationArtifact,
            LatestSafetyArtifact = latestSafetyArtifact,
            HasFailureContext = _artifactClassificationService.HasRecordedFailureState(state) || latestFailureArtifact is not null,
            HasRepairContext = latestRepairArtifact is not null,
            HasRepairChain = latestRepairArtifact is not null || latestPatchArtifact is not null || latestVerificationArtifact is not null,
            HasSafetyAbort = _artifactClassificationService.IsSafetyOutcomeType(state.LastFailureOutcomeType) || latestSafetyArtifact is not null,
            HasAutoValidationResult = latestAutoValidationArtifact is not null,
            HasSuccessfulBuild = !string.IsNullOrWhiteSpace(state.LastSuccessUtc)
                && !string.IsNullOrWhiteSpace(state.LastSuccessToolName)
                && !string.Equals(state.LastSuccessToolName, "apply_patch_draft", StringComparison.OrdinalIgnoreCase)
        };

        PopulateLatestResult(record);
        return record;
    }

    private void PopulateLatestResult(LatestActionableStateRecord record)
    {
        var state = record.ExecutionState;
        record.LatestBuildFamily = FirstNonEmpty(
            state.LastSelectedBuildProfileType,
            state.LastBuildToolFamily,
            state.LastFailureToolName,
            state.LastSuccessToolName);
        record.LatestBuildTarget = FirstNonEmpty(
            state.LastFailureTargetPath,
            state.LastSuccessTargetPath,
            state.LastVerificationTargetPath);

        if (IsVerificationNewer(state))
        {
            record.LatestResultKind = "verification";
            record.LatestResultToolName = state.LastVerificationToolName;
            record.LatestResultTargetPath = state.LastVerificationTargetPath;
            record.LatestResultOutcomeType = state.LastVerificationOutcomeType;
            record.LatestResultSummary = state.LastVerificationSummary;
            return;
        }

        if (IsFailureNewer(state))
        {
            record.LatestResultKind = _artifactClassificationService.IsSafetyOutcomeType(state.LastFailureOutcomeType)
                ? "safety_abort"
                : "failure";
            record.LatestResultToolName = state.LastFailureToolName;
            record.LatestResultTargetPath = state.LastFailureTargetPath;
            record.LatestResultOutcomeType = state.LastFailureOutcomeType;
            record.LatestResultSummary = state.LastFailureSummary;
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.LastSuccessUtc))
        {
            record.LatestResultKind = "success";
            record.LatestResultToolName = state.LastSuccessToolName;
            record.LatestResultTargetPath = state.LastSuccessTargetPath;
            record.LatestResultOutcomeType = state.LastSuccessOutcomeType;
            record.LatestResultSummary = state.LastSuccessSummary;
            return;
        }

        record.LatestResultKind = "none";
    }

    private static bool IsVerificationNewer(WorkspaceExecutionStateRecord state)
    {
        if (string.IsNullOrWhiteSpace(state.LastVerificationUtc))
            return false;

        return ParseUtc(state.LastVerificationUtc) >= ParseUtc(state.LastFailureUtc)
            && ParseUtc(state.LastVerificationUtc) >= ParseUtc(state.LastSuccessUtc);
    }

    private static bool IsFailureNewer(WorkspaceExecutionStateRecord state)
    {
        if (string.IsNullOrWhiteSpace(state.LastFailureUtc))
            return false;

        return ParseUtc(state.LastFailureUtc) >= ParseUtc(state.LastSuccessUtc);
    }

    private static DateTime ParseUtc(string value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed
            : DateTime.MinValue;
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
}
