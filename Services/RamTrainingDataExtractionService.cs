using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.IO;
using RAM.Models;

namespace RAM.Services;

public sealed partial class RamTrainingDataExtractionService
{
    public List<RamTrainingIntermediateRecord> Extract(
        string workspaceRoot,
        IReadOnlyList<ArtifactRecord> artifacts,
        IReadOnlyList<RamFileTouchRecord> fileTouches,
        IReadOnlyList<TaskboardSkipDecisionRecord> skipRecords)
    {
        var results = new List<RamTrainingIntermediateRecord>();
        var artifactsById = artifacts.ToDictionary(current => BuildArtifactKey(current.WorkspaceRoot, current.Id));
        var latestArtifactsByPath = artifacts
            .GroupBy(current => BuildWorkspacePathKey(current.WorkspaceRoot, current.RelativePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(current => ParseUtc(current.UpdatedUtc)).ThenByDescending(current => current.Id).First())
            .ToDictionary(current => BuildWorkspacePathKey(current.WorkspaceRoot, current.RelativePath), StringComparer.OrdinalIgnoreCase);

        ExtractTaskboardImportRows(results, artifacts, artifactsById);
        ExtractPhraseResolutionRows(results, artifacts);
        ExtractExecutionLaneRows(results, artifacts);
        ExtractExecutionGoalFallbackRows(results, artifacts);
        ExtractDecompositionFallbackRows(results, artifacts);
        ExtractSkipRows(results, workspaceRoot, skipRecords);
        ExtractSummaryRows(results, artifacts);
        ExtractRepairRows(results, artifacts);
        ExtractGenerationRows(results, workspaceRoot, artifacts, latestArtifactsByPath, fileTouches);

        return results
            .OrderBy(current => current.TrackHint)
            .ThenBy(current => current.DatasetFamilyHint, StringComparer.OrdinalIgnoreCase)
            .ThenBy(current => current.SourceKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(current => current.Lineage.CreatedUtc, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ExtractTaskboardImportRows(
        ICollection<RamTrainingIntermediateRecord> results,
        IReadOnlyList<ArtifactRecord> artifacts,
        IReadOnlyDictionary<string, ArtifactRecord> artifactsById)
    {
        foreach (var artifact in artifacts.Where(current => string.Equals(current.ArtifactType, "taskboard_import", StringComparison.OrdinalIgnoreCase)))
        {
            var record = TryDeserialize<TaskboardImportRecord>(artifact.Content);
            if (record is null)
                continue;

            var rawArtifact = record.RawArtifactId > 0 && artifactsById.TryGetValue(BuildArtifactKey(artifact.WorkspaceRoot, record.RawArtifactId), out var linkedRaw)
                ? linkedRaw
                : null;
            var rawInput = FirstNonEmpty(rawArtifact?.Content, record.Title);
            var label = ResolveImportIntentLabel(record);
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(rawInput))
                continue;

            results.Add(BuildIntermediate(
                RamTrainingDatasetTrack.Intake,
                "intake_intent_classification",
                "taskboard_import",
                rawInput,
                $"document_type={record.DocumentType} title_pattern={record.TitlePatternKind} classification_confidence={record.ClassificationConfidence} reason={FirstNonEmpty(record.ClassificationReason, record.ValidationSummary)} matched={FormatList(record.MatchedSignals)} missing={FormatList(record.MissingExpectedSignals)}",
                label,
                [],
                ["preferred_import_classification"],
                artifact,
                96,
                record.ClassificationConfidence switch
                {
                    TaskboardClassificationConfidence.High => 95,
                    TaskboardClassificationConfidence.Medium => 84,
                    _ => 72
                }));
        }
    }

    private static void ExtractPhraseResolutionRows(
        ICollection<RamTrainingIntermediateRecord> results,
        IReadOnlyList<ArtifactRecord> artifacts)
    {
        foreach (var artifact in artifacts.Where(current => string.Equals(current.ArtifactType, "taskboard_phrase_family_resolution", StringComparison.OrdinalIgnoreCase)))
        {
            var record = TryDeserialize<TaskboardPhraseFamilyResolutionRecord>(artifact.Content);
            if (record is null || string.IsNullOrWhiteSpace(record.PhraseFamily))
                continue;

            var qualitySignals = new List<string>();
            if (!record.IsBlocked)
                qualitySignals.Add("resolution_unblocked");
            if (record.CandidatePhraseFamilies.Count <= 1)
                qualitySignals.Add("single_candidate");

            results.Add(BuildIntermediate(
                RamTrainingDatasetTrack.Intake,
                "intake_phrase_resolution",
                "taskboard_phrase_family_resolution",
                $"Title: {FirstNonEmpty(record.WorkItemTitle, "(none)")}",
                $"resolution_source={record.ResolutionSource} confidence={FirstNonEmpty(record.Confidence, record.DeterministicConfidence)} candidates={FormatList(record.CandidatePhraseFamilies)} summary={FirstNonEmpty(record.ResolutionSummary, record.DeterministicReason, record.BlockerMessage)}",
                NormalizeLabel(record.PhraseFamily),
                record.CandidatePhraseFamilies.Count > 1 ? ["candidate_phrase_family_conflict"] : [],
                qualitySignals,
                artifact,
                100,
                record.IsBlocked ? 58 : 94));
        }
    }

    private static void ExtractExecutionLaneRows(
        ICollection<RamTrainingIntermediateRecord> results,
        IReadOnlyList<ArtifactRecord> artifacts)
    {
        foreach (var artifact in artifacts.Where(current => string.Equals(current.ArtifactType, "taskboard_execution_lane", StringComparison.OrdinalIgnoreCase)))
        {
            var lane = TryDeserialize<TaskboardExecutionLaneResolution>(artifact.Content);
            if (lane is null)
                continue;

            var baseInput = BuildLaneInput(lane);
            var baseContext = SerializePacket(new
            {
                normalized_request = FirstNonEmpty(lane.PromptText, lane.SourceWorkItemTitle, "(none)"),
                phrase_family = NormalizeLabel(lane.PhraseFamily),
                work_family = NormalizeLabel(lane.WorkFamily),
                operation_kind = NormalizeLabel(lane.OperationKind),
                lane_kind = NormalizeLabel(lane.LaneKind.ToString()),
                selected_tool = NormalizeLabel(lane.SelectedToolId),
                selected_chain = NormalizeLabel(lane.SelectedChainTemplateId),
                blocker_code = NormalizeLabel(lane.Blocker.Code.ToString()),
                blocker_message = FirstNonEmpty(lane.Blocker.Message, lane.Blocker.Detail),
                resolution_reason = lane.ResolutionReason,
                selection_path = lane.SelectionPath
            });
            var baseSignals = new List<string>();
            if (lane.LaneKind == TaskboardExecutionLaneKind.ChainLane)
                baseSignals.Add("chain_lane_selected");
            if (lane.LaneKind == TaskboardExecutionLaneKind.ToolLane)
                baseSignals.Add("tool_lane_selected");

            var intentLabel = ResolveLaneIntentLabel(lane);
            if (!string.IsNullOrWhiteSpace(intentLabel))
            {
                results.Add(BuildIntermediate(
                    RamTrainingDatasetTrack.Intake,
                    "intake_intent_classification",
                    "taskboard_execution_lane",
                    baseInput,
                    baseContext,
                    intentLabel,
                    lane.Blocker.Code == TaskboardExecutionLaneBlockerCode.AmbiguousLaneCandidates ? ["ambiguous_lane_candidates"] : [],
                    baseSignals,
                    artifact,
                    94,
                    lane.LaneKind == TaskboardExecutionLaneKind.BlockedLane ? 70 : 90));
            }

            if (!string.IsNullOrWhiteSpace(lane.WorkFamily))
            {
                results.Add(BuildIntermediate(
                    RamTrainingDatasetTrack.Intake,
                    "intake_work_family_resolution",
                    "taskboard_execution_lane",
                    baseInput,
                    baseContext,
                    NormalizeLabel(lane.WorkFamily),
                    lane.WorkFamilyCandidates.Count > 1 ? ["multiple_work_family_candidates"] : [],
                    ["lane_resolution_authoritative"],
                    artifact,
                    100,
                    93));
            }

            if (!string.IsNullOrWhiteSpace(lane.OperationKind))
            {
                results.Add(BuildIntermediate(
                    RamTrainingDatasetTrack.Intake,
                    "intake_operation_selection",
                    "taskboard_execution_lane",
                    baseInput,
                    baseContext,
                    NormalizeLabel(lane.OperationKind),
                    [],
                    ["lane_resolution_authoritative"],
                    artifact,
                    100,
                    93));
            }

            var laneOutput = ResolveLaneSelectionOutput(lane);
            if (!string.IsNullOrWhiteSpace(laneOutput))
            {
                results.Add(BuildIntermediate(
                    RamTrainingDatasetTrack.Intake,
                    "intake_lane_chain_selection",
                    "taskboard_execution_lane",
                    baseInput,
                    baseContext,
                    laneOutput,
                    lane.Blocker.Code == TaskboardExecutionLaneBlockerCode.AmbiguousLaneCandidates ? ["ambiguous_lane_candidates"] : [],
                    baseSignals,
                    artifact,
                    100,
                    lane.LaneKind == TaskboardExecutionLaneKind.BlockedLane ? 72 : 95));
            }
        }
    }

    private static void ExtractExecutionGoalFallbackRows(
        ICollection<RamTrainingIntermediateRecord> results,
        IReadOnlyList<ArtifactRecord> artifacts)
    {
        foreach (var artifact in artifacts.Where(current => string.Equals(current.ArtifactType, "taskboard_execution_goal", StringComparison.OrdinalIgnoreCase)))
        {
            var goal = TryDeserialize<TaskboardExecutionGoalResolution>(artifact.Content);
            if (goal is null)
                continue;

            var input = $"Title: {FirstNonEmpty(goal.SourceWorkItemTitle, "(none)")}\nPrompt: {FirstNonEmpty(goal.PromptText, "(none)")}";
            var context = SerializePacket(new
            {
                normalized_request = FirstNonEmpty(goal.PromptText, goal.SourceWorkItemTitle, "(none)"),
                phrase_family = NormalizeLabel(goal.PhraseFamily),
                work_family = NormalizeLabel(goal.WorkFamily),
                operation_kind = NormalizeLabel(goal.OperationKind),
                goal_kind = NormalizeLabel(goal.GoalKind.ToString()),
                blocker_code = NormalizeLabel(goal.Blocker.Code.ToString()),
                blocker_message = FirstNonEmpty(goal.Blocker.Message, goal.Blocker.Detail),
                resolution_reason = goal.ResolutionReason
            });

            if (!string.IsNullOrWhiteSpace(goal.WorkFamily))
            {
                results.Add(BuildIntermediate(
                    RamTrainingDatasetTrack.Intake,
                    "intake_work_family_resolution",
                    "taskboard_execution_goal",
                    input,
                    context,
                    NormalizeLabel(goal.WorkFamily),
                    goal.Blocker.Code == TaskboardExecutionGoalBlockerCode.NoDeterministicLane ? ["goal_missing_deterministic_lane"] : [],
                    ["goal_resolution_fallback"],
                    artifact,
                    88,
                    81));
            }

            if (!string.IsNullOrWhiteSpace(goal.OperationKind))
            {
                results.Add(BuildIntermediate(
                    RamTrainingDatasetTrack.Intake,
                    "intake_operation_selection",
                    "taskboard_execution_goal",
                    input,
                    context,
                    NormalizeLabel(goal.OperationKind),
                    [],
                    ["goal_resolution_fallback"],
                    artifact,
                    88,
                    81));
            }
        }
    }

    private static void ExtractDecompositionFallbackRows(
        ICollection<RamTrainingIntermediateRecord> results,
        IReadOnlyList<ArtifactRecord> artifacts)
    {
        foreach (var artifact in artifacts.Where(current => string.Equals(current.ArtifactType, "taskboard_decomposition", StringComparison.OrdinalIgnoreCase)))
        {
            var record = TryDeserialize<TaskboardWorkItemDecompositionRecord>(artifact.Content);
            if (record is null)
                continue;

            foreach (var subItem in record.SubItems)
            {
                var input = $"Parent: {FirstNonEmpty(record.OriginalTitle, "(none)")}\nSub-item: {FirstNonEmpty(subItem.Description, subItem.PromptText, subItem.Summary, "(none)")}";
                var context = $"phrase={FirstNonEmpty(subItem.PhraseFamily, record.PhraseFamily, "(none)")} template={FirstNonEmpty(subItem.TemplateId, record.TemplateId, "(none)")} stack={FirstNonEmpty(subItem.TargetStack, record.BuildProfile.StackFamily.ToString(), "(none)")}";

                if (!string.IsNullOrWhiteSpace(subItem.WorkFamily))
                {
                    results.Add(BuildIntermediate(
                        RamTrainingDatasetTrack.Intake,
                        "intake_work_family_resolution",
                        "taskboard_decomposition",
                        input,
                        context,
                        NormalizeLabel(subItem.WorkFamily),
                        [],
                        ["decomposition_fallback"],
                        artifact,
                        78,
                        74));
                }

                if (!string.IsNullOrWhiteSpace(subItem.OperationKind))
                {
                    results.Add(BuildIntermediate(
                        RamTrainingDatasetTrack.Intake,
                        "intake_operation_selection",
                        "taskboard_decomposition",
                        input,
                        context,
                        NormalizeLabel(subItem.OperationKind),
                        [],
                        ["decomposition_fallback"],
                        artifact,
                        78,
                        74));
                }
            }
        }
    }

    private static void ExtractSkipRows(
        ICollection<RamTrainingIntermediateRecord> results,
        string workspaceRoot,
        IReadOnlyList<TaskboardSkipDecisionRecord> skipRecords)
    {
        foreach (var record in skipRecords)
        {
            results.Add(BuildIntermediate(
                RamTrainingDatasetTrack.Intake,
                "intake_skip_reasoning",
                "taskboard_skip_record",
                $"Work item: {FirstNonEmpty(record.WorkItemTitle, "(none)")}\nTool: {FirstNonEmpty(record.ToolName, "(none)")}\nStep: {FirstNonEmpty(record.StepId, "(none)")}",
                SerializePacket(new
                {
                    skip_family = NormalizeLabel(record.SkipFamily),
                    evidence_source = NormalizeLabel(record.EvidenceSource),
                    evidence_summary = FirstNonEmpty(record.EvidenceSummary, "(none)"),
                    linked_files = record.LinkedFilePaths,
                    repeated_touches_avoided = record.RepeatedTouchesAvoidedCount,
                    fast_path = record.UsedFileTouchFastPath
                }),
                NormalizeLabel(record.ReasonCode),
                [],
                record.UsedFileTouchFastPath ? ["file_touch_fast_path"] : [],
                new ArtifactRecord
                {
                    Id = record.Id,
                    WorkspaceRoot = workspaceRoot,
                    SourceRunStateId = record.RunStateId,
                    SourceBatchId = record.BatchId,
                    SourceWorkItemId = record.WorkItemId,
                    CreatedUtc = record.CreatedUtc,
                    UpdatedUtc = record.CreatedUtc,
                    ArtifactType = "taskboard_skip_record",
                    RelativePath = ".ram/ram.db:taskboard_skip_records"
                },
                100,
                96));
        }
    }

    private static void ExtractSummaryRows(
        ICollection<RamTrainingIntermediateRecord> results,
        IReadOnlyList<ArtifactRecord> artifacts)
    {
        foreach (var artifact in artifacts.Where(current => string.Equals(current.ArtifactType, "taskboard_run_summary", StringComparison.OrdinalIgnoreCase)))
        {
            var summary = TryDeserialize<TaskboardRunTerminalSummaryRecord>(artifact.Content);
            if (summary is null)
                continue;

            results.Add(BuildIntermediate(
                RamTrainingDatasetTrack.Intake,
                "intake_summary_explanations",
                "taskboard_run_summary",
                $"Final status: {FirstNonEmpty(summary.FinalStatus, "(none)")}\nTerminal category: {FirstNonEmpty(summary.TerminalCategory, "(none)")}\nTerminal work item: {FirstNonEmpty(summary.TerminalWorkItemTitle, "(none)")}",
                SerializePacket(new
                {
                    blocker_work_family = NormalizeLabel(summary.BlockerWorkFamily),
                    blocker_phrase_family = NormalizeLabel(summary.BlockerPhraseFamily),
                    blocker_operation_kind = NormalizeLabel(summary.BlockerOperationKind),
                    blocker_lane_kind = NormalizeLabel(summary.BlockerLaneKind),
                    blocker_reason = FirstNonEmpty(summary.BlockerReason, "(none)"),
                    repair_attempt_summary = FirstNonEmpty(summary.RepairAttemptSummary, "(none)"),
                    repair_mutation_observed = summary.RepairMutationObserved,
                    verification_after_mutation = NormalizeLabel(summary.VerificationAfterMutationOutcome),
                    completion_proof_template = FirstNonEmpty(summary.PreferredCompletionProofTemplateId, "(none)"),
                    completion_proof_reason = FirstNonEmpty(summary.PreferredCompletionProofReason, "(none)"),
                    proof_insufficiency = summary.PreferredCompletionProofInsufficiencyReasons,
                    terminal_note = FirstNonEmpty(summary.TerminalNote, "(none)")
                }),
                BuildSummaryExplanation(summary),
                summary.PreferredCompletionProofInsufficiencyReasons.Count > 0
                    ? [.. summary.PreferredCompletionProofInsufficiencyReasons.Select(NormalizeLabel), "proof_insufficiency_present"]
                    : [],
                ["terminal_summary_source"],
                artifact,
                100,
                95));
        }
    }
}

public sealed partial class RamTrainingDataExtractionService
{
    private static void ExtractRepairRows(
        ICollection<RamTrainingIntermediateRecord> results,
        IReadOnlyList<ArtifactRecord> artifacts)
    {
        var proposals = artifacts
            .Where(current => string.Equals(current.ArtifactType, "repair_proposal", StringComparison.OrdinalIgnoreCase))
            .Select(current => (Artifact: current, Record: TryDeserialize<RepairProposalRecord>(current.Content)))
            .Where(current => current.Record is not null)
            .ToDictionary(current => BuildScopedIdentifier(current.Artifact.WorkspaceRoot, current.Record!.ProposalId), current => (current.Artifact, current.Record!), StringComparer.OrdinalIgnoreCase);
        var drafts = artifacts
            .Where(current => string.Equals(current.ArtifactType, "patch_draft", StringComparison.OrdinalIgnoreCase))
            .Select(current => (Artifact: current, Record: TryDeserialize<PatchDraftRecord>(current.Content)))
            .Where(current => current.Record is not null)
            .ToList();
        var outcomesByDraftId = artifacts
            .Where(current => string.Equals(current.ArtifactType, "verification_result", StringComparison.OrdinalIgnoreCase))
            .Select(current => TryDeserialize<VerificationOutcomeRecord>(current.Content))
            .Where(current => current is not null && !string.IsNullOrWhiteSpace(current.SourcePatchDraftId))
            .GroupBy(current => current!.SourcePatchDraftId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => BuildScopedIdentifier(group.First()!.WorkspaceRoot, group.Key),
                group => group.OrderByDescending(current => ParseUtc(current!.CreatedUtc)).First()!,
                StringComparer.OrdinalIgnoreCase);

        foreach (var draftEntry in drafts)
        {
            var draft = draftEntry.Record;
            if (draft is null)
                continue;

            if (!proposals.TryGetValue(BuildScopedIdentifier(draftEntry.Artifact.WorkspaceRoot, draft.SourceProposalId), out var proposalEntry))
                continue;

            var proposal = proposalEntry.Item2;
            outcomesByDraftId.TryGetValue(BuildScopedIdentifier(draftEntry.Artifact.WorkspaceRoot, draft.DraftId), out var outcome);
            var input = SerializePacket(new
            {
                target_file = FirstNonEmpty(draft.TargetFilePath, proposal.TargetFilePath, "(none)"),
                failure = FirstNonEmpty(proposal.FailureSummary, draft.FailureSummary, "(none)"),
                scope = FirstNonEmpty(draft.AllowedEditScope, draft.MutationFamily, proposal.ProposedActionType, "(none)"),
                action_type = FirstNonEmpty(proposal.ProposedActionType, "(none)"),
                targeting_strategy = FirstNonEmpty(proposal.TargetingStrategy, "(none)"),
                excerpt = FirstNonEmpty(draft.OriginalExcerpt, proposal.FileExcerpt, "(none)")
            });
            var context = SerializePacket(new
            {
                draft_kind = FirstNonEmpty(draft.DraftKind, "(none)"),
                can_apply_locally = draft.CanApplyLocally,
                symbol_recovery_status = FirstNonEmpty(draft.SymbolRecoveryStatus, proposal.SymbolRecoveryStatus, "(none)"),
                symbol_recovery_summary = FirstNonEmpty(draft.SymbolRecoverySummary, proposal.SymbolRecoverySummary, "(none)"),
                preview_rationale = FirstNonEmpty(draft.RationaleSummary, proposal.Rationale, "(none)"),
                verification_outcome = NormalizeLabel(outcome?.OutcomeClassification ?? "not_run"),
                verification_explanation = FirstNonEmpty(outcome?.Explanation, "(none)")
            });
            var closureStatus = ResolveRepairClosureStatus(draft, outcome);
            var output = SerializePacket(new
            {
                repair_status = closureStatus,
                target_file = FirstNonEmpty(draft.TargetFilePath, proposal.TargetFilePath, "(none)"),
                start_line = draft.StartLine,
                end_line = draft.EndLine,
                replacement = draft.ReplacementText,
                closure = FirstNonEmpty(outcome?.OutcomeClassification, "not_closed")
            });
            var reasonCodes = BuildRepairReasonCodes(draft, proposal, outcome);
            var qualitySignals = BuildRepairQualitySignals(draft, outcome);

            results.Add(BuildIntermediate(
                RamTrainingDatasetTrack.Coder,
                "coder_file_local_repair",
                "repair_proposal",
                input,
                context,
                output,
                reasonCodes,
                qualitySignals,
                draftEntry.Artifact,
                100,
                ResolveRepairConfidence(draft, outcome)));
        }
    }

    private static void ExtractGenerationRows(
        ICollection<RamTrainingIntermediateRecord> results,
        string workspaceRoot,
        IReadOnlyList<ArtifactRecord> artifacts,
        IReadOnlyDictionary<string, ArtifactRecord> latestArtifactsByPath,
        IReadOnlyList<RamFileTouchRecord> fileTouches)
    {
        foreach (var artifact in artifacts.Where(current => string.Equals(current.ArtifactType, "behavior_depth_evidence", StringComparison.OrdinalIgnoreCase)))
        {
            var evidence = TryDeserialize<BehaviorDepthEvidenceRecord>(artifact.Content);
            if (evidence is null || string.IsNullOrWhiteSpace(evidence.TargetPath))
                continue;

            var normalizedTargetPath = NormalizePath(evidence.TargetPath);
            latestArtifactsByPath.TryGetValue(BuildWorkspacePathKey(artifact.WorkspaceRoot, normalizedTargetPath), out var fileArtifact);
            var datasetFamilies = ResolveGenerationDatasetFamilies(evidence.Profile);
            if (datasetFamilies.Count == 0)
                continue;

            var targetContent = fileArtifact?.Content ?? "";
            var context = BuildGenerationContext(artifact.WorkspaceRoot, evidence, normalizedTargetPath, fileArtifact, artifacts, fileTouches);
            var signals = new List<string>
            {
                $"behavior_depth={NormalizeLabel(evidence.BehaviorDepthTier)}",
                $"profile={NormalizeLabel(evidence.Profile)}",
                $"recommendation={NormalizeLabel(evidence.CompletionRecommendation)}",
                $"completion_strength={NormalizeLabel(evidence.CompletionStrength)}",
                $"output_quality={NormalizeLabel(evidence.OutputQuality)}"
            };
            if (evidence.RepositoryOrServiceLinkageFound)
                signals.Add("repository_linkage_found");
            if (evidence.CommandViewModelOrBindingEvidenceFound)
                signals.Add("binding_evidence_found");
            if (evidence.DiOrRegistrationEvidenceFound)
                signals.Add("registration_evidence_found");
            if (evidence.TestLinkageFound)
                signals.Add("test_linkage_found");
            AddDerivedGenerationSignals(normalizedTargetPath, targetContent, signals);

            var reasonCodes = BuildGenerationReasonCodes(evidence, fileArtifact, targetContent, signals);

            foreach (var datasetFamily in datasetFamilies)
            {
                results.Add(BuildIntermediate(
                    RamTrainingDatasetTrack.Coder,
                    datasetFamily,
                    "behavior_depth_evidence",
                    BuildGenerationInput(datasetFamily, normalizedTargetPath, evidence, context),
                    context,
                    targetContent,
                    reasonCodes,
                    signals,
                    BuildArtifactLineageProxy(artifact.WorkspaceRoot, artifact, fileArtifact),
                    92,
                    ResolveGenerationConfidence(evidence, fileArtifact)));
            }
        }
    }

    private static ArtifactRecord BuildArtifactLineageProxy(string workspaceRoot, ArtifactRecord evidenceArtifact, ArtifactRecord? fileArtifact)
    {
        return new ArtifactRecord
        {
            Id = evidenceArtifact.Id,
            WorkspaceRoot = workspaceRoot,
            ArtifactType = evidenceArtifact.ArtifactType,
            RelativePath = evidenceArtifact.RelativePath,
            SourceRunStateId = FirstNonEmpty(fileArtifact?.SourceRunStateId, evidenceArtifact.SourceRunStateId),
            SourceBatchId = FirstNonEmpty(fileArtifact?.SourceBatchId, evidenceArtifact.SourceBatchId),
            SourceWorkItemId = FirstNonEmpty(fileArtifact?.SourceWorkItemId, evidenceArtifact.SourceWorkItemId),
            CreatedUtc = FirstNonEmpty(fileArtifact?.CreatedUtc, evidenceArtifact.CreatedUtc),
            UpdatedUtc = FirstNonEmpty(fileArtifact?.UpdatedUtc, evidenceArtifact.UpdatedUtc)
        };
    }

    private static List<string> ResolveGenerationDatasetFamilies(string profile)
    {
        var normalized = NormalizeLabel(profile);
        return normalized switch
        {
            "contract_generation" => ["coder_scaffold_generation", "coder_validation_aware_generation"],
            "runtime_wiring" or "wpf_shell_integration" or "wpf_viewmodel_impl" or "wpf_xaml_layout_impl" => ["coder_runtime_wiring", "coder_validation_aware_generation"],
            "repository_implementation" or "test_registry_impl" or "snapshot_builder_impl" or "findings_normalizer_impl" or "test_helper_impl" or "builder_impl" or "normalizer_impl" => ["coder_runtime_wiring", "coder_validation_aware_generation"],
            "simple_implementation" => ["coder_validation_aware_generation"],
            _ => []
        };
    }

    private static string BuildGenerationInput(string datasetFamily, string targetPath, BehaviorDepthEvidenceRecord evidence, string context)
    {
        return string.Join(
            Environment.NewLine,
            datasetFamily switch
            {
                "coder_scaffold_generation" => "Generate the bounded scaffold file.",
                "coder_runtime_wiring" => "Generate the bounded runtime wiring file.",
                "coder_validation_aware_generation" => "Generate the bounded file so it satisfies the active validation contract.",
                "coder_behavior_depth_upgrade" => "Upgrade the bounded file so the weak behavior path is closed.",
                _ => "Generate the bounded file."
            },
            $"Target file: {targetPath}",
            $"Profile: {NormalizeLabel(evidence.Profile)}",
            $"Behavior depth: {NormalizeLabel(evidence.BehaviorDepthTier)}",
            $"Follow-up recommendation: {FirstNonEmpty(evidence.FollowUpRecommendation, "(none)")}",
            context);
    }

    private static string BuildGenerationContext(
        string sourceWorkspaceRoot,
        BehaviorDepthEvidenceRecord evidence,
        string targetPath,
        ArtifactRecord? fileArtifact,
        IReadOnlyList<ArtifactRecord> artifacts,
        IReadOnlyList<RamFileTouchRecord> fileTouches)
    {
        var directory = NormalizePath(Path.GetDirectoryName(targetPath)?.Replace('\\', '/') ?? "");
        var siblingFiles = artifacts
            .Where(current =>
                string.Equals(current.WorkspaceRoot, sourceWorkspaceRoot, StringComparison.OrdinalIgnoreCase)
                && !current.RelativePath.StartsWith(".ram/", StringComparison.OrdinalIgnoreCase)
                &&
                NormalizePath(Path.GetDirectoryName(current.RelativePath)?.Replace('\\', '/') ?? "") == directory
                && !string.Equals(NormalizePath(current.RelativePath), targetPath, StringComparison.OrdinalIgnoreCase)
                && IsCodeLikePath(current.RelativePath))
            .Select(current => Path.GetFileName(current.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(current => current, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        var targetFramework = ResolveTargetFramework(sourceWorkspaceRoot, targetPath, artifacts);
        var namespaceName = ResolveNamespaceFromContent(fileArtifact?.Content ?? "");
        var touchCount = fileTouches.Count(current =>
            string.Equals(current.WorkspaceRoot, sourceWorkspaceRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizePath(current.FilePath), targetPath, StringComparison.OrdinalIgnoreCase));

        return SerializePacket(new
        {
            target_framework = FirstNonEmpty(targetFramework, "(unknown)"),
            @namespace = FirstNonEmpty(namespaceName, "(unknown)"),
            sibling_files = siblingFiles,
            caller_refs = evidence.CallerReferencePaths,
            registration_refs = evidence.DiOrRegistrationEvidencePaths,
            binding_refs = evidence.CommandViewModelOrBindingEvidencePaths,
            repository_service_refs = evidence.RepositoryOrServiceLinkagePaths,
            test_refs = evidence.TestLinkagePaths,
            shallow_flags = evidence.ShallowPatternFlags,
            touch_count = touchCount,
            stronger_behavior_missing = evidence.StrongerBehaviorProofStillMissing,
            completion_recommendation = NormalizeLabel(evidence.CompletionRecommendation),
            follow_up_recommendation = FirstNonEmpty(evidence.FollowUpRecommendation, "(none)")
        });
    }

    private static int ResolveGenerationConfidence(BehaviorDepthEvidenceRecord evidence, ArtifactRecord? fileArtifact)
    {
        var confidence = 82;
        if (fileArtifact is not null)
            confidence += 4;
        if (string.Equals(evidence.BehaviorDepthTier, "integrated_behavior_impl", StringComparison.OrdinalIgnoreCase))
            confidence += 10;
        else if (string.Equals(evidence.BehaviorDepthTier, "family_aligned_impl", StringComparison.OrdinalIgnoreCase))
            confidence += 4;
        if (evidence.ShallowPatternFlags.Count > 0)
            confidence -= 18;
        if (evidence.StrongerBehaviorProofStillMissing)
            confidence -= 8;

        return Math.Clamp(confidence, 0, 100);
    }

    private static List<string> BuildGenerationReasonCodes(
        BehaviorDepthEvidenceRecord evidence,
        ArtifactRecord? fileArtifact,
        string targetContent,
        IReadOnlyCollection<string> signals)
    {
        var reasonCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var flag in evidence.ShallowPatternFlags.Select(NormalizeLabel))
            reasonCodes.Add(flag);

        if (signals.Any(signal => signal.Contains("binding_evidence_found", StringComparison.OrdinalIgnoreCase)))
            reasonCodes.Remove("ui_surface_without_binding_evidence");
        if (signals.Any(signal => signal.Contains("registration_evidence_found", StringComparison.OrdinalIgnoreCase)))
            reasonCodes.Remove("runtime_wiring_without_registration_evidence");
        if (signals.Any(signal => signal.Contains("test_linkage_found", StringComparison.OrdinalIgnoreCase)))
            reasonCodes.Remove("helper_without_test_linkage");

        var completionStrength = NormalizeLabel(evidence.CompletionStrength);
        if (string.Equals(evidence.CompletionRecommendation, "followup_required_for_behavior_depth", StringComparison.OrdinalIgnoreCase)
            && (completionStrength is "accepted_write_only" or "accepted_structural_impl"))
        {
            reasonCodes.Add("accepted_write_without_closure");
        }

        if (fileArtifact is null || string.IsNullOrWhiteSpace(targetContent))
            reasonCodes.Add("missing_target_file_artifact");

        return reasonCodes.ToList();
    }

    private static void AddDerivedGenerationSignals(string targetPath, string targetContent, ICollection<string> signals)
    {
        if (string.IsNullOrWhiteSpace(targetContent))
            return;

        if (targetPath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            && targetContent.Contains("{Binding", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("binding_evidence_found");
        }

        if (targetContent.Contains("AddSingleton(", StringComparison.OrdinalIgnoreCase)
            || targetContent.Contains("AddScoped(", StringComparison.OrdinalIgnoreCase)
            || targetContent.Contains("AddTransient(", StringComparison.OrdinalIgnoreCase)
            || targetContent.Contains("services.", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("registration_evidence_found");
        }

        if (targetContent.Contains("[Fact]", StringComparison.OrdinalIgnoreCase)
            || targetContent.Contains("[Test]", StringComparison.OrdinalIgnoreCase)
            || targetContent.Contains("Assert.", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("test_linkage_found");
        }
    }
}

public sealed partial class RamTrainingDataExtractionService
{
    private static string ResolveImportIntentLabel(TaskboardImportRecord record)
    {
        if (record.AcceptedAsTaskboardCandidate
            || record.DocumentType is TaskboardDocumentType.TaskboardCandidate or TaskboardDocumentType.CodexTaskboard)
        {
            return "taskboard_candidate";
        }

        return record.DocumentType switch
        {
            TaskboardDocumentType.UnsupportedStructuredDocument => "unsupported_or_blocked",
            TaskboardDocumentType.PlainRequest => "unsupported_or_blocked",
            _ => ""
        };
    }

    private static string ResolveLaneIntentLabel(TaskboardExecutionLaneResolution lane)
    {
        if (lane.LaneKind is TaskboardExecutionLaneKind.BlockedLane or TaskboardExecutionLaneKind.ManualOnlyLane)
            return "unsupported_or_blocked";

        var operation = NormalizeLabel(lane.OperationKind);
        var phrase = NormalizeLabel(lane.PhraseFamily);
        var family = NormalizeLabel(lane.WorkFamily);
        var selectedTool = NormalizeLabel(lane.SelectedToolId);
        var selectedChain = NormalizeLabel(lane.SelectedChainTemplateId);

        if (operation.Contains("repair", StringComparison.OrdinalIgnoreCase)
            || family.Contains("repair", StringComparison.OrdinalIgnoreCase)
            || selectedTool is "plan_repair" or "preview_patch_draft" or "apply_patch_draft"
            || selectedChain.Contains("repair", StringComparison.OrdinalIgnoreCase))
        {
            return "repair_request";
        }

        if (operation.Contains("build", StringComparison.OrdinalIgnoreCase)
            || phrase == "build_verify"
            || family.Contains("build", StringComparison.OrdinalIgnoreCase)
            || selectedChain == "workspace.build_verify.v1")
        {
            return "build_verify_request";
        }

        if (operation.Contains("inspect", StringComparison.OrdinalIgnoreCase)
            || selectedTool is "read_file_chunk" or "inspect_project" or "list_projects")
        {
            return "file_inspection_request";
        }

        if (family.Contains("summary", StringComparison.OrdinalIgnoreCase)
            || operation.Contains("summary", StringComparison.OrdinalIgnoreCase)
            || lane.SourceWorkItemTitle.Contains("summary", StringComparison.OrdinalIgnoreCase))
        {
            return "summary_request";
        }

        return "";
    }

    private static string ResolveLaneSelectionOutput(TaskboardExecutionLaneResolution lane)
    {
        return lane.LaneKind switch
        {
            TaskboardExecutionLaneKind.ChainLane when !string.IsNullOrWhiteSpace(lane.SelectedChainTemplateId) => NormalizeLabel(lane.SelectedChainTemplateId),
            TaskboardExecutionLaneKind.ToolLane when !string.IsNullOrWhiteSpace(lane.SelectedToolId) => NormalizeLabel(lane.SelectedToolId),
            TaskboardExecutionLaneKind.BlockedLane => $"blocked:{NormalizeLabel(lane.Blocker.Code.ToString())}",
            TaskboardExecutionLaneKind.ManualOnlyLane => "manual_only",
            _ => ""
        };
    }

    private static string BuildLaneInput(TaskboardExecutionLaneResolution lane)
    {
        return string.Join(
            Environment.NewLine,
            $"Title: {FirstNonEmpty(lane.SourceWorkItemTitle, "(none)")}",
            $"Prompt: {FirstNonEmpty(lane.PromptText, "(none)")}");
    }

    private static string BuildSummaryExplanation(TaskboardRunTerminalSummaryRecord summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.BlockerReason))
        {
            var insufficiency = summary.PreferredCompletionProofInsufficiencyReasons.Count == 0
                ? ""
                : $" Proof insufficient because {string.Join(", ", summary.PreferredCompletionProofInsufficiencyReasons)}.";
            return $"Run stopped because {summary.BlockerReason}. Existing proof: {FirstNonEmpty(summary.PreferredCompletionProofTemplateId, "(none)")}.{insufficiency}".Trim();
        }

        return $"Run ended with {FirstNonEmpty(summary.FinalStatus, "(none)")} and terminal note {FirstNonEmpty(summary.TerminalNote, "(none)")}. Completion proof: {FirstNonEmpty(summary.PreferredCompletionProofTemplateId, "(none)")}.";
    }

    private static RamTrainingIntermediateRecord BuildIntermediate(
        RamTrainingDatasetTrack track,
        string datasetFamily,
        string sourceKind,
        string rawInput,
        string rawContext,
        string rawResult,
        IReadOnlyList<string> reasonCodes,
        IReadOnlyList<string> qualitySignals,
        ArtifactRecord artifact,
        int sourcePriority,
        int extractionConfidence)
    {
        return new RamTrainingIntermediateRecord
        {
            RecordId = Guid.NewGuid().ToString("N"),
            TrackHint = track,
            DatasetFamilyHint = datasetFamily,
            SourceKind = sourceKind,
            RawInput = NormalizeText(rawInput),
            RawContext = NormalizeText(rawContext),
            RawResult = NormalizeText(rawResult),
            CanonicalLabel = NormalizeLabel(rawResult),
            ReasonCodes = [.. reasonCodes.Where(value => !string.IsNullOrWhiteSpace(value)).Select(NormalizeLabel).Distinct(StringComparer.OrdinalIgnoreCase)],
            QualitySignals = [.. qualitySignals.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)],
            Lineage = new RamTrainingLineageRecord
            {
                WorkspaceRoot = artifact.WorkspaceRoot,
                RunStateId = artifact.SourceRunStateId,
                PlanImportId = ExtractPlanImportId(artifact),
                BatchId = artifact.SourceBatchId,
                WorkItemId = artifact.SourceWorkItemId,
                SourceKind = sourceKind,
                SourcePriority = sourcePriority,
                ExtractionConfidence = extractionConfidence,
                SourceArtifactId = artifact.Id,
                SourceArtifactType = artifact.ArtifactType,
                SourceArtifactRelativePath = artifact.RelativePath,
                CreatedUtc = FirstNonEmpty(artifact.UpdatedUtc, artifact.CreatedUtc)
            }
        };
    }

    private static string ExtractPlanImportId(ArtifactRecord artifact)
    {
        var normalizedPath = NormalizePath(artifact.RelativePath);
        var marker = ".ram/taskboards/";
        var index = normalizedPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return "";

        var remainder = normalizedPath[(index + marker.Length)..];
        var slashIndex = remainder.IndexOf('/');
        return slashIndex <= 0 ? "" : remainder[..slashIndex];
    }

    private static string ResolveTargetFramework(string sourceWorkspaceRoot, string targetPath, IReadOnlyList<ArtifactRecord> artifacts)
    {
        var normalizedTargetPath = NormalizePath(targetPath);
        var directory = NormalizePath(Path.GetDirectoryName(normalizedTargetPath)?.Replace('\\', '/') ?? "");
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var projectArtifact = artifacts.FirstOrDefault(current =>
                string.Equals(current.WorkspaceRoot, sourceWorkspaceRoot, StringComparison.OrdinalIgnoreCase)
                &&
                string.Equals(Path.GetExtension(current.RelativePath), ".csproj", StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizePath(Path.GetDirectoryName(current.RelativePath)?.Replace('\\', '/') ?? ""), directory, StringComparison.OrdinalIgnoreCase));
            if (projectArtifact is not null)
                return TryReadTargetFramework(projectArtifact.Content);

            var parent = NormalizePath(Path.GetDirectoryName(directory)?.Replace('\\', '/') ?? "");
            if (string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                break;
            directory = parent;
        }

        return "";
    }

    private static string TryReadTargetFramework(string content)
    {
        try
        {
            var document = XDocument.Parse(content);
            var targetFramework = document.Descendants()
                .FirstOrDefault(current => string.Equals(current.Name.LocalName, "TargetFramework", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim();
            if (!string.IsNullOrWhiteSpace(targetFramework))
                return targetFramework;

            return document.Descendants()
                .FirstOrDefault(current => string.Equals(current.Name.LocalName, "TargetFrameworks", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault()
                ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string ResolveNamespaceFromContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        var fileScoped = Regex.Match(content, @"namespace\s+([A-Za-z_][A-Za-z0-9_\.]*)\s*;", RegexOptions.CultureInvariant);
        if (fileScoped.Success)
            return fileScoped.Groups[1].Value;

        var block = Regex.Match(content, @"namespace\s+([A-Za-z_][A-Za-z0-9_\.]*)\s*\{", RegexOptions.CultureInvariant);
        return block.Success ? block.Groups[1].Value : "";
    }

    private static bool IsCodeLikePath(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        return string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static T? TryDeserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return default;
        }
    }

    private static DateTime ParseUtc(string? value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.MinValue;
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

        var normalized = value.Trim().Replace('\\', '/');
        normalized = Regex.Replace(normalized, @"\s+", "_");
        return normalized.ToLowerInvariant();
    }

    private static string NormalizePath(string value)
    {
        return (value ?? "").Replace('\\', '/').Trim();
    }

    private static string BuildArtifactKey(string workspaceRoot, long artifactId)
    {
        return $"{NormalizePath(workspaceRoot)}|{artifactId}";
    }

    private static string BuildScopedIdentifier(string workspaceRoot, string identifier)
    {
        return $"{NormalizePath(workspaceRoot)}|{NormalizeLabel(identifier)}";
    }

    private static string BuildWorkspacePathKey(string workspaceRoot, string relativePath)
    {
        return $"{NormalizePath(workspaceRoot)}|{NormalizePath(relativePath)}";
    }

    private static string SerializePacket(object value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static List<string> BuildRepairReasonCodes(PatchDraftRecord draft, RepairProposalRecord proposal, VerificationOutcomeRecord? outcome)
    {
        var reasons = new List<string>();
        if (!draft.CanApplyLocally)
            reasons.Add("repair_preview_only");
        if (string.Equals(draft.DraftKind, "inspect_only", StringComparison.OrdinalIgnoreCase))
            reasons.Add("repair_preview_only");
        if (string.Equals(draft.SymbolRecoveryStatus, "generated_symbol_not_reconciled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(proposal.SymbolRecoveryStatus, "generated_symbol_not_reconciled", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("generated_symbol_not_reconciled");
        }
        if (outcome is null)
            reasons.Add("verification_not_run");
        else if (!string.Equals(outcome.OutcomeClassification, "verified_fixed", StringComparison.OrdinalIgnoreCase))
            reasons.Add($"verification_{NormalizeLabel(outcome.OutcomeClassification)}");

        return reasons
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildRepairQualitySignals(PatchDraftRecord draft, VerificationOutcomeRecord? outcome)
    {
        var signals = new List<string>
        {
            $"draft_kind={NormalizeLabel(draft.DraftKind)}",
            $"repair_scope={NormalizeLabel(FirstNonEmpty(draft.AllowedEditScope, draft.MutationFamily))}",
            $"symbol_recovery_status={NormalizeLabel(draft.SymbolRecoveryStatus)}"
        };

        if (draft.CanApplyLocally)
            signals.Add("local_patch_available");
        if (outcome is not null)
            signals.Add($"verification={NormalizeLabel(outcome.OutcomeClassification)}");
        if (string.Equals(outcome?.OutcomeClassification, "verified_fixed", StringComparison.OrdinalIgnoreCase))
            signals.Add("verified_fixed");

        return signals;
    }

    private static int ResolveRepairConfidence(PatchDraftRecord draft, VerificationOutcomeRecord? outcome)
    {
        if (string.Equals(outcome?.OutcomeClassification, "verified_fixed", StringComparison.OrdinalIgnoreCase))
            return 98;
        if (draft.CanApplyLocally)
            return 82;
        return 68;
    }

    private static string ResolveRepairClosureStatus(PatchDraftRecord draft, VerificationOutcomeRecord? outcome)
    {
        if (string.Equals(outcome?.OutcomeClassification, "verified_fixed", StringComparison.OrdinalIgnoreCase))
            return draft.ReplacementText.Length == 0 ? "rebuild_first_closed" : "applied_and_verified";
        if (draft.CanApplyLocally)
            return "applied_but_unverified";
        return "preview_only_not_closed";
    }

    private static string FormatList(IReadOnlyList<string> values)
    {
        return values.Count == 0
            ? "(none)"
            : string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
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
