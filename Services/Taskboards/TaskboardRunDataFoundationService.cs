using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardRunDataFoundationService
{
    private readonly TaskboardArtifactStore _artifactStore = new();
    private readonly RamDataDisciplineService _dataDisciplineService = new();

    public void FinalizeTerminalRunData(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardPlanRunStateRecord runState,
        TaskboardRunTerminalSummaryRecord summary,
        RamDbService ramDbService)
    {
        var artifacts = ramDbService.LoadArtifactsSince(workspaceRoot, FirstNonEmpty(summary.StartedUtc, runState.StartedUtc), 2000);
        var fileTouches = ramDbService.LoadFileTouchRecordsForRun(workspaceRoot, runState.RunStateId, 4000);
        var skipRecords = ramDbService.LoadTaskboardSkipRecordsForRun(workspaceRoot, runState.RunStateId, 4000);
        var rollups = BuildFileTouchRollups(fileTouches);
        var skipRollups = BuildSkipReasonRollups(skipRecords);
        var normalized = BuildNormalizedRunRecord(activeImport, runState, summary, artifacts, rollups, skipRecords, skipRollups);
        var normalizedArtifact = _artifactStore.SaveNormalizedRunArtifact(ramDbService, workspaceRoot, normalized);
        var indexExport = BuildIndexExport(normalized);
        var indexArtifact = _artifactStore.SaveIndexExportArtifact(ramDbService, workspaceRoot, indexExport);
        var corpusExport = BuildCorpusExport(normalized);
        var corpusArtifact = _artifactStore.SaveCorpusExportArtifact(ramDbService, workspaceRoot, corpusExport);

        summary.NormalizedRunRecordId = normalized.RecordId;
        summary.NormalizedRunArtifactRelativePath = normalizedArtifact.RelativePath;
        summary.IndexExportArtifactRelativePath = indexArtifact.RelativePath;
        summary.CorpusExportArtifactRelativePath = corpusArtifact.RelativePath;
        summary.FileTouchCount = normalized.FileTouchCount;
        summary.RepeatedFileTouchCount = normalized.RepeatedFileTouchCount;
        summary.TouchedFileCount = normalized.TouchedFileCount;
        summary.TopRepeatedTouchPaths = normalized.FileTouchRollups
            .Where(current => current.RepeatedTouchCount > 0)
            .OrderByDescending(current => current.RepeatedTouchCount)
            .ThenBy(current => current.FilePath, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(current => current.FilePath)
            .ToList();
        summary.PreferredCompletionProofTemplateId = normalized.PreferredCompletionProofTemplateId;
        summary.PreferredCompletionProofToolNames = [.. normalized.PreferredCompletionProofToolNames];
        summary.PreferredCompletionProofReason = normalized.PreferredCompletionProofReason;
        summary.PreferredCompletionProofProfile = normalized.PreferredCompletionProofProfile;
        summary.PreferredCompletionProofQuality = normalized.PreferredCompletionProofQuality;
        summary.PreferredCompletionProofStrength = normalized.PreferredCompletionProofStrength;
        summary.PreferredCompletionProofStrongerBehaviorMissing = normalized.PreferredCompletionProofStrongerBehaviorMissing;
        summary.SatisfactionSkipCount = normalized.SatisfactionSkipCount;
        summary.RepeatedFileTouchesAvoidedCount = normalized.RepeatedFileTouchesAvoidedCount;
        summary.LastSatisfactionSkipWorkItemTitle = normalized.LastSatisfactionSkipWorkItemTitle;
        summary.LastSatisfactionSkipReasonCode = normalized.LastSatisfactionSkipReasonCode;
        summary.LastVerificationWarningCount = normalized.LastVerificationWarningCount;
        summary.LastVerificationWarningCodes = [.. normalized.LastVerificationWarningCodes];
        summary.WarningPolicyMode = normalized.WarningPolicyMode;
        summary.RepairMutationObserved = normalized.RepairMutationObserved;
        summary.RepairMutationToolName = normalized.RepairMutationToolName;
        summary.RepairMutationUtc = normalized.RepairMutationUtc;
        summary.RepairMutatedFiles = [.. normalized.RepairMutatedFiles];
        summary.VerificationAfterMutationOutcome = normalized.VerificationAfterMutationOutcome;
        summary.VerificationAfterMutationUtc = normalized.VerificationAfterMutationUtc;
        summary.PatchMutationFamily = normalized.PatchMutationFamily;
        summary.PatchAllowedEditScope = normalized.PatchAllowedEditScope;
        summary.PatchTargetFiles = [.. normalized.PatchTargetFiles];
        summary.PatchContractArtifactRelativePath = normalized.PatchContractArtifactRelativePath;
        summary.PatchPlanArtifactRelativePath = normalized.PatchPlanArtifactRelativePath;
        summary.RetrievalBackend = normalized.RetrievalBackend;
        summary.RetrievalEmbedderModel = normalized.RetrievalEmbedderModel;
        summary.RetrievalQueryKind = normalized.RetrievalQueryKind;
        summary.RetrievalHitCount = normalized.RetrievalHitCount;
        summary.RetrievalSourceKinds = [.. normalized.RetrievalSourceKinds];
        summary.RetrievalSourcePaths = [.. normalized.RetrievalSourcePaths];
        summary.RetrievalQueryArtifactRelativePath = normalized.RetrievalQueryArtifactRelativePath;
        summary.RetrievalResultArtifactRelativePath = normalized.RetrievalResultArtifactRelativePath;
        summary.RetrievalContextPacketArtifactRelativePath = normalized.RetrievalContextPacketArtifactRelativePath;
        summary.RetrievalIndexBatchArtifactRelativePath = normalized.RetrievalIndexBatchArtifactRelativePath;
        summary.TopSatisfactionSkipReasonCodes = normalized.SatisfactionSkipReasonRollups
            .OrderByDescending(current => current.Count)
            .ThenBy(current => current.ReasonCode, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(current => $"{current.ReasonCode}:{current.Count}")
            .ToList();
    }

    public List<RamFileTouchRollupRecord> BuildFileTouchRollups(IReadOnlyList<RamFileTouchRecord> touches)
    {
        return touches
            .GroupBy(current => NormalizePath(current.FilePath), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
            {
                var ordered = group.OrderBy(current => current.TouchOrderIndex).ThenBy(current => current.Id).ToList();
                return new RamFileTouchRollupRecord
                {
                    FilePath = group.Key,
                    TouchCount = ordered.Count,
                    RepeatedTouchCount = Math.Max(ordered.Count - 1, 0),
                    ProductiveTouchCount = ordered.Count(current => current.IsProductiveTouch),
                    NoOpTouchCount = ordered.Count(current => !current.ContentChanged),
                    ReasonCounts = ordered
                        .GroupBy(current => FirstNonEmpty(current.Reason, "(none)"), StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(current => current.Count())
                        .ThenBy(current => current.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(current => $"{current.Key}:{current.Count()}")
                        .ToList(),
                    OperationCounts = ordered
                        .GroupBy(current => FirstNonEmpty(current.OperationType, "(none)"), StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(current => current.Count())
                        .ThenBy(current => current.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(current => $"{current.Key}:{current.Count()}")
                        .ToList(),
                    FirstTouchedUtc = ordered.FirstOrDefault()?.CreatedUtc ?? "",
                    LastTouchedUtc = ordered.LastOrDefault()?.CreatedUtc ?? ""
                };
            })
            .OrderByDescending(current => current.TouchCount)
            .ThenBy(current => current.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<TaskboardSkipReasonRollupRecord> BuildSkipReasonRollups(IReadOnlyList<TaskboardSkipDecisionRecord> skipRecords)
    {
        return skipRecords
            .GroupBy(current => FirstNonEmpty(current.ReasonCode, "(none)"), StringComparer.OrdinalIgnoreCase)
            .Select(group => new TaskboardSkipReasonRollupRecord
            {
                ReasonCode = group.Key,
                Count = group.Count(),
                RepeatedTouchesAvoidedCount = group.Sum(current => current.RepeatedTouchesAvoidedCount),
                EvidenceSources = group
                    .Select(current => FirstNonEmpty(current.EvidenceSource, "(none)"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(current => current, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .OrderByDescending(current => current.Count)
            .ThenBy(current => current.ReasonCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private TaskboardNormalizedRunRecord BuildNormalizedRunRecord(
        TaskboardImportRecord activeImport,
        TaskboardPlanRunStateRecord runState,
        TaskboardRunTerminalSummaryRecord summary,
        IReadOnlyList<ArtifactRecord> artifacts,
        IReadOnlyList<RamFileTouchRollupRecord> rollups,
        IReadOnlyList<TaskboardSkipDecisionRecord> skipRecords,
        IReadOnlyList<TaskboardSkipReasonRollupRecord> skipRollups)
    {
        var terminalWorkItem = FindWorkItem(runState, summary.TerminalWorkItemId);
        var stackFamily = FirstNonEmpty(
            runState.LastBlockerStackFamily,
            terminalWorkItem?.TargetStack,
            runState.LastCompletedStackFamily,
            NormalizeStackFamily(runState.LastResolvedBuildProfile.StackFamily));
        var selectedLaneKind = FirstNonEmpty(
            summary.BlockerLaneKind,
            NormalizeLaneKind(runState.LastExecutionGoalResolution?.LaneResolution?.LaneKind ?? TaskboardExecutionLaneKind.Unknown));
        var selectedChain = FirstNonEmpty(
            runState.LastExecutionGoalResolution?.Goal.SelectedChainTemplateId,
            runState.LastExecutionGoalResolution?.LaneResolution?.SelectedChainTemplateId,
            summary.LastSuccessfulChainTemplateId);

        var recordId = Guid.NewGuid().ToString("N");
        return new TaskboardNormalizedRunRecord
        {
            RecordId = recordId,
            RecordArtifactRelativePath = TaskboardArtifactStore.BuildNormalizedRunPath(activeImport.ImportId, recordId),
            RunStateId = runState.RunStateId,
            SummaryId = summary.SummaryId,
            WorkspaceRoot = summary.WorkspaceRoot,
            PlanImportId = activeImport.ImportId,
            PlanTitle = activeImport.Title,
            ActionName = summary.ActionName,
            FinalStatus = summary.FinalStatus,
            TerminalCategory = summary.TerminalCategory,
            StartedUtc = summary.StartedUtc,
            EndedUtc = summary.EndedUtc,
            CompletedBatchCount = summary.CompletedBatchCount,
            TotalBatchCount = summary.TotalBatchCount,
            CompletedWorkItemCount = summary.CompletedWorkItemCount,
            TotalWorkItemCount = summary.TotalWorkItemCount,
            TerminalBatchId = summary.TerminalBatchId,
            TerminalBatchTitle = summary.TerminalBatchTitle,
            TerminalWorkItemId = summary.TerminalWorkItemId,
            TerminalWorkItemTitle = summary.TerminalWorkItemTitle,
            WorkFamily = FirstNonEmpty(summary.BlockerWorkFamily, terminalWorkItem?.WorkFamily, runState.LastCompletedWorkFamily, runState.LastWorkFamily),
            PhraseFamily = FirstNonEmpty(summary.BlockerPhraseFamily, terminalWorkItem?.PhraseFamily, runState.LastCompletedPhraseFamily, runState.LastPhraseFamily),
            OperationKind = FirstNonEmpty(summary.BlockerOperationKind, terminalWorkItem?.OperationKind, runState.LastCompletedOperationKind),
            StackFamily = stackFamily,
            SelectedLaneKind = selectedLaneKind,
            SelectedChainTemplateId = selectedChain,
            LastSuccessfulChainTemplateId = summary.LastSuccessfulChainTemplateId,
            LastSuccessfulToolNames = [.. summary.LastSuccessfulToolNames],
            PreferredCompletionProofTemplateId = summary.PreferredCompletionProofTemplateId,
            PreferredCompletionProofToolNames = [.. summary.PreferredCompletionProofToolNames],
            PreferredCompletionProofReason = summary.PreferredCompletionProofReason,
            PreferredCompletionProofProfile = summary.PreferredCompletionProofProfile,
            PreferredCompletionProofQuality = summary.PreferredCompletionProofQuality,
            PreferredCompletionProofStrength = summary.PreferredCompletionProofStrength,
            PreferredCompletionProofStrongerBehaviorMissing = summary.PreferredCompletionProofStrongerBehaviorMissing,
            BlockerWorkFamily = summary.BlockerWorkFamily,
            BlockerPhraseFamily = summary.BlockerPhraseFamily,
            BlockerOperationKind = summary.BlockerOperationKind,
            BlockerLaneKind = summary.BlockerLaneKind,
            BlockerReason = summary.BlockerReason,
            LastVerificationOutcome = summary.LastVerificationOutcome,
            LastVerificationTarget = summary.LastVerificationTarget,
            LastVerificationWarningCount = summary.LastVerificationWarningCount,
            LastVerificationWarningCodes = [.. summary.LastVerificationWarningCodes],
            WarningPolicyMode = summary.WarningPolicyMode,
            RepairAttemptSummary = summary.RepairAttemptSummary,
            RepairMutationObserved = summary.RepairMutationObserved,
            RepairMutationToolName = summary.RepairMutationToolName,
            RepairMutationUtc = summary.RepairMutationUtc,
            RepairMutatedFiles = [.. summary.RepairMutatedFiles],
            VerificationAfterMutationOutcome = summary.VerificationAfterMutationOutcome,
            VerificationAfterMutationUtc = summary.VerificationAfterMutationUtc,
            MaintenanceBaselineSolutionPath = summary.MaintenanceBaselineSolutionPath,
            MaintenanceAllowedRoots = [.. summary.MaintenanceAllowedRoots],
            MaintenanceExcludedRoots = [.. summary.MaintenanceExcludedRoots],
            MaintenanceGuardSummary = summary.MaintenanceGuardSummary,
            LastHeadingPolicyNormalizedTitle = summary.LastHeadingPolicyNormalizedTitle,
            LastHeadingPolicyClass = summary.LastHeadingPolicyClass,
            LastHeadingPolicyTreatment = summary.LastHeadingPolicyTreatment,
            LastHeadingPolicyReasonCode = summary.LastHeadingPolicyReasonCode,
            LastHeadingPolicySummary = summary.LastHeadingPolicySummary,
            PatchMutationFamily = summary.PatchMutationFamily,
            PatchAllowedEditScope = summary.PatchAllowedEditScope,
            PatchTargetFiles = [.. summary.PatchTargetFiles],
            PatchContractArtifactRelativePath = summary.PatchContractArtifactRelativePath,
            PatchPlanArtifactRelativePath = summary.PatchPlanArtifactRelativePath,
            RetrievalBackend = summary.RetrievalBackend,
            RetrievalEmbedderModel = summary.RetrievalEmbedderModel,
            RetrievalQueryKind = summary.RetrievalQueryKind,
            RetrievalHitCount = summary.RetrievalHitCount,
            RetrievalSourceKinds = [.. summary.RetrievalSourceKinds],
            RetrievalSourcePaths = [.. summary.RetrievalSourcePaths],
            RetrievalQueryArtifactRelativePath = summary.RetrievalQueryArtifactRelativePath,
            RetrievalResultArtifactRelativePath = summary.RetrievalResultArtifactRelativePath,
            RetrievalContextPacketArtifactRelativePath = summary.RetrievalContextPacketArtifactRelativePath,
            RetrievalIndexBatchArtifactRelativePath = summary.RetrievalIndexBatchArtifactRelativePath,
            ChangedFileCount = summary.ChangedFileCount,
            KeyChangedPaths = [.. summary.KeyChangedPaths],
            FileTouchCount = rollups.Sum(current => current.TouchCount),
            RepeatedFileTouchCount = rollups.Sum(current => current.RepeatedTouchCount),
            TouchedFileCount = rollups.Count,
            FileTouchRollups = rollups.Take(12).ToList(),
            SatisfactionSkipCount = skipRecords.Count,
            RepeatedFileTouchesAvoidedCount = skipRecords.Sum(current => current.RepeatedTouchesAvoidedCount),
            LastSatisfactionSkipWorkItemTitle = FirstNonEmpty(skipRecords.LastOrDefault()?.WorkItemTitle, runState.LastSatisfactionSkipWorkItemTitle),
            LastSatisfactionSkipReasonCode = FirstNonEmpty(skipRecords.LastOrDefault()?.ReasonCode, runState.LastSatisfactionSkipReasonCode),
            SatisfactionSkipReasonRollups = skipRollups.Take(8).ToList(),
            ArtifactReferences = BuildArtifactReferences(artifacts),
            TerminalNote = summary.TerminalNote,
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private RamIndexExportRecord BuildIndexExport(TaskboardNormalizedRunRecord normalized)
    {
        var exportId = Guid.NewGuid().ToString("N");
        var recency = ResolveRecency(normalized.CreatedUtc);
        return new RamIndexExportRecord
        {
            ExportId = exportId,
            ArtifactRelativePath = TaskboardArtifactStore.BuildIndexExportPath(normalized.PlanImportId, exportId),
            WorkspaceRoot = normalized.WorkspaceRoot,
            PlanImportId = normalized.PlanImportId,
            RunStateId = normalized.RunStateId,
            FinalStatus = normalized.FinalStatus,
            TerminalCategory = normalized.TerminalCategory,
            Documents =
            [
                new RamIndexDocumentRecord
                {
                    DocumentId = $"{exportId}:summary",
                    SourceKind = "run_summary",
                    TrustLabel = "current_truth",
                    RecencyLabel = recency,
                    Title = $"Run summary: {normalized.PlanTitle}",
                    Text = $"{normalized.FinalStatus} {normalized.TerminalCategory} progress={normalized.CompletedWorkItemCount}/{normalized.TotalWorkItemCount} terminal={FirstNonEmpty(normalized.TerminalBatchTitle, "(none)")} / {FirstNonEmpty(normalized.TerminalWorkItemTitle, "(none)")} note={FirstNonEmpty(normalized.TerminalNote, "(none)")}",
                    SourcePath = normalized.RecordArtifactRelativePath,
                    ArtifactType = "taskboard_run_summary",
                    RunStateId = normalized.RunStateId
                },
                new RamIndexDocumentRecord
                {
                    DocumentId = $"{exportId}:normalized_run",
                    SourceKind = "normalized_run_record",
                    TrustLabel = "current_truth",
                    RecencyLabel = recency,
                    Title = $"Normalized run: {normalized.PlanTitle}",
                    Text = $"family={FirstNonEmpty(normalized.WorkFamily, "(none)")} phrase={FirstNonEmpty(normalized.PhraseFamily, "(none)")} operation={FirstNonEmpty(normalized.OperationKind, "(none)")} stack={FirstNonEmpty(normalized.StackFamily, "(none)")} lane={FirstNonEmpty(normalized.SelectedLaneKind, "(none)")} chain={FirstNonEmpty(normalized.SelectedChainTemplateId, "(none)")} completion_proof={FirstNonEmpty(normalized.PreferredCompletionProofTemplateId, "(none)")} completion_profile={FirstNonEmpty(normalized.PreferredCompletionProofProfile, "(none)")} completion_quality={FirstNonEmpty(normalized.PreferredCompletionProofQuality, "(none)")} completion_strength={FirstNonEmpty(normalized.PreferredCompletionProofStrength, "(none)")} stronger_behavior_proof_missing={normalized.PreferredCompletionProofStrongerBehaviorMissing.ToString().ToLowerInvariant()} completion_reason={FirstNonEmpty(normalized.PreferredCompletionProofReason, "(none)")}",
                    SourcePath = normalized.RecordArtifactRelativePath,
                    ArtifactType = "taskboard_normalized_run",
                    RunStateId = normalized.RunStateId
                },
                new RamIndexDocumentRecord
                {
                    DocumentId = $"{exportId}:verification",
                    SourceKind = "verification_outcome",
                    TrustLabel = "current_truth",
                    RecencyLabel = recency,
                    Title = $"Verification outcome: {normalized.PlanTitle}",
                    Text = $"verification={FirstNonEmpty(normalized.LastVerificationOutcome, "(none)")} target={FirstNonEmpty(normalized.LastVerificationTarget, "(none)")} warnings={normalized.LastVerificationWarningCount} warning_policy={FirstNonEmpty(normalized.WarningPolicyMode, "(none)")} repair={FirstNonEmpty(normalized.RepairAttemptSummary, "(none)")} mutation={normalized.RepairMutationObserved.ToString().ToLowerInvariant()} mutation_tool={FirstNonEmpty(normalized.RepairMutationToolName, "(none)")} mutation_files={FormatList(normalized.RepairMutatedFiles)} baseline={FirstNonEmpty(normalized.MaintenanceBaselineSolutionPath, "(none)")} guard={FirstNonEmpty(normalized.MaintenanceGuardSummary, "(none)")}",
                    SourcePath = normalized.RecordArtifactRelativePath,
                    ArtifactType = "verification_result",
                    RunStateId = normalized.RunStateId
                },
                new RamIndexDocumentRecord
                {
                    DocumentId = $"{exportId}:patch_contract",
                    SourceKind = "repair_context",
                    TrustLabel = "current_truth",
                    RecencyLabel = recency,
                    Title = $"C# patch foundation: {normalized.PlanTitle}",
                    Text = $"patch_family={FirstNonEmpty(normalized.PatchMutationFamily, "(none)")} scope={FirstNonEmpty(normalized.PatchAllowedEditScope, "(none)")} targets={FirstNonEmpty(normalized.PatchTargetFiles.Count == 0 ? "" : string.Join(",", normalized.PatchTargetFiles), "(none)")} contract={FirstNonEmpty(normalized.PatchContractArtifactRelativePath, "(none)")} plan={FirstNonEmpty(normalized.PatchPlanArtifactRelativePath, "(none)")}",
                    SourcePath = normalized.RecordArtifactRelativePath,
                    ArtifactType = "csharp_patch_contract",
                    RunStateId = normalized.RunStateId
                },
                new RamIndexDocumentRecord
                {
                    DocumentId = $"{exportId}:state_satisfaction",
                    SourceKind = "state_satisfaction_record",
                    TrustLabel = "current_truth",
                    RecencyLabel = recency,
                    Title = $"State satisfaction: {normalized.PlanTitle}",
                    Text = $"skips={normalized.SatisfactionSkipCount} repeated_touches_avoided={normalized.RepeatedFileTouchesAvoidedCount} reasons={FormatSkipReasonRollups(normalized.SatisfactionSkipReasonRollups)}",
                    SourcePath = normalized.RecordArtifactRelativePath,
                    ArtifactType = "taskboard_skip_record",
                    RunStateId = normalized.RunStateId
                },
                new RamIndexDocumentRecord
                {
                    DocumentId = $"{exportId}:retrieval_context",
                    SourceKind = "retrieval_context",
                    TrustLabel = "current_truth",
                    RecencyLabel = recency,
                    Title = $"Retrieval context: {normalized.PlanTitle}",
                    Text = $"backend={FirstNonEmpty(normalized.RetrievalBackend, "(none)")} embedder={FirstNonEmpty(normalized.RetrievalEmbedderModel, "(none)")} query={FirstNonEmpty(normalized.RetrievalQueryKind, "(none)")} hits={normalized.RetrievalHitCount} sources={FormatList(normalized.RetrievalSourceKinds)} context={FirstNonEmpty(normalized.RetrievalContextPacketArtifactRelativePath, "(none)")}",
                    SourcePath = normalized.RecordArtifactRelativePath,
                    ArtifactType = "coder_context_packet",
                    RunStateId = normalized.RunStateId
                }
            ],
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private RamCorpusExportRecord BuildCorpusExport(TaskboardNormalizedRunRecord normalized)
    {
        var exportId = Guid.NewGuid().ToString("N");
        var toolsText = normalized.PreferredCompletionProofToolNames.Count == 0
            ? "(none)"
            : string.Join(",", normalized.PreferredCompletionProofToolNames);
        return new RamCorpusExportRecord
        {
            ExportId = exportId,
            ArtifactRelativePath = TaskboardArtifactStore.BuildCorpusExportPath(normalized.PlanImportId, exportId),
            WorkspaceRoot = normalized.WorkspaceRoot,
            PlanImportId = normalized.PlanImportId,
            RunStateId = normalized.RunStateId,
            FinalStatus = normalized.FinalStatus,
            TerminalCategory = normalized.TerminalCategory,
            Records =
            [
                new RamCorpusReadyRecord
                {
                    RecordId = Guid.NewGuid().ToString("N"),
                    InputProblem = $"Taskboard `{normalized.PlanTitle}` run targeting {FirstNonEmpty(normalized.TerminalWorkItemTitle, normalized.TerminalBatchTitle, "(terminal state)")}.",
                    NormalizedState = $"status={normalized.FinalStatus} category={normalized.TerminalCategory} family={FirstNonEmpty(normalized.WorkFamily, "(none)")} phrase={FirstNonEmpty(normalized.PhraseFamily, "(none)")} operation={FirstNonEmpty(normalized.OperationKind, "(none)")} stack={FirstNonEmpty(normalized.StackFamily, "(none)")} lane={FirstNonEmpty(normalized.SelectedLaneKind, "(none)")}",
                    ActionSequence =
                    [
                        $"chain:{FirstNonEmpty(normalized.SelectedChainTemplateId, normalized.PreferredCompletionProofTemplateId, normalized.LastSuccessfulChainTemplateId, "(none)")}",
                        $"tools:{toolsText}",
                        $"completion_proof:{FirstNonEmpty(normalized.PreferredCompletionProofTemplateId, "(none)")}",
                        $"completion_profile:{FirstNonEmpty(normalized.PreferredCompletionProofProfile, "(none)")}",
                        $"completion_quality:{FirstNonEmpty(normalized.PreferredCompletionProofQuality, "(none)")}",
                        $"completion_strength:{FirstNonEmpty(normalized.PreferredCompletionProofStrength, "(none)")}",
                        $"stronger_behavior_proof_missing:{normalized.PreferredCompletionProofStrongerBehaviorMissing.ToString().ToLowerInvariant()}",
                        $"completion_reason:{FirstNonEmpty(normalized.PreferredCompletionProofReason, "(none)")}",
                        $"patch_family:{FirstNonEmpty(normalized.PatchMutationFamily, "(none)")}",
                        $"patch_scope:{FirstNonEmpty(normalized.PatchAllowedEditScope, "(none)")}",
                        $"retrieval_backend:{FirstNonEmpty(normalized.RetrievalBackend, "(none)")}",
                        $"retrieval_query:{FirstNonEmpty(normalized.RetrievalQueryKind, "(none)")}",
                        $"retrieval_hits:{normalized.RetrievalHitCount}",
                        $"retrieval_sources:{FormatList(normalized.RetrievalSourceKinds)}",
                        $"baseline_solution:{FirstNonEmpty(normalized.MaintenanceBaselineSolutionPath, "(none)")}",
                        $"maintenance_guard:{FirstNonEmpty(normalized.MaintenanceGuardSummary, "(none)")}",
                        $"warning_count:{normalized.LastVerificationWarningCount}",
                        $"mutation_observed:{normalized.RepairMutationObserved.ToString().ToLowerInvariant()}",
                        $"mutation_tool:{FirstNonEmpty(normalized.RepairMutationToolName, "(none)")}",
                        $"mutation_files:{FormatList(normalized.RepairMutatedFiles)}",
                        $"verification_after_mutation:{FirstNonEmpty(normalized.VerificationAfterMutationOutcome, "(none)")}",
                        $"touches:{normalized.FileTouchCount}",
                        $"skips:{normalized.SatisfactionSkipCount}",
                        $"skip_reasons:{FormatSkipReasonRollups(normalized.SatisfactionSkipReasonRollups)}"
                    ],
                    Outcome = $"verification={FirstNonEmpty(normalized.LastVerificationOutcome, "(none)")} blocker={FirstNonEmpty(normalized.BlockerReason, "(none)")}",
                    ValidatorResult = FirstNonEmpty(normalized.LastVerificationOutcome, normalized.FinalStatus),
                    TerminalTruth = FirstNonEmpty(normalized.TerminalNote, normalized.BlockerReason, normalized.FinalStatus),
                    ArtifactReferencePaths = normalized.ArtifactReferences.Select(current => current.RelativePath).Take(12).ToList()
                }
            ],
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private List<TaskboardNormalizedArtifactReferenceRecord> BuildArtifactReferences(IReadOnlyList<ArtifactRecord> artifacts)
    {
        return artifacts
            .OrderByDescending(current => ParseUtc(current.UpdatedUtc))
            .ThenByDescending(current => current.Id)
            .Select(current =>
            {
                var dataCategory = FirstNonEmpty(current.DataCategory, _dataDisciplineService.ResolveArtifactDataCategory(current));
                var retentionClass = FirstNonEmpty(current.RetentionClass, _dataDisciplineService.ResolveArtifactRetentionClass(current));
                var lifecycleState = FirstNonEmpty(current.LifecycleState, _dataDisciplineService.ResolveArtifactLifecycleState(current));
                return new TaskboardNormalizedArtifactReferenceRecord
                {
                    ArtifactId = current.Id,
                    ArtifactType = current.ArtifactType,
                    Title = current.Title,
                    RelativePath = current.RelativePath,
                    Summary = BuildReferenceSafeSummary(current, dataCategory),
                    DataCategory = dataCategory,
                    RetentionClass = retentionClass,
                    LifecycleState = lifecycleState,
                    ContentSha256 = current.ContentSha256,
                    ContentLengthBytes = current.ContentLengthBytes,
                    UpdatedUtc = current.UpdatedUtc
                };
            })
            .Where(current => !string.Equals(current.DataCategory, "raw_transient_log", StringComparison.OrdinalIgnoreCase))
            .Take(16)
            .ToList();
    }

    private static string BuildReferenceSafeSummary(ArtifactRecord artifact, string dataCategory)
    {
        if (string.Equals(dataCategory, "artifact_reference", StringComparison.OrdinalIgnoreCase))
            return $"artifact_type={FirstNonEmpty(artifact.ArtifactType, "(none)")} path={FirstNonEmpty(artifact.RelativePath, "(none)")}";

        return artifact.Summary;
    }

    private static TaskboardWorkItemRunStateRecord? FindWorkItem(TaskboardPlanRunStateRecord runState, string workItemId)
    {
        if (string.IsNullOrWhiteSpace(workItemId))
            return null;

        foreach (var batch in runState.Batches)
        {
            var match = batch.WorkItems.FirstOrDefault(current =>
                string.Equals(current.WorkItemId, workItemId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return null;
    }

    private static string NormalizeStackFamily(TaskboardStackFamily stackFamily)
    {
        return stackFamily switch
        {
            TaskboardStackFamily.DotnetDesktop => "dotnet_desktop",
            TaskboardStackFamily.NativeCppDesktop => "native_cpp_desktop",
            TaskboardStackFamily.WebApp => "web_app",
            TaskboardStackFamily.RustApp => "rust_app",
            _ => ""
        };
    }

    private static string NormalizeLaneKind(TaskboardExecutionLaneKind laneKind)
    {
        return laneKind switch
        {
            TaskboardExecutionLaneKind.ToolLane => "tool",
            TaskboardExecutionLaneKind.ChainLane => "chain",
            TaskboardExecutionLaneKind.ManualOnlyLane => "manual_only",
            TaskboardExecutionLaneKind.BlockedLane => "blocked",
            _ => ""
        };
    }

    private static string FormatSkipReasonRollups(IReadOnlyList<TaskboardSkipReasonRollupRecord> rollups)
    {
        if (rollups.Count == 0)
            return "(none)";

        return string.Join(
            "|",
            rollups
                .Take(5)
                .Select(current => $"{FirstNonEmpty(current.ReasonCode, "(none)")}:{current.Count}"));
    }

    private static string FormatList(IReadOnlyList<string> values)
    {
        return values.Count == 0
            ? "(none)"
            : string.Join(", ", values);
    }

    private static DateTime ParseUtc(string? value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.MinValue;
    }

    private string ResolveRecency(string? createdUtc)
    {
        var created = ParseUtc(createdUtc);
        return _dataDisciplineService.ResolveRecencyLabel(created == DateTime.MinValue ? DateTime.UtcNow : created);
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
