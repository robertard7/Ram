using RAM.Models;

namespace RAM.Tools;

public sealed class VerifyPatchDraftTool
{
    public string Format(
        VerificationPlanRecord plan,
        VerificationOutcomeRecord outcome,
        ArtifactRecord planArtifact,
        ArtifactRecord resultArtifact,
        ArtifactRecord? closureArtifact)
    {
        var lines = new List<string>
        {
            "Patch verification:",
            $"Modification intent: {DisplayValue(plan.ModificationIntent)}",
            $"Target surface type: {DisplayValue(plan.TargetSurfaceType)}",
            $"Verification tool: {DisplayValue(plan.VerificationTool)}",
            $"Target: {DisplayValue(plan.TargetPath)}",
            $"Failure kind: {DisplayValue(plan.FailureKind)}",
            $"Warning policy: {DisplayValue(plan.WarningPolicyMode)}",
            $"Confidence: {DisplayValue(plan.Confidence)}",
            $"Rationale: {DisplayValue(plan.Rationale)}",
            $"Outcome: {DisplayValue(outcome.OutcomeClassification)}",
            $"Before: {DisplayValue(outcome.BeforeSummary)}",
            $"After: {DisplayValue(outcome.AfterSummary)}",
            $"Explanation: {DisplayValue(outcome.Explanation)}"
        };

        if (!string.IsNullOrWhiteSpace(plan.SafetyPolicySummary))
            lines.Add($"Execution safety: {plan.SafetyPolicySummary}");

        if (plan.ValidationRequirements.Count > 0)
            lines.Add($"Validation requirements: {string.Join(", ", plan.ValidationRequirements)}");

        if (plan.RerunRequirements.Count > 0)
            lines.Add($"Rerun requirements: {string.Join(", ", plan.RerunRequirements)}");

        if (plan.TargetFiles.Count > 0)
            lines.Add($"Target files: {string.Join(", ", plan.TargetFiles)}");

        if (outcome.BeforeFailureCount.HasValue || outcome.AfterFailureCount.HasValue)
        {
            lines.Add(
                $"Failure counts: before={DisplayCount(outcome.BeforeFailureCount)} after={DisplayCount(outcome.AfterFailureCount)} delta={DisplayCount(outcome.FailureCountDelta)}");
        }

        if (outcome.BeforeWarningCount.HasValue || outcome.AfterWarningCount.HasValue)
        {
            lines.Add(
                $"Warning counts: before={DisplayCount(outcome.BeforeWarningCount)} after={DisplayCount(outcome.AfterWarningCount)} delta={DisplayCount(outcome.WarningCountDelta)}");
        }

        if (outcome.WarningCodes.Count > 0)
            lines.Add($"Warning codes: {string.Join(", ", outcome.WarningCodes)}");

        if (outcome.TopRemainingFailures.Count > 0)
        {
            lines.Add("Top remaining failures:");
            foreach (var remaining in outcome.TopRemainingFailures.Take(5))
                lines.Add($"- {remaining}");
        }

        lines.Add($"Artifact synced: {planArtifact.RelativePath}");
        lines.Add($"Artifact synced: {resultArtifact.RelativePath}");

        if (closureArtifact is not null)
            lines.Add($"Artifact synced: {closureArtifact.RelativePath}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string DisplayCount(int? value)
    {
        return value.HasValue ? value.Value.ToString() : "(unknown)";
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
