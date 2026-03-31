using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class RamTrainingDataValidationService
{
    private const int LowConfidenceThreshold = 70;
    private const int HighQualityThreshold = 85;

    public RamTrainingValidationResult Validate(
        string workspaceRoot,
        IReadOnlyList<RamTrainingIntermediateRecord> intermediateRows,
        IReadOnlyList<RamTrainingExampleRecord> candidateExamples,
        IReadOnlyList<RamTrainingDatasetContractRecord> contracts)
    {
        var contractByFamily = contracts.ToDictionary(current => current.DatasetFamily, StringComparer.OrdinalIgnoreCase);
        var reviewQueues = new Dictionary<string, List<RamTrainingReviewRecord>>(StringComparer.OrdinalIgnoreCase);
        var survivingExamples = new List<RamTrainingExampleRecord>();

        foreach (var example in candidateExamples)
        {
            var reviewBucket = ResolveBaseReviewBucket(example, contractByFamily);
            if (reviewBucket is not null)
            {
                AddReview(reviewQueues, reviewBucket, example, reviewBucket);
                continue;
            }

            survivingExamples.Add(example);
        }

        var contradictionRejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in survivingExamples
                     .GroupBy(
                         current => $"{current.DatasetFamily}|{BuildContradictionKey(current.Input)}",
                         StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Select(current => NormalizeText(current.Output)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            foreach (var example in group)
            {
                contradictionRejected.Add(example.ExampleId);
                AddReview(reviewQueues, "contradiction_rows", example, "conflicting_canonical_output");
            }
        }

        survivingExamples = survivingExamples
            .Where(current => !contradictionRejected.Contains(current.ExampleId))
            .ToList();

        var dedupedExamples = new List<RamTrainingExampleRecord>();
        foreach (var group in survivingExamples
                     .GroupBy(current => $"{current.DatasetFamily}|{current.Fingerprint}", StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderByDescending(current => current.QualityScore)
                .ThenByDescending(current => current.Lineage.SourcePriority)
                .ThenByDescending(current => current.Lineage.ExtractionConfidence)
                .ThenBy(current => current.Lineage.CreatedUtc, StringComparer.OrdinalIgnoreCase)
                .ThenBy(current => current.ExampleId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            dedupedExamples.Add(ordered[0]);
            foreach (var duplicate in ordered.Skip(1))
                AddReview(reviewQueues, "duplicate_rows", duplicate, $"duplicate_of={ordered[0].ExampleId}");
        }

        dedupedExamples = dedupedExamples
            .OrderBy(current => current.DatasetFamily, StringComparer.OrdinalIgnoreCase)
            .ThenBy(current => current.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var example in dedupedExamples)
        {
            example.ValidationStatus = example.QualityScore >= HighQualityThreshold
                ? "accepted_high_quality"
                : "accepted";
        }

        var datasetFamilyStatuses = BuildDatasetFamilyStatuses(contracts, candidateExamples, dedupedExamples, reviewQueues);
        var report = new RamTrainingValidationReport
        {
            ReportId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            IntermediateCountsBySourceKind = intermediateRows
                .GroupBy(current => current.SourceKind, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            FinalCountsByDatasetFamily = dedupedExamples
                .GroupBy(current => current.DatasetFamily, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            ReviewCountsByBucket = reviewQueues
                .OrderBy(current => current.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(current => current.Key, current => current.Value.Count, StringComparer.OrdinalIgnoreCase),
            DuplicateRejectCount = CountBucket(reviewQueues, "duplicate_rows"),
            ContradictionRejectCount = CountBucket(reviewQueues, "contradiction_rows"),
            AmbiguityRejectCount = CountBucket(reviewQueues, "ambiguous_rows"),
            LowConfidenceRejectCount = CountBucket(reviewQueues, "low_confidence_rows"),
            IncompleteLineageRejectCount = CountBucket(reviewQueues, "incomplete_lineage_rows"),
            ShallowCoderRejectCount = CountBucket(reviewQueues, "shallow_coder_rows"),
            BlockedButUsefulReviewCount = CountBucket(reviewQueues, "blocked_but_useful_rows"),
            AcceptedHighQualityCount = dedupedExamples.Count(current => string.Equals(current.ValidationStatus, "accepted_high_quality", StringComparison.OrdinalIgnoreCase)),
            AcceptedTotalCount = dedupedExamples.Count,
            DatasetFamilyStatuses = datasetFamilyStatuses
        };

        foreach (var status in datasetFamilyStatuses.Where(current => current.AcceptedCount == 0))
        {
            var explanation = status.Notes.Count == 0
                ? "no_acceptance_reason_recorded"
                : string.Join(";", status.Notes.Select(NormalizeLabel));
            report.Notes.Add($"dataset_family_empty:{status.DatasetFamily}:{explanation}");
        }

        if (report.ContradictionRejectCount > 0)
            report.Notes.Add("contradiction_rows_quarantined");
        if (report.DuplicateRejectCount > 0)
            report.Notes.Add("duplicate_rows_deduped_deterministically");
        if (report.BlockedButUsefulReviewCount > 0)
            report.Notes.Add("blocked_but_useful_rows_preserved_for_review");
        if (report.ShallowCoderRejectCount > 0)
            report.Notes.Add("shallow_coder_rows_preserved_for_review");

        return new RamTrainingValidationResult
        {
            AcceptedExamples = dedupedExamples,
            ReviewQueues = reviewQueues
                .OrderBy(current => current.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    current => current.Key,
                    current => current.Value
                        .OrderBy(item => item.DatasetFamily, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(item => item.Fingerprint, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(item => item.ReviewId, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase),
            Report = report
        };
    }

    private static string? ResolveBaseReviewBucket(
        RamTrainingExampleRecord example,
        IReadOnlyDictionary<string, RamTrainingDatasetContractRecord> contractByFamily)
    {
        if (!contractByFamily.TryGetValue(example.DatasetFamily, out var contract))
            return "unknown_dataset_rows";

        if (contract.Track != example.Track)
            return "mixed_track_rows";

        if (HasMissingRequiredField(example, contract))
            return "empty_or_incomplete_rows";

        if (ContainsForbiddenPlaceholder(example.Output))
            return "placeholder_rows";

        if (example.ReasonCodes.Any(reason => reason.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)))
            return "ambiguous_rows";

        if (HasIncompleteLineage(example.Lineage))
            return "incomplete_lineage_rows";

        if (IsBlockedButUsefulExample(example))
            return "blocked_but_useful_rows";

        if (IsShallowCoderExample(example))
            return "shallow_coder_rows";

        if (example.QualityScore < LowConfidenceThreshold)
            return "low_confidence_rows";

        return null;
    }

    private static bool HasMissingRequiredField(RamTrainingExampleRecord example, RamTrainingDatasetContractRecord contract)
    {
        foreach (var field in contract.RequiredFields)
        {
            var normalizedField = NormalizeLabel(field);
            var value = normalizedField switch
            {
                "instruction" => example.Instruction,
                "input" => example.Input,
                "output" => example.Output,
                "fingerprint" => example.Fingerprint,
                _ => ""
            };

            if (string.IsNullOrWhiteSpace(value))
                return true;
        }

        return false;
    }

    private static bool ContainsForbiddenPlaceholder(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return true;

        return Regex.IsMatch(
            output,
            @"\b(TODO|placeholder|implement later|fill in later|NotImplementedException)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool HasIncompleteLineage(RamTrainingLineageRecord lineage)
    {
        return string.IsNullOrWhiteSpace(lineage.WorkspaceRoot)
            || string.IsNullOrWhiteSpace(lineage.SourceKind)
            || lineage.SourceArtifactId <= 0
            || string.IsNullOrWhiteSpace(lineage.SourceArtifactRelativePath)
            || string.IsNullOrWhiteSpace(lineage.CreatedUtc);
    }

    private static bool IsBlockedButUsefulExample(RamTrainingExampleRecord example)
    {
        if (example.Track != RamTrainingDatasetTrack.Coder)
            return false;

        if (!string.Equals(example.DatasetFamily, "coder_file_local_repair", StringComparison.OrdinalIgnoreCase))
            return false;

        if (example.ReasonCodes.Any(reason =>
                reason.Contains("repair_preview_only", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("generated_symbol_not_reconciled", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("verification_not_run", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return NormalizeText(example.Output).Contains("preview_only_not_closed", StringComparison.OrdinalIgnoreCase)
            || NormalizeText(example.Output).Contains("applied_but_unverified", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShallowCoderExample(RamTrainingExampleRecord example)
    {
        if (example.Track != RamTrainingDatasetTrack.Coder
            || string.Equals(example.DatasetFamily, "coder_scaffold_generation", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (example.ReasonCodes.Any(reason => reason.Contains("missing_target_file_artifact", StringComparison.OrdinalIgnoreCase)))
            return true;

        var hasPositiveIntegrationSignal = HasPositiveIntegrationEvidence(example);
        var hasStrongBehaviorTier = example.QualitySignals.Any(signal =>
            signal.Contains("behavior_depth=family_aligned_impl", StringComparison.OrdinalIgnoreCase)
            || signal.Contains("behavior_depth=integrated_behavior_impl", StringComparison.OrdinalIgnoreCase)
            || signal.Contains("behavior_depth=verified_integrated_behavior", StringComparison.OrdinalIgnoreCase)
            || signal.Contains("completion_strength=accepted_behavior_impl", StringComparison.OrdinalIgnoreCase)
            || signal.Contains("completion_strength=verified_integrated_behavior", StringComparison.OrdinalIgnoreCase));
        var hasWeakBehaviorTier = example.QualitySignals.Any(signal =>
            signal.Contains("behavior_depth=accepted_write_only", StringComparison.OrdinalIgnoreCase)
            || signal.Contains("behavior_depth=accepted_structural_impl", StringComparison.OrdinalIgnoreCase)
            || signal.Contains("behavior_depth=accepted_behavior_impl", StringComparison.OrdinalIgnoreCase)
            || signal.Contains("completion_strength=accepted_write_only", StringComparison.OrdinalIgnoreCase)
            || signal.Contains("completion_strength=accepted_structural_impl", StringComparison.OrdinalIgnoreCase));

        if (example.ReasonCodes.Any(reason =>
                reason.Contains("repository_without_consumer", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("dead_helper_without_consumer", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("dead_helper_without_caller_path", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("helper_without_test_linkage", StringComparison.OrdinalIgnoreCase)))
        {
            return !hasPositiveIntegrationSignal;
        }

        if (example.ReasonCodes.Any(reason => reason.Contains("accepted_write_without_closure", StringComparison.OrdinalIgnoreCase)))
            return !hasPositiveIntegrationSignal && !hasStrongBehaviorTier;

        return hasWeakBehaviorTier && !hasPositiveIntegrationSignal && !hasStrongBehaviorTier;
    }

    private static bool HasPositiveIntegrationEvidence(RamTrainingExampleRecord example)
    {
        return example.QualitySignals.Any(signal =>
            signal.Contains("verified_fixed", StringComparison.OrdinalIgnoreCase)
            || signal.Contains("repository_linkage_found", StringComparison.OrdinalIgnoreCase)
            || signal.Contains("registration_evidence_found", StringComparison.OrdinalIgnoreCase)
            || signal.Contains("binding_evidence_found", StringComparison.OrdinalIgnoreCase)
            || signal.Contains("test_linkage_found", StringComparison.OrdinalIgnoreCase)
            || signal.Contains("behavior_depth=integrated_behavior_impl", StringComparison.OrdinalIgnoreCase)
            || signal.Contains("behavior_depth=verified_integrated_behavior", StringComparison.OrdinalIgnoreCase));
    }

    private static List<RamTrainingDatasetFamilyStatusRecord> BuildDatasetFamilyStatuses(
        IReadOnlyList<RamTrainingDatasetContractRecord> contracts,
        IReadOnlyList<RamTrainingExampleRecord> candidateExamples,
        IReadOnlyList<RamTrainingExampleRecord> acceptedExamples,
        IReadOnlyDictionary<string, List<RamTrainingReviewRecord>> reviewQueues)
    {
        return contracts
            .OrderBy(current => current.Track)
            .ThenBy(current => current.DatasetFamily, StringComparer.OrdinalIgnoreCase)
            .Select(contract =>
            {
                var candidateCount = candidateExamples.Count(current => string.Equals(current.DatasetFamily, contract.DatasetFamily, StringComparison.OrdinalIgnoreCase));
                var acceptedCount = acceptedExamples.Count(current => string.Equals(current.DatasetFamily, contract.DatasetFamily, StringComparison.OrdinalIgnoreCase));
                var reviewRecords = reviewQueues
                    .SelectMany(current => current.Value)
                    .Where(current => string.Equals(current.DatasetFamily, contract.DatasetFamily, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var reviewCountsByBucket = reviewRecords
                    .GroupBy(current => current.Bucket, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
                var rejectCountsByReason = reviewRecords
                    .SelectMany(current => current.ReasonCodes)
                    .Where(current => !string.IsNullOrWhiteSpace(current))
                    .GroupBy(NormalizeLabel, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

                return new RamTrainingDatasetFamilyStatusRecord
                {
                    Track = contract.Track,
                    DatasetFamily = contract.DatasetFamily,
                    CandidateCount = candidateCount,
                    AcceptedCount = acceptedCount,
                    ReviewCount = reviewRecords.Count,
                    ReviewCountsByBucket = reviewCountsByBucket,
                    RejectCountsByReason = rejectCountsByReason,
                    Notes = BuildDatasetFamilyNotes(candidateCount, acceptedCount, reviewCountsByBucket, rejectCountsByReason)
                };
            })
            .ToList();
    }

    private static List<string> BuildDatasetFamilyNotes(
        int candidateCount,
        int acceptedCount,
        IReadOnlyDictionary<string, int> reviewCountsByBucket,
        IReadOnlyDictionary<string, int> rejectCountsByReason)
    {
        var notes = new List<string>();
        if (candidateCount == 0)
        {
            notes.Add("no qualifying source rows found");
            return notes;
        }

        if (acceptedCount == 0)
            notes.Add("no accepted examples reached final export");

        if (reviewCountsByBucket.TryGetValue("blocked_but_useful_rows", out var blockedButUseful) && blockedButUseful > 0)
            notes.Add("examples quarantined as blocked but useful");
        if (reviewCountsByBucket.TryGetValue("shallow_coder_rows", out var shallow) && shallow > 0)
            notes.Add("examples quarantined for shallow behavior depth");
        if (reviewCountsByBucket.TryGetValue("incomplete_lineage_rows", out var incompleteLineage) && incompleteLineage > 0)
            notes.Add("lineage join missing");
        if (reviewCountsByBucket.TryGetValue("ambiguous_rows", out var ambiguous) && ambiguous > 0)
            notes.Add("examples quarantined for ambiguity");
        if (reviewCountsByBucket.TryGetValue("contradiction_rows", out var contradiction) && contradiction > 0)
            notes.Add("examples quarantined for contradictory outputs");
        if (reviewCountsByBucket.TryGetValue("low_confidence_rows", out var lowConfidence) && lowConfidence > 0)
            notes.Add("examples quarantined for low confidence");

        if (notes.Count == 0 && rejectCountsByReason.Count > 0)
            notes.Add($"review pressure from {rejectCountsByReason.Keys.First()}");

        if (acceptedCount > 0)
            notes.Add("family has accepted training-ready examples");

        return notes;
    }

    private static void AddReview(
        IDictionary<string, List<RamTrainingReviewRecord>> reviewQueues,
        string bucket,
        RamTrainingExampleRecord example,
        params string[] additionalReasons)
    {
        example.ValidationStatus = $"review:{NormalizeLabel(bucket)}";
        if (!reviewQueues.TryGetValue(bucket, out var bucketRecords))
        {
            bucketRecords = [];
            reviewQueues[bucket] = bucketRecords;
        }

        bucketRecords.Add(new RamTrainingReviewRecord
        {
            ReviewId = Guid.NewGuid().ToString("N"),
            Bucket = bucket,
            Track = example.Track,
            DatasetFamily = example.DatasetFamily,
            SourceKind = example.Lineage.SourceKind,
            Input = example.Input,
            Output = example.Output,
            Fingerprint = example.Fingerprint,
            QualityScore = example.QualityScore,
            ValidationStatus = example.ValidationStatus,
            ReasonCodes =
            [
                .. example.ReasonCodes,
                .. additionalReasons.Where(value => !string.IsNullOrWhiteSpace(value)).Select(NormalizeLabel)
            ],
            QualitySignals = [.. example.QualitySignals],
            Lineage = example.Lineage
        });
    }

    private static int CountBucket(IReadOnlyDictionary<string, List<RamTrainingReviewRecord>> reviewQueues, string bucket)
    {
        return reviewQueues.TryGetValue(bucket, out var records) ? records.Count : 0;
    }

    private static string BuildContradictionKey(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";

        var marker = $"{Environment.NewLine}Context:{Environment.NewLine}";
        var index = input.IndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
            return NormalizeText(input[..index]);

        index = input.IndexOf("\nContext:\n", StringComparison.Ordinal);
        return index >= 0
            ? NormalizeText(input[..index])
            : NormalizeText(input);
    }

    private static string NormalizeText(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? ""
            : Regex.Replace(value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), @"\s+", " ").Trim();
    }

    private static string NormalizeLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return Regex.Replace(value.Trim(), @"\s+", "_").ToLowerInvariant();
    }
}
