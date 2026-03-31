using RAM.Models;

namespace RAM.Services;

public sealed class ArtifactClassificationService
{
    private static readonly HashSet<string> FailureArtifactTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "build_failure_summary",
        "test_failure_summary"
    };

    private static readonly HashSet<string> RepairArtifactTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "repair_context",
        "repair_proposal"
    };

    private static readonly HashSet<string> PatchArtifactTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "patch_draft",
        "patch_apply_result"
    };

    private static readonly HashSet<string> VerificationArtifactTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "verification_plan",
        "verification_result",
        "repair_loop_closure"
    };

    private static readonly HashSet<string> AutoValidationArtifactTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto_validation_plan",
        "auto_validation_result"
    };

    private static readonly HashSet<string> SafetyArtifactTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "execution_safety_result",
        "build_scope_block"
    };

    private static readonly HashSet<string> TaskboardArtifactTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "taskboard_import",
        "taskboard_raw",
        "taskboard_parsed",
        "taskboard_plan",
        "taskboard_validation",
        "taskboard_run_projection",
        "taskboard_run_state",
        "taskboard_run_summary",
        "taskboard_normalized_run",
        "taskboard_index_export",
        "taskboard_corpus_export",
        "taskboard_execution_goal"
    };

    public bool IsFailureArtifact(ArtifactRecord? artifact)
    {
        return artifact is not null && IsFailureArtifactType(artifact.ArtifactType);
    }

    public bool IsRepairArtifact(ArtifactRecord? artifact)
    {
        return artifact is not null && IsRepairArtifactType(artifact.ArtifactType);
    }

    public bool IsPatchArtifact(ArtifactRecord? artifact)
    {
        return artifact is not null && IsPatchArtifactType(artifact.ArtifactType);
    }

    public bool IsVerificationArtifact(ArtifactRecord? artifact)
    {
        return artifact is not null && IsVerificationArtifactType(artifact.ArtifactType);
    }

    public bool IsRepairLoopArtifact(ArtifactRecord? artifact)
    {
        return artifact is not null && IsRepairLoopArtifactType(artifact.ArtifactType);
    }

    public bool IsSafetyArtifact(ArtifactRecord? artifact)
    {
        return artifact is not null && IsSafetyArtifactType(artifact.ArtifactType);
    }

    public bool IsAutoValidationArtifact(ArtifactRecord? artifact)
    {
        return artifact is not null && IsAutoValidationArtifactType(artifact.ArtifactType);
    }

    public bool IsTaskboardArtifact(ArtifactRecord? artifact)
    {
        return artifact is not null && IsTaskboardArtifactType(artifact.ArtifactType);
    }

    public bool IsFailureArtifactType(string artifactType)
    {
        return FailureArtifactTypes.Contains(artifactType ?? "");
    }

    public bool IsRepairArtifactType(string artifactType)
    {
        return RepairArtifactTypes.Contains(artifactType ?? "");
    }

    public bool IsPatchArtifactType(string artifactType)
    {
        return PatchArtifactTypes.Contains(artifactType ?? "");
    }

    public bool IsVerificationArtifactType(string artifactType)
    {
        return VerificationArtifactTypes.Contains(artifactType ?? "");
    }

    public bool IsSafetyArtifactType(string artifactType)
    {
        return SafetyArtifactTypes.Contains(artifactType ?? "");
    }

    public bool IsAutoValidationArtifactType(string artifactType)
    {
        return AutoValidationArtifactTypes.Contains(artifactType ?? "");
    }

    public bool IsTaskboardArtifactType(string artifactType)
    {
        return TaskboardArtifactTypes.Contains(artifactType ?? "");
    }

    public bool IsRepairLoopArtifactType(string artifactType)
    {
        return IsFailureArtifactType(artifactType)
            || IsRepairArtifactType(artifactType)
            || IsPatchArtifactType(artifactType)
            || IsVerificationArtifactType(artifactType)
            || IsAutoValidationArtifactType(artifactType)
            || IsSafetyArtifactType(artifactType);
    }

    public bool IsFileBackedArtifact(ArtifactRecord? artifact)
    {
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.RelativePath))
            return false;

        return !IsRepairLoopArtifactType(artifact.ArtifactType)
            && !artifact.RelativePath.StartsWith(".ram/", StringComparison.OrdinalIgnoreCase);
    }

    public bool HasRecordedFailureState(WorkspaceExecutionStateRecord state)
    {
        if (state is null || string.IsNullOrWhiteSpace(state.LastFailureUtc))
            return false;

        return IsRepairEligibleFailureKind(state.LastFailureOutcomeType);
    }

    public bool HasRecordedVerificationState(WorkspaceExecutionStateRecord state)
    {
        if (state is null || string.IsNullOrWhiteSpace(state.LastVerificationUtc))
            return false;

        return state.LastVerificationOutcomeType is "verified_fixed"
            or "still_failing"
            or "partially_improved"
            or "verification_failed"
            or "not_verifiable";
    }

    public bool IsSafetyOutcomeType(string outcomeType)
    {
        return string.Equals(outcomeType, "timed_out", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcomeType, "output_limit_exceeded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcomeType, "safety_blocked", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcomeType, "safety_blocked_scope", StringComparison.OrdinalIgnoreCase);
    }

    public string ClassifyArtifactFamily(ArtifactRecord? artifact)
    {
        if (artifact is null)
            return "none";

        if (IsFailureArtifact(artifact))
            return "failure";
        if (IsRepairArtifact(artifact))
            return "repair";
        if (IsPatchArtifact(artifact))
            return "patch";
        if (IsVerificationArtifact(artifact))
            return "verification";
        if (IsAutoValidationArtifact(artifact))
            return "auto_validation";
        if (IsSafetyArtifact(artifact))
            return "safety";
        if (IsTaskboardArtifact(artifact))
            return "taskboard";
        if (IsFileBackedArtifact(artifact))
            return "file";

        return "other";
    }

    public bool IsRepairEligibleFailureKind(string failureKind)
    {
        return string.Equals(failureKind, "build_failure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(failureKind, "test_failure", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsBuildOrTestTargetPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
    }
}
