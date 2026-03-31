using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class RepairEligibilityService
{
    private readonly ArtifactClassificationService _artifactClassificationService;

    public RepairEligibilityService(ArtifactClassificationService artifactClassificationService)
    {
        _artifactClassificationService = artifactClassificationService;
    }

    public RepairEligibilityResult EvaluatePlanRepair(string workspaceRoot, RamDbService ramDbService)
    {
        var result = BuildBaseResult(workspaceRoot, ramDbService);
        if (result.HasRecordedFailureState
            || result.HasPersistedFailureArtifact
            || result.HasPersistedRepairChain
            || result.HasRecordedVerificationState)
            return MarkEligible(result);

        return MarkIneligible(
            result,
            "no_failure_context",
            "plan_repair failed: no recorded build or test failure is available for this workspace."
            + Environment.NewLine
            + "Run dotnet build or dotnet test first, or select a failure-related artifact.");
    }

    public RepairEligibilityResult EvaluatePreviewPatchDraft(string workspaceRoot, RamDbService ramDbService)
    {
        var result = BuildBaseResult(workspaceRoot, ramDbService);
        if (result.HasPersistedPatchDraft || result.HasPersistedRepairArtifact || result.HasRecordedFailureState || result.HasPersistedFailureArtifact)
            return MarkEligible(result);

        return MarkIneligible(
            result,
            "no_repair_context",
            "preview_patch_draft failed: no repair proposal, patch draft, or failure-related repair context is available for this workspace."
            + Environment.NewLine
            + "Run dotnet build or dotnet test first, or create a repair plan from a recorded failure.");
    }

    public RepairEligibilityResult EvaluateApplyPatchDraft(string workspaceRoot, RamDbService ramDbService)
    {
        var result = BuildBaseResult(workspaceRoot, ramDbService);
        if (result.HasPersistedPatchDraft)
            return MarkEligible(result);

        return MarkIneligible(
            result,
            "no_patch_draft",
            "apply_patch_draft failed: no stored patch draft is available for this workspace."
            + Environment.NewLine
            + "Preview a repair patch from a recorded failure before applying it.");
    }

    public RepairEligibilityResult EvaluateVerifyPatchDraft(string workspaceRoot, RamDbService ramDbService)
    {
        var result = BuildBaseResult(workspaceRoot, ramDbService);
        if (result.HasPersistedPatchApply)
            return MarkEligible(result);

        return MarkIneligible(
            result,
            "no_patch_apply",
            "verify_patch_draft failed: no patch has been applied yet, so nothing can be verified."
            + Environment.NewLine
            + "Apply a stored patch draft from a real repair chain first.");
    }

    public RepairEligibilityResult EvaluateFailureNavigation(string workspaceRoot, RamDbService ramDbService)
    {
        var result = BuildBaseResult(workspaceRoot, ramDbService);
        if (result.HasRecordedFailureState
            || result.HasPersistedFailureArtifact
            || result.HasPersistedRepairArtifact
            || result.HasPersistedVerificationArtifact
            || result.HasRecordedVerificationState)
            return MarkEligible(result);

        return MarkIneligible(
            result,
            "no_failure_navigation_context",
            "open_failure_context failed: no recorded build or test failure is available for this workspace."
            + Environment.NewLine
            + "Run dotnet build or dotnet test first, or select a failure-related artifact.");
    }

    public bool HasValidRepairChain(PatchDraftRecord draft)
    {
        if (draft is null)
            return false;

        return _artifactClassificationService.IsRepairEligibleFailureKind(draft.FailureKind)
            || _artifactClassificationService.IsRepairLoopArtifactType(draft.SourceProposalArtifactType);
    }

    public bool HasValidRepairChain(PatchApplyResultRecord applyResult)
    {
        return applyResult is not null && HasValidRepairChain(applyResult.Draft);
    }

    public bool IsValidRepairProposal(RepairProposalRecord proposal)
    {
        if (proposal is null)
            return false;

        return _artifactClassificationService.IsRepairEligibleFailureKind(proposal.FailureKind)
            || _artifactClassificationService.IsRepairLoopArtifactType(proposal.SourceArtifactType);
    }

    public bool IsValidRepairProposalArtifact(ArtifactRecord artifact)
    {
        if (!_artifactClassificationService.IsRepairArtifact(artifact))
            return false;

        if (string.Equals(artifact.ArtifactType, "repair_context", StringComparison.OrdinalIgnoreCase))
            return true;

        var proposal = TryDeserialize<RepairProposalRecord>(artifact);
        return proposal is not null && IsValidRepairProposal(proposal);
    }

    public bool IsValidPatchDraftArtifact(ArtifactRecord artifact)
    {
        if (!string.Equals(artifact.ArtifactType, "patch_draft", StringComparison.OrdinalIgnoreCase))
            return false;

        var draft = TryDeserialize<PatchDraftRecord>(artifact);
        return draft is not null && HasValidRepairChain(draft);
    }

    public bool IsValidPatchApplyArtifact(ArtifactRecord artifact)
    {
        if (!string.Equals(artifact.ArtifactType, "patch_apply_result", StringComparison.OrdinalIgnoreCase))
            return false;

        var applyResult = TryDeserialize<PatchApplyResultRecord>(artifact);
        return applyResult is not null && HasValidRepairChain(applyResult);
    }

    private RepairEligibilityResult BuildBaseResult(string workspaceRoot, RamDbService ramDbService)
    {
        var recentArtifacts = ramDbService.LoadLatestArtifacts(workspaceRoot, 40);
        var state = ramDbService.LoadExecutionState(workspaceRoot);

        return new RepairEligibilityResult
        {
            ExecutionState = state,
            RecentArtifacts = recentArtifacts,
            HasRecordedFailureState = _artifactClassificationService.HasRecordedFailureState(state),
            HasRecordedVerificationState = _artifactClassificationService.HasRecordedVerificationState(state),
            HasPersistedFailureArtifact = recentArtifacts.Any(_artifactClassificationService.IsFailureArtifact),
            HasPersistedRepairArtifact = recentArtifacts.Any(IsValidRepairProposalArtifact),
            HasPersistedPatchDraft = recentArtifacts.Any(IsValidPatchDraftArtifact),
            HasPersistedPatchApply = recentArtifacts.Any(IsValidPatchApplyArtifact),
            HasPersistedVerificationArtifact = recentArtifacts.Any(_artifactClassificationService.IsVerificationArtifact)
        };
    }

    private static RepairEligibilityResult MarkEligible(RepairEligibilityResult result)
    {
        result.IsEligible = true;
        result.ReasonCode = "";
        result.Message = "";
        return result;
    }

    private static RepairEligibilityResult MarkIneligible(RepairEligibilityResult result, string reasonCode, string message)
    {
        result.IsEligible = false;
        result.ReasonCode = reasonCode;
        result.Message = message;
        return result;
    }

    private static T? TryDeserialize<T>(ArtifactRecord artifact)
    {
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.Content))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(artifact.Content);
        }
        catch
        {
            return default;
        }
    }
}
