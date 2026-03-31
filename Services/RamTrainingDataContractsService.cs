using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class RamTrainingDataContractsService
{
    public List<RamTrainingSourceTruthRuleRecord> BuildSourceTruthRules()
    {
        return
        [
            BuildTruthRule("intake_intent_classification", "taskboard_import", ["taskboard_execution_lane", "taskboard_normalized_run"], "Intent examples prefer import classification artifacts and fall back to normalized runtime routing only when taskboard import evidence is unavailable."),
            BuildTruthRule("intake_phrase_resolution", "taskboard_phrase_family_resolution", ["taskboard_decomposition", "taskboard_normalized_run"], "Phrase-family training prefers explicit resolution artifacts over downstream summaries."),
            BuildTruthRule("intake_work_family_resolution", "taskboard_execution_lane", ["taskboard_execution_goal", "taskboard_decomposition"], "Work-family training prefers resolved lane records because they contain the final family actually used by runtime."),
            BuildTruthRule("intake_operation_selection", "taskboard_execution_lane", ["taskboard_execution_goal", "taskboard_decomposition"], "Operation selection prefers resolved lane records and uses decomposition only as lower-priority fallback."),
            BuildTruthRule("intake_lane_chain_selection", "taskboard_execution_lane", ["taskboard_execution_goal"], "Lane and chain selection examples prefer the final lane resolution rather than looser prompt summaries."),
            BuildTruthRule("intake_skip_reasoning", "taskboard_skip_record", ["taskboard_normalized_run"], "Skip reasoning prefers explicit skip decisions over terminal summaries."),
            BuildTruthRule("intake_summary_explanations", "taskboard_run_summary", ["taskboard_normalized_run"], "Summary explanation training prefers terminal run summaries over intermediate logs."),
            BuildTruthRule("coder_scaffold_generation", "behavior_depth_evidence", ["file_artifact"], "Scaffold generation examples prefer accepted generated files that also carry behavior-depth lineage."),
            BuildTruthRule("coder_runtime_wiring", "behavior_depth_evidence", ["file_artifact", "taskboard_execution_lane"], "Runtime wiring examples prefer behavior-depth evidence joined to the generated file artifact and resolved runtime family."),
            BuildTruthRule("coder_file_local_repair", "repair_proposal", ["patch_draft", "verification_result"], "Repair examples prefer repair proposal plus patch draft plus verification outcome over chat text or summary-only artifacts."),
            BuildTruthRule("coder_behavior_depth_upgrade", "behavior_depth_evidence", ["taskboard_run_summary"], "Behavior-depth upgrade candidates prefer behavior-depth evidence and are quarantined unless a stronger later output can be proven."),
            BuildTruthRule("coder_validation_aware_generation", "behavior_depth_evidence", ["verification_result", "file_artifact"], "Validation-aware coding examples prefer accepted files backed by behavior-depth evidence and, when present, verification evidence.")
        ];
    }

    public List<RamTrainingDatasetContractRecord> BuildDatasetContracts()
    {
        return
        [
            BuildDatasetContract(RamTrainingDatasetTrack.Intake, "intake_intent_classification", "intake_intent_classification.jsonl", "Classify the request into the correct intake intent.", ["instruction", "input", "output", "fingerprint"], ["taskboard_import", "taskboard_execution_lane"], "single canonical label"),
            BuildDatasetContract(RamTrainingDatasetTrack.Intake, "intake_phrase_resolution", "intake_phrase_resolution.jsonl", "Resolve the phrase to the correct phrase family.", ["instruction", "input", "output", "fingerprint"], ["taskboard_phrase_family_resolution", "taskboard_decomposition"], "single canonical phrase family"),
            BuildDatasetContract(RamTrainingDatasetTrack.Intake, "intake_work_family_resolution", "intake_work_family_resolution.jsonl", "Select the correct work family for the request.", ["instruction", "input", "output", "fingerprint"], ["taskboard_execution_lane", "taskboard_execution_goal"], "single canonical work family"),
            BuildDatasetContract(RamTrainingDatasetTrack.Intake, "intake_operation_selection", "intake_operation_selection.jsonl", "Select the correct bounded operation kind.", ["instruction", "input", "output", "fingerprint"], ["taskboard_execution_lane", "taskboard_execution_goal"], "single canonical operation kind"),
            BuildDatasetContract(RamTrainingDatasetTrack.Intake, "intake_lane_chain_selection", "intake_lane_chain_selection.jsonl", "Select the correct execution lane or chain.", ["instruction", "input", "output", "fingerprint"], ["taskboard_execution_lane"], "single canonical lane target"),
            BuildDatasetContract(RamTrainingDatasetTrack.Intake, "intake_skip_reasoning", "intake_skip_reasoning.jsonl", "Explain why the work item should be skipped or treated as already satisfied.", ["instruction", "input", "output", "fingerprint"], ["taskboard_skip_record"], "single canonical skip reason"),
            BuildDatasetContract(RamTrainingDatasetTrack.Intake, "intake_summary_explanations", "intake_summary_explanations.jsonl", "Explain the current terminal state truthfully and concisely.", ["instruction", "input", "output", "fingerprint"], ["taskboard_run_summary", "taskboard_normalized_run"], "single canonical explanation"),
            BuildDatasetContract(RamTrainingDatasetTrack.Coder, "coder_scaffold_generation", "coder_scaffold_generation.jsonl", "Generate the bounded scaffold file that matches the contract and local context.", ["instruction", "input", "output", "fingerprint"], ["behavior_depth_evidence", "file_artifact"], "single canonical file content"),
            BuildDatasetContract(RamTrainingDatasetTrack.Coder, "coder_runtime_wiring", "coder_runtime_wiring.jsonl", "Write the bounded runtime wiring file so it matches the requested family and local context.", ["instruction", "input", "output", "fingerprint"], ["behavior_depth_evidence", "file_artifact"], "single canonical file content"),
            BuildDatasetContract(RamTrainingDatasetTrack.Coder, "coder_file_local_repair", "coder_file_local_repair.jsonl", "Repair the target file using the smallest deterministic local change that resolves the recorded failure.", ["instruction", "input", "output", "fingerprint"], ["repair_proposal", "patch_draft", "verification_result"], "single canonical local patch"),
            BuildDatasetContract(RamTrainingDatasetTrack.Coder, "coder_behavior_depth_upgrade", "coder_behavior_depth_upgrade.jsonl", "Upgrade the shallow implementation into stronger behavior within the bounded target file.", ["instruction", "input", "output", "fingerprint"], ["behavior_depth_evidence"], "single canonical upgraded file content"),
            BuildDatasetContract(RamTrainingDatasetTrack.Coder, "coder_validation_aware_generation", "coder_validation_aware_generation.jsonl", "Generate code that survives the bounded validation contract and local runtime constraints.", ["instruction", "input", "output", "fingerprint"], ["behavior_depth_evidence", "verification_result"], "single canonical validated file content")
        ];
    }

    public RamTrainingSourceInventoryRecord BuildSourceInventory(
        string workspaceRoot,
        IReadOnlyList<string> sourceWorkspaceRoots,
        IReadOnlyList<ArtifactRecord> artifacts,
        IReadOnlyList<RamFileTouchRecord> fileTouches,
        IReadOnlyList<TaskboardSkipDecisionRecord> skipRecords,
        IReadOnlyList<MemorySummaryRecord> memorySummaries)
    {
        var artifactCountsByType = artifacts
            .GroupBy(current => FirstNonEmpty(current.ArtifactType, "(none)"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var artifactCountsByPrefix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["taskboards"] = artifacts.Count(current => current.RelativePath.StartsWith(".ram/taskboards/", StringComparison.OrdinalIgnoreCase)),
            ["tool_chains"] = artifacts.Count(current => current.RelativePath.StartsWith(".ram/tool-chains/", StringComparison.OrdinalIgnoreCase)),
            ["repair_proposals"] = artifacts.Count(current => current.RelativePath.Contains("/repair-proposals/", StringComparison.OrdinalIgnoreCase)),
            ["patch_drafts"] = artifacts.Count(current => current.RelativePath.Contains("/patch-drafts/", StringComparison.OrdinalIgnoreCase)),
            ["csharp_patch_contracts"] = artifacts.Count(current => current.RelativePath.Contains("/csharp-patch/contracts/", StringComparison.OrdinalIgnoreCase)),
            ["auto_validation"] = artifacts.Count(current => current.RelativePath.Contains("/auto-validation/", StringComparison.OrdinalIgnoreCase)),
            ["retrieval_context"] = artifacts.Count(current => current.RelativePath.Contains("/retrieval/context/", StringComparison.OrdinalIgnoreCase)),
            ["retrieval_results"] = artifacts.Count(current => current.RelativePath.Contains("/retrieval/results/", StringComparison.OrdinalIgnoreCase)),
            ["behavior_depth"] = artifacts.Count(current => string.Equals(current.ArtifactType, "behavior_depth_evidence", StringComparison.OrdinalIgnoreCase)),
            ["verification_results"] = artifacts.Count(current => string.Equals(current.ArtifactType, "verification_result", StringComparison.OrdinalIgnoreCase)),
            ["repair_closure"] = artifacts.Count(current => IsRepairClosureArtifact(current.Content, current.ArtifactType))
        };

        return new RamTrainingSourceInventoryRecord
        {
            InventoryId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            SourceWorkspaceRoots = sourceWorkspaceRoots
                .OrderBy(current => current, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SourceSystems = new List<RamTrainingSourceSystemRecord>
            {
                BuildSourceSystem("workspace_history_roots", "workspace_root_set", workspaceRoot, sourceWorkspaceRoots.Count > 0, sourceWorkspaceRoots.Count, "aggregated RAM history roots", "The pipeline aggregates RAM history across discovered workspace roots instead of reading only the top-level workspace database."),
                BuildSourceSystem("ram_db_intents", "db_table", ".ram/ram.db:intents", true, 1, "intake classification context", "The workspace intent row provides top-level request framing when it exists."),
                BuildSourceSystem("ram_db_memory_summaries", "db_table", ".ram/ram.db:memory_summaries", memorySummaries.Count > 0, memorySummaries.Count, "summary and context history", "Memory summaries are lower-priority support context and not primary coder truth."),
                BuildSourceSystem("ram_db_artifacts", "db_table", ".ram/ram.db:artifacts", artifacts.Count > 0, artifacts.Count, "artifact-backed source of truth", "Artifacts are the primary training source because they preserve normalized runtime payloads."),
                BuildSourceSystem("ram_db_file_touch_records", "db_table", ".ram/ram.db:file_touch_records", fileTouches.Count > 0, fileTouches.Count, "mutation lineage", "File-touch records provide mutation lineage and same-run ordering."),
                BuildSourceSystem("ram_db_taskboard_skip_records", "db_table", ".ram/ram.db:taskboard_skip_records", skipRecords.Count > 0, skipRecords.Count, "skip reasoning", "Skip records provide authoritative satisfaction and reuse reasoning."),
                BuildSourceSystem("artifact_family_taskboards", "artifact_family", ".ram/taskboards/", artifactCountsByPrefix["taskboards"] > 0, artifactCountsByPrefix["taskboards"], "taskboard intake, routing, summaries", "Taskboard artifacts are the preferred intake-model source family."),
                BuildSourceSystem("artifact_family_tool_chains", "artifact_family", ".ram/tool-chains/", artifactCountsByPrefix["tool_chains"] > 0, artifactCountsByPrefix["tool_chains"], "chain execution traces", "Tool-chain artifacts provide runtime continuity and stop-state truth."),
                BuildSourceSystem("artifact_family_repair_proposals", "artifact_family", ".ram/repair-proposals/", artifactCountsByPrefix["repair_proposals"] > 0, artifactCountsByPrefix["repair_proposals"], "repair planning", "Repair proposals anchor coder repair examples."),
                BuildSourceSystem("artifact_family_patch_drafts", "artifact_family", ".ram/patch-drafts/", artifactCountsByPrefix["patch_drafts"] > 0, artifactCountsByPrefix["patch_drafts"], "local patch drafts", "Patch drafts provide deterministic local repair outputs when mutation stays bounded."),
                BuildSourceSystem("artifact_family_csharp_patch_contracts", "artifact_family", ".ram/csharp-patch/contracts/", artifactCountsByPrefix["csharp_patch_contracts"] > 0, artifactCountsByPrefix["csharp_patch_contracts"], "patch scope contracts", "Patch contracts constrain repair scope and improve coder example quality."),
                BuildSourceSystem("artifact_family_auto_validation", "artifact_family", ".ram/auto-validation/", artifactCountsByPrefix["auto_validation"] > 0, artifactCountsByPrefix["auto_validation"], "validation evidence", "Auto-validation artifacts can strengthen validation-aware coder examples."),
                BuildSourceSystem("artifact_family_behavior_depth", "artifact_family", "artifact_type:behavior_depth_evidence", artifactCountsByPrefix["behavior_depth"] > 0, artifactCountsByPrefix["behavior_depth"], "behavior-depth evidence for coder acceptance and upgrade judgment", "Behavior-depth artifacts are first-class coder supervision because they record caller paths, integration evidence, shallow flags, and bounded follow-up recommendations."),
                BuildSourceSystem("artifact_family_verification_results", "artifact_family", "artifact_type:verification_result", artifactCountsByPrefix["verification_results"] > 0, artifactCountsByPrefix["verification_results"], "verification and closure evidence", "Verification-result artifacts distinguish preview-only repair from applied or rebuild-first closure."),
                BuildSourceSystem("artifact_family_repair_closure", "artifact_family", "verification_result linked to repair/patch lineage", artifactCountsByPrefix["repair_closure"] > 0, artifactCountsByPrefix["repair_closure"], "repair-loop closure evidence", "Repair-closure evidence teaches the coder model what actually fixed a failure versus what merely reached preview or review."),
                BuildSourceSystem("artifact_family_retrieval_context", "artifact_family", ".ram/retrieval/context/", artifactCountsByPrefix["retrieval_context"] > 0, artifactCountsByPrefix["retrieval_context"], "retrieval context packets", "Retrieval context is useful lineage but lower-priority than terminal runtime truth."),
                BuildSourceSystem("artifact_family_retrieval_results", "artifact_family", ".ram/retrieval/results/", artifactCountsByPrefix["retrieval_results"] > 0, artifactCountsByPrefix["retrieval_results"], "retrieval result packets", "Retrieval result packets provide supporting context only.")
            }
            .Concat(artifactCountsByType
                .OrderBy(current => current.Key, StringComparer.OrdinalIgnoreCase)
                .Select(current => BuildSourceSystem(
                    $"artifact_type:{current.Key}",
                    "artifact_type",
                    current.Key,
                    current.Value > 0,
                    current.Value,
                    "artifact subtype",
                    $"Artifact subtype `{current.Key}` is present in the workspace training source inventory.")))
            .ToList(),
            SourceTruthRules = BuildSourceTruthRules(),
            DatasetContracts = BuildDatasetContracts()
        };
    }

    private static RamTrainingSourceTruthRuleRecord BuildTruthRule(
        string datasetFamily,
        string preferredSourceKind,
        List<string> fallbackSourceKinds,
        string summary)
    {
        return new RamTrainingSourceTruthRuleRecord
        {
            DatasetFamily = datasetFamily,
            PreferredSourceKind = preferredSourceKind,
            FallbackSourceKinds = fallbackSourceKinds,
            RuleSummary = summary
        };
    }

    private static RamTrainingDatasetContractRecord BuildDatasetContract(
        RamTrainingDatasetTrack track,
        string datasetFamily,
        string fileName,
        string instructionTemplate,
        List<string> requiredFields,
        List<string> preferredSourceKinds,
        string canonicalAnswerShape)
    {
        return new RamTrainingDatasetContractRecord
        {
            Track = track,
            DatasetFamily = datasetFamily,
            FileName = fileName,
            InstructionTemplate = instructionTemplate,
            RequiredFields = requiredFields,
            PreferredSourceKinds = preferredSourceKinds,
            CanonicalAnswerShape = canonicalAnswerShape,
            ValidationSummary = "required_fields_present; canonical_answer_singleton; no_cross_track_leakage"
        };
    }

    private static RamTrainingSourceSystemRecord BuildSourceSystem(
        string sourceId,
        string sourceKind,
        string locationHint,
        bool present,
        int recordCount,
        string preferredUse,
        string summary)
    {
        return new RamTrainingSourceSystemRecord
        {
            SourceId = sourceId,
            SourceKind = sourceKind,
            LocationHint = locationHint,
            Present = present,
            RecordCount = recordCount,
            PreferredUse = preferredUse,
            Summary = summary
        };
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

    private static bool IsRepairClosureArtifact(string content, string artifactType)
    {
        if (!string.Equals(artifactType, "verification_result", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            var record = JsonSerializer.Deserialize<VerificationOutcomeRecord>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (record is null)
                return false;

            return !string.IsNullOrWhiteSpace(record.SourcePatchDraftId)
                || !string.IsNullOrWhiteSpace(record.SourceRepairProposalId);
        }
        catch
        {
            return false;
        }
    }
}
