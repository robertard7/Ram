using System.Security.Cryptography;
using System.Text;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardRuntimeStateFingerprintService
{
    public const string CurrentRuntimeStateVersion = "phase27_34_runtime_v2";
    public const string CurrentResolverContractVersion = "taskboard_live_resolution.v2";

    public TaskboardRuntimeStateAssessment Evaluate(
        TaskboardPlanRunStateRecord? existing,
        TaskboardImportRecord activeImport,
        TaskboardDocument activeDocument)
    {
        var currentFingerprint = BuildFingerprint(activeImport, activeDocument);
        if (existing is null)
        {
            return new TaskboardRuntimeStateAssessment
            {
                HasSnapshot = false,
                IsCompatible = false,
                StatusCode = "fresh_runtime_snapshot",
                Summary = "No cached runtime snapshot exists. A fresh runtime snapshot is required.",
                CurrentVersion = CurrentRuntimeStateVersion,
                CurrentFingerprint = currentFingerprint
            };
        }

        var cachedVersion = Normalize(existing.RuntimeStateVersion);
        var cachedFingerprint = Normalize(existing.RuntimeStateFingerprint);
        if (string.IsNullOrWhiteSpace(cachedVersion))
        {
            return BuildStaleAssessment(
                currentFingerprint,
                cachedVersion,
                cachedFingerprint,
                "runtime_state_version_missing",
                "Cached runtime snapshot predates the current runtime-state contract.");
        }

        if (!string.Equals(cachedVersion, CurrentRuntimeStateVersion, StringComparison.OrdinalIgnoreCase))
        {
            return BuildStaleAssessment(
                currentFingerprint,
                cachedVersion,
                cachedFingerprint,
                "runtime_state_version_mismatch",
                $"Cached runtime snapshot version `{cachedVersion}` does not match current version `{CurrentRuntimeStateVersion}`.");
        }

        if (string.IsNullOrWhiteSpace(cachedFingerprint))
        {
            return BuildStaleAssessment(
                currentFingerprint,
                cachedVersion,
                cachedFingerprint,
                "runtime_state_fingerprint_missing",
                "Cached runtime snapshot does not carry a taskboard fingerprint.");
        }

        if (!string.Equals(cachedFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return BuildStaleAssessment(
                currentFingerprint,
                cachedVersion,
                cachedFingerprint,
                "runtime_state_fingerprint_mismatch",
                "Cached runtime snapshot does not match the current active taskboard content.");
        }

        if (existing.Batches.Count == 0)
        {
            return BuildStaleAssessment(
                currentFingerprint,
                cachedVersion,
                cachedFingerprint,
                "runtime_state_missing_batches",
                "Cached runtime snapshot has no batch state and cannot be reused.");
        }

        return new TaskboardRuntimeStateAssessment
        {
            HasSnapshot = true,
            IsCompatible = true,
            StatusCode = "fresh_runtime_snapshot",
            Summary = "Cached runtime snapshot matches the current taskboard contract.",
            CurrentVersion = CurrentRuntimeStateVersion,
            CurrentFingerprint = currentFingerprint,
            CachedVersion = existing.RuntimeStateVersion,
            CachedFingerprint = existing.RuntimeStateFingerprint
        };
    }

    public string BuildFingerprint(TaskboardImportRecord activeImport, TaskboardDocument activeDocument)
    {
        var builder = new StringBuilder();
        builder.Append(CurrentRuntimeStateVersion).Append('|');
        builder.Append(CurrentResolverContractVersion).Append('|');
        builder.Append(BuilderWorkItemDecompositionService.ResolverContractVersion).Append('|');
        builder.Append(BuildProfileResolutionService.ResolverContractVersion).Append('|');
        builder.Append(CommandCanonicalizationService.ResolverContractVersion).Append('|');
        builder.Append(DeterministicPhraseFamilyFallbackService.ResolverContractVersion).Append('|');
        builder.Append(TaskboardBuilderOperationResolutionService.ResolverContractVersion).Append('|');
        builder.Append(TaskboardWorkItemStateRefreshService.ResolverContractVersion).Append('|');
        builder.Append(TaskboardExecutionLaneResolutionService.ResolverContractVersion).Append('|');
        builder.Append(FileIdentityService.ResolverContractVersion).Append('|');
        builder.Append(Normalize(activeImport.ImportId)).Append('|');
        builder.Append(Normalize(activeImport.ContentHash)).Append('|');
        builder.Append(Normalize(activeDocument.ContentHash)).Append('|');
        builder.Append(Normalize(activeDocument.ParserVersion)).Append('|');
        builder.Append(Normalize(activeDocument.ModelBuilderVersion)).Append('|');
        builder.Append(Normalize(activeDocument.Title)).Append('|');
        builder.Append(activeDocument.Batches.Count).Append('|');

        foreach (var batch in activeDocument.Batches.OrderBy(current => current.BatchNumber))
        {
            builder.Append(Normalize(batch.BatchId)).Append('|');
            builder.Append(batch.BatchNumber).Append('|');
            builder.Append(Normalize(batch.Title)).Append('|');
            builder.Append(batch.Steps.Count).Append('|');

            foreach (var step in batch.Steps.OrderBy(current => current.Ordinal))
            {
                builder.Append(Normalize(step.StepId)).Append('|');
                builder.Append(step.Ordinal).Append('|');
                builder.Append(Normalize(step.Title)).Append('|');
            }
        }

        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static TaskboardRuntimeStateAssessment BuildStaleAssessment(
        string currentFingerprint,
        string cachedVersion,
        string cachedFingerprint,
        string invalidationReason,
        string summary)
    {
        return new TaskboardRuntimeStateAssessment
        {
            HasSnapshot = true,
            IsCompatible = false,
            StatusCode = "stale_cached_snapshot",
            Summary = summary,
            InvalidationReason = invalidationReason,
            CurrentVersion = CurrentRuntimeStateVersion,
            CurrentFingerprint = currentFingerprint,
            CachedVersion = cachedVersion,
            CachedFingerprint = cachedFingerprint
        };
    }

    private static string Normalize(string? value)
    {
        return (value ?? "").Trim();
    }
}
