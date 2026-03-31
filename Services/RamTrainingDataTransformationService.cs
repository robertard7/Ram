using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class RamTrainingDataTransformationService
{
    public List<RamTrainingExampleRecord> Transform(
        IReadOnlyList<RamTrainingIntermediateRecord> intermediateRows,
        IReadOnlyList<RamTrainingDatasetContractRecord> contracts)
    {
        var contractsByFamily = contracts.ToDictionary(current => current.DatasetFamily, StringComparer.OrdinalIgnoreCase);
        var results = new List<RamTrainingExampleRecord>();

        foreach (var row in intermediateRows)
        {
            if (!contractsByFamily.TryGetValue(row.DatasetFamilyHint, out var contract))
                continue;

            var input = BuildInput(row);
            var output = NormalizeText(row.RawResult);
            var qualityScore = ComputeQualityScore(row);
            var fingerprint = ComputeFingerprint(contract.DatasetFamily, input, output);

            results.Add(new RamTrainingExampleRecord
            {
                ExampleId = Guid.NewGuid().ToString("N"),
                Track = contract.Track,
                DatasetFamily = contract.DatasetFamily,
                Instruction = contract.InstructionTemplate,
                Input = input,
                Output = output,
                Fingerprint = fingerprint,
                QualityScore = qualityScore,
                ValidationStatus = "candidate",
                ReasonCodes = [.. row.ReasonCodes],
                QualitySignals =
                [
                    .. row.QualitySignals,
                    $"source_kind={row.SourceKind}",
                    $"canonical_label={row.CanonicalLabel}"
                ],
                Lineage = row.Lineage
            });
        }

        return results;
    }

    private static string BuildInput(RamTrainingIntermediateRecord row)
    {
        return string.IsNullOrWhiteSpace(row.RawContext)
            ? NormalizeText(row.RawInput)
            : string.Join(Environment.NewLine, NormalizeText(row.RawInput), "Context:", NormalizeText(row.RawContext));
    }

    private static int ComputeQualityScore(RamTrainingIntermediateRecord row)
    {
        var score = (int)Math.Round((row.Lineage.SourcePriority * 0.6) + (row.Lineage.ExtractionConfidence * 0.4));
        if (row.TrackHint == RamTrainingDatasetTrack.Intake)
        {
            if (row.QualitySignals.Any(signal => signal.Contains("authoritative", StringComparison.OrdinalIgnoreCase)))
                score += 6;
            if (row.SourceKind is "taskboard_run_summary" or "taskboard_skip_record")
                score += 5;
        }
        else if (row.TrackHint == RamTrainingDatasetTrack.Coder)
        {
            if (row.QualitySignals.Any(signal => signal.Contains("verified_fixed", StringComparison.OrdinalIgnoreCase)))
                score += 12;
            if (row.QualitySignals.Any(signal => signal.Contains("binding_evidence_found", StringComparison.OrdinalIgnoreCase)
                                                 || signal.Contains("registration_evidence_found", StringComparison.OrdinalIgnoreCase)
                                                 || signal.Contains("repository_linkage_found", StringComparison.OrdinalIgnoreCase)
                                                 || signal.Contains("test_linkage_found", StringComparison.OrdinalIgnoreCase)))
            {
                score += 6;
            }
            if (row.QualitySignals.Any(signal => signal.Contains("behavior_depth=verified_integrated_behavior", StringComparison.OrdinalIgnoreCase)))
                score += 14;
            if (row.QualitySignals.Any(signal => signal.Contains("behavior_depth=integrated_behavior_impl", StringComparison.OrdinalIgnoreCase)))
                score += 10;
            if (row.QualitySignals.Any(signal => signal.Contains("behavior_depth=family_aligned_impl", StringComparison.OrdinalIgnoreCase)))
                score += 8;
            if (row.QualitySignals.Any(signal => signal.Contains("behavior_depth=accepted_behavior_impl", StringComparison.OrdinalIgnoreCase)))
                score += 6;
            if (row.QualitySignals.Any(signal => signal.Contains("completion_strength=family_aligned_impl", StringComparison.OrdinalIgnoreCase)
                                                 || signal.Contains("output_quality=family_aligned_impl", StringComparison.OrdinalIgnoreCase)))
            {
                score += 4;
            }
        }

        if (row.ReasonCodes.Any(reason => reason.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)))
            score -= 22;
        if (row.ReasonCodes.Any(reason => reason.Contains("accepted_write_without_closure", StringComparison.OrdinalIgnoreCase)))
            score -= 6;
        if (row.ReasonCodes.Count > 0)
            score -= Math.Min(12, row.ReasonCodes.Count * 3);

        return Math.Clamp(score, 0, 100);
    }

    private static string ComputeFingerprint(string datasetFamily, string input, string output)
    {
        var payload = $"{NormalizeLabel(datasetFamily)}|{NormalizeText(input)}|{NormalizeText(output)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @"[ \t]+", " ");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        return normalized.Trim();
    }

    private static string NormalizeLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return Regex.Replace(value.Trim(), @"\s+", "_").ToLowerInvariant();
    }
}
