using RAM.Models;

namespace RAM.Services;

public sealed class RamRetrievalQueryBuilderService
{
    public RamRetrievalQueryPacketRecord BuildRepairQueryPacket(
        string workspaceRoot,
        RepairPlanInput input,
        string planTitle,
        string stackFamily,
        string activeTargetPath,
        IReadOnlyList<ArtifactRecord> recentArtifacts)
    {
        if (input is null)
            throw new ArgumentNullException(nameof(input));

        var targetPaths = BuildDistinctPaths(
            input.TargetFilePath,
            input.TargetProjectPath,
            input.BaselineSolutionPath,
            input.ExplicitPath,
            activeTargetPath);
        var relatedArtifacts = recentArtifacts
            .Where(current =>
                string.Equals(current.ArtifactType, "taskboard_run_summary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(current.ArtifactType, "taskboard_normalized_run", StringComparison.OrdinalIgnoreCase)
                || string.Equals(current.ArtifactType, "repair_context", StringComparison.OrdinalIgnoreCase)
                || string.Equals(current.ArtifactType, "repair_proposal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(current.ArtifactType, "verification_result", StringComparison.OrdinalIgnoreCase)
                || string.Equals(current.ArtifactType, "patch_apply_result", StringComparison.OrdinalIgnoreCase))
            .Select(current => current.RelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Take(8)
            .ToList();

        return new RamRetrievalQueryPacketRecord
        {
            QueryId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            QueryKind = "repair_patch_context",
            ProblemSummary = BuildRepairProblemSummary(input),
            PlanTitle = planTitle,
            MaintenanceMode = "existing_project_maintenance",
            Language = "csharp",
            StackFamily = FirstNonEmpty(stackFamily, "dotnet_desktop"),
            ScopePaths = BuildDistinctPaths(targetPaths.Concat(input.BaselineAllowedRoots).ToArray()),
            TargetPaths = targetPaths,
            TrustFilters = ["current_truth", "historical_truth"],
            RecencyFilters = ["current", "recent", "historical"],
            RequiredTags = ["repair_context", "maintenance_context"],
            RelatedArtifactPaths = relatedArtifacts,
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    public RamRetrievalQueryPacketRecord BuildFeatureUpdateQueryPacket(
        string workspaceRoot,
        string problemSummary,
        string targetFilePath,
        string targetProjectPath,
        string targetSolutionPath,
        string planTitle,
        string stackFamily = "dotnet_desktop")
    {
        var targetPaths = BuildDistinctPaths(targetFilePath, targetProjectPath, targetSolutionPath);
        return new RamRetrievalQueryPacketRecord
        {
            QueryId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            QueryKind = "feature_update_context",
            ProblemSummary = FirstNonEmpty(problemSummary, "Prepare bounded C# feature-update context."),
            PlanTitle = planTitle,
            MaintenanceMode = "existing_project_maintenance",
            Language = "csharp",
            StackFamily = FirstNonEmpty(stackFamily, "dotnet_desktop"),
            ScopePaths = targetPaths,
            TargetPaths = targetPaths,
            TrustFilters = ["current_truth", "historical_truth"],
            RecencyFilters = ["current", "recent", "historical"],
            RequiredTags = ["feature_update", "maintenance_context"],
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private static string BuildRepairProblemSummary(RepairPlanInput input)
    {
        var parts = new List<string>
        {
            $"failure_kind={FirstNonEmpty(input.FailureKind, "(none)")}",
            $"failure_code={FirstNonEmpty(input.FailureCode, "(none)")}",
            $"target_file={FirstNonEmpty(input.TargetFilePath, "(none)")}",
            $"target_project={FirstNonEmpty(input.TargetProjectPath, "(none)")}",
            $"summary={FirstNonEmpty(input.FailureMessage, input.Message, "(none)")}"
        };

        if (!string.IsNullOrWhiteSpace(input.AmbiguitySummary))
            parts.Add($"ambiguity={input.AmbiguitySummary}");

        if (!string.IsNullOrWhiteSpace(input.BaselineSolutionPath))
            parts.Add($"baseline_solution={input.BaselineSolutionPath}");

        if (input.BaselineAllowedRoots.Count > 0)
            parts.Add($"allowed_roots={string.Join(",", input.BaselineAllowedRoots)}");

        if (input.BaselineExcludedRoots.Count > 0)
            parts.Add($"excluded_roots={string.Join(",", input.BaselineExcludedRoots)}");

        return string.Join(" | ", parts);
    }

    private static List<string> BuildDistinctPaths(params string?[] values)
    {
        return values
            .Select(NormalizePath)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizePath(string? value)
    {
        return (value ?? "").Replace('\\', '/').Trim();
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
