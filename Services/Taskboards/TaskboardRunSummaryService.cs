using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardRunSummaryService
{
    public TaskboardRunTerminalSummaryRecord Build(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardPlanRunStateRecord runState,
        RamDbService ramDbService,
        string actionName,
        string selectedBatchId,
        string terminalStatusCode)
    {
        var startedUtc = FirstNonEmpty(runState.StartedUtc, runState.UpdatedUtc, DateTime.UtcNow.ToString("O"));
        var endedUtc = FirstNonEmpty(runState.UpdatedUtc, DateTime.UtcNow.ToString("O"));
        var artifacts = ramDbService.LoadArtifactsSince(workspaceRoot, startedUtc, 600);
        var finalStatus = NormalizeFinalStatus(runState, terminalStatusCode, selectedBatchId);
        var terminalCategory = ResolveTerminalCategory(runState, finalStatus, artifacts);
        var terminalBatch = ResolveTerminalBatch(runState, finalStatus, selectedBatchId);
        var terminalWorkItem = ResolveTerminalWorkItem(runState, terminalBatch, finalStatus);
        var successfulChains = artifacts
            .Where(artifact => string.Equals(artifact.ArtifactType, "tool_chain_record", StringComparison.OrdinalIgnoreCase))
            .Select(artifact => new CompletionProofSourceRecord
            {
                Artifact = artifact,
                Record = TryDeserialize<ToolChainRecord>(artifact.Content)
            })
            .Where(item => item.Record is not null
                && item.Record.CurrentStatus == ToolChainStatus.Completed
                && item.Record.StopReason == ToolChainStopReason.GoalCompleted)
            .OrderByDescending(item => ParseUtc(FirstNonEmpty(item.Record!.CompletedUtc, item.Artifact.UpdatedUtc, item.Artifact.CreatedUtc)))
            .ToList();
        var lastSuccessfulChainArtifact = successfulChains.FirstOrDefault();
        var lastSuccessfulChain = lastSuccessfulChainArtifact?.Record;
        var preferredCompletionProof = SelectPreferredCompletionProof(successfulChains, runState);
        var proofInsufficiencyReasons = BuildProofInsufficiencyReasons(runState);
        var preferredCompletionProofReason = AppendProofInsufficiencyReasonNote(
            preferredCompletionProof.Reason,
            proofInsufficiencyReasons,
            runState);

        var lastVerificationArtifact = artifacts
            .Where(artifact =>
                string.Equals(artifact.ArtifactType, "verification_result", StringComparison.OrdinalIgnoreCase)
                || string.Equals(artifact.ArtifactType, "auto_validation_result", StringComparison.OrdinalIgnoreCase)
                || string.Equals(artifact.ArtifactType, "build_result", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(artifact => ParseUtc(FirstNonEmpty(artifact.UpdatedUtc, artifact.CreatedUtc)))
            .FirstOrDefault();
        var verificationOutcome = BuildVerificationOutcome(lastVerificationArtifact, runState);
        var patchFoundation = BuildPatchFoundationSnapshot(artifacts);
        var retrievalSnapshot = BuildRetrievalSnapshot(artifacts);
        var repairAttemptSummary = BuildRepairAttemptSummary(artifacts);
        var changedPaths = CollectChangedPaths(artifacts);
        var blockerLaneKind = ResolveBlockerLaneKind(runState);
        var terminalNote = BuildTerminalNote(runState, finalStatus, terminalCategory, terminalBatch, terminalWorkItem, verificationOutcome.summary, repairAttemptSummary);
        var started = ParseUtc(startedUtc);
        var ended = ParseUtc(endedUtc);
        var durationSeconds = ended > started
            ? Math.Round((ended - started).TotalSeconds, 3)
            : 0d;
        var summaryId = Guid.NewGuid().ToString("N");

        return new TaskboardRunTerminalSummaryRecord
        {
            SummaryId = summaryId,
            SummaryArtifactRelativePath = TaskboardArtifactStore.BuildRunSummaryPath(activeImport.ImportId, summaryId),
            TerminalStatusCode = terminalStatusCode,
            RunStateId = runState.RunStateId,
            WorkspaceRoot = workspaceRoot,
            PlanImportId = activeImport.ImportId,
            PlanTitle = activeImport.Title,
            ActionName = actionName,
            RunScope = string.IsNullOrWhiteSpace(selectedBatchId) ? "active_plan" : "selected_batch",
            StartedUtc = startedUtc,
            EndedUtc = endedUtc,
            DurationSeconds = durationSeconds,
            FinalStatus = finalStatus,
            TerminalCategory = terminalCategory,
            CompletedBatchCount = runState.Batches.Count(batch => batch.Status is TaskboardBatchRuntimeStatus.Completed or TaskboardBatchRuntimeStatus.Skipped),
            TotalBatchCount = runState.Batches.Count,
            CompletedWorkItemCount = runState.CompletedWorkItemCount,
            TotalWorkItemCount = runState.TotalWorkItemCount,
            TerminalBatchId = terminalBatch?.BatchId ?? "",
            TerminalBatchTitle = terminalBatch?.Title ?? "",
            TerminalWorkItemId = terminalWorkItem?.WorkItemId ?? "",
            TerminalWorkItemTitle = terminalWorkItem?.Title ?? "",
            LastSuccessfulChainTemplateId = FirstNonEmpty(lastSuccessfulChain?.SelectedTemplateName),
            LastSuccessfulToolNames = lastSuccessfulChain?.Steps
                .Where(step => step.ExecutionAttempted && !string.IsNullOrWhiteSpace(step.ToolName))
                .Select(step => step.ToolName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [],
            PreferredCompletionProofTemplateId = preferredCompletionProof.TemplateId,
            PreferredCompletionProofToolNames = preferredCompletionProof.ToolNames,
            PreferredCompletionProofReason = preferredCompletionProofReason,
            PreferredCompletionProofProfile = preferredCompletionProof.Profile,
            PreferredCompletionProofQuality = preferredCompletionProof.Quality,
            PreferredCompletionProofStrength = preferredCompletionProof.Strength,
            PreferredCompletionProofStrongerBehaviorMissing = preferredCompletionProof.StrongerBehaviorMissing,
            PreferredCompletionProofInsufficiencyReasons = proofInsufficiencyReasons,
            ChangedFileCount = changedPaths.Count,
            KeyChangedPaths = changedPaths.Take(8).ToList(),
            BlockerWorkFamily = runState.LastBlockerWorkFamily,
            BlockerPhraseFamily = runState.LastBlockerPhraseFamily,
            BlockerOperationKind = runState.LastBlockerOperationKind,
            BlockerLaneKind = blockerLaneKind,
            BlockerReason = FirstNonEmpty(runState.LastBlockerReason, runState.LastResultSummary),
            LastVerificationOutcome = verificationOutcome.summary,
            LastVerificationTarget = verificationOutcome.target,
            LastVerificationWarningCount = verificationOutcome.warningCount,
            LastVerificationWarningCodes = verificationOutcome.warningCodes,
            WarningPolicyMode = verificationOutcome.warningPolicyMode,
            RepairAttemptSummary = repairAttemptSummary,
            RepairMutationObserved = !string.IsNullOrWhiteSpace(runState.LastMutationToolName),
            RepairMutationToolName = runState.LastMutationToolName,
            RepairMutationUtc = runState.LastMutationUtc,
            RepairMutatedFiles = [.. runState.LastMutationTouchedFilePaths],
            VerificationAfterMutationOutcome = runState.LastVerificationAfterMutationOutcome,
            VerificationAfterMutationUtc = runState.LastVerificationAfterMutationUtc,
            MaintenanceBaselineSolutionPath = runState.LastMaintenanceBaselineSolutionPath,
            MaintenanceAllowedRoots = [.. runState.LastMaintenanceAllowedRoots],
            MaintenanceExcludedRoots = [.. runState.LastMaintenanceExcludedRoots],
            MaintenanceGuardSummary = runState.LastMaintenanceGuardSummary,
            LastHeadingPolicyNormalizedTitle = runState.LastHeadingPolicyNormalizedTitle,
            LastHeadingPolicyClass = runState.LastHeadingPolicyClass,
            LastHeadingPolicyTreatment = runState.LastHeadingPolicyTreatment,
            LastHeadingPolicyReasonCode = runState.LastHeadingPolicyReasonCode,
            LastHeadingPolicySummary = runState.LastHeadingPolicySummary,
            PatchMutationFamily = patchFoundation.mutationFamily,
            PatchAllowedEditScope = patchFoundation.allowedEditScope,
            PatchTargetFiles = patchFoundation.targetFiles,
            PatchContractArtifactRelativePath = patchFoundation.contractArtifactPath,
            PatchPlanArtifactRelativePath = patchFoundation.planArtifactPath,
            RetrievalBackend = retrievalSnapshot.backend,
            RetrievalEmbedderModel = retrievalSnapshot.embedderModel,
            RetrievalQueryKind = retrievalSnapshot.queryKind,
            RetrievalHitCount = retrievalSnapshot.hitCount,
            RetrievalSourceKinds = retrievalSnapshot.sourceKinds,
            RetrievalSourcePaths = retrievalSnapshot.sourcePaths,
            RetrievalQueryArtifactRelativePath = retrievalSnapshot.queryArtifactPath,
            RetrievalResultArtifactRelativePath = retrievalSnapshot.resultArtifactPath,
            RetrievalContextPacketArtifactRelativePath = retrievalSnapshot.contextArtifactPath,
            RetrievalIndexBatchArtifactRelativePath = retrievalSnapshot.indexBatchArtifactPath,
            TerminalNote = terminalNote,
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    public string ComputeTerminalFingerprint(TaskboardRunTerminalSummaryRecord? summary)
    {
        if (summary is null)
            return "";

        var fingerprintSeed = string.Join("|", new[]
        {
            FirstNonEmpty(summary.RunStateId),
            FirstNonEmpty(summary.PlanImportId),
            FirstNonEmpty(summary.ActionName),
            FirstNonEmpty(summary.RunScope),
            FirstNonEmpty(summary.TerminalStatusCode),
            FirstNonEmpty(summary.FinalStatus),
            FirstNonEmpty(summary.TerminalCategory),
            summary.CompletedBatchCount.ToString(),
            summary.TotalBatchCount.ToString(),
            summary.CompletedWorkItemCount.ToString(),
            summary.TotalWorkItemCount.ToString(),
            FirstNonEmpty(summary.TerminalBatchId),
            FirstNonEmpty(summary.TerminalWorkItemId),
            FirstNonEmpty(summary.BlockerWorkFamily),
            FirstNonEmpty(summary.BlockerPhraseFamily),
            FirstNonEmpty(summary.BlockerOperationKind),
            FirstNonEmpty(summary.BlockerLaneKind),
            FirstNonEmpty(summary.BlockerReason),
            FirstNonEmpty(summary.PreferredCompletionProofTemplateId),
            string.Join(",", summary.PreferredCompletionProofToolNames),
            FirstNonEmpty(summary.PreferredCompletionProofReason),
            string.Join(",", summary.PreferredCompletionProofInsufficiencyReasons),
            FirstNonEmpty(summary.PreferredCompletionProofProfile),
            FirstNonEmpty(summary.PreferredCompletionProofQuality),
            FirstNonEmpty(summary.PreferredCompletionProofStrength),
            summary.PreferredCompletionProofStrongerBehaviorMissing.ToString().ToLowerInvariant(),
            FirstNonEmpty(summary.LastVerificationOutcome),
            FirstNonEmpty(summary.LastVerificationTarget),
            summary.LastVerificationWarningCount.ToString(),
            string.Join(",", summary.LastVerificationWarningCodes),
            FirstNonEmpty(summary.WarningPolicyMode),
            FirstNonEmpty(summary.RepairAttemptSummary),
            summary.RepairMutationObserved.ToString().ToLowerInvariant(),
            FirstNonEmpty(summary.RepairMutationToolName),
            FirstNonEmpty(summary.RepairMutationUtc),
            string.Join(",", summary.RepairMutatedFiles),
            FirstNonEmpty(summary.VerificationAfterMutationOutcome),
            FirstNonEmpty(summary.VerificationAfterMutationUtc),
            FirstNonEmpty(summary.MaintenanceBaselineSolutionPath),
            string.Join(",", summary.MaintenanceAllowedRoots),
            string.Join(",", summary.MaintenanceExcludedRoots),
            FirstNonEmpty(summary.MaintenanceGuardSummary),
            FirstNonEmpty(summary.LastHeadingPolicyNormalizedTitle),
            FirstNonEmpty(summary.LastHeadingPolicyClass),
            FirstNonEmpty(summary.LastHeadingPolicyTreatment),
            FirstNonEmpty(summary.LastHeadingPolicyReasonCode),
            FirstNonEmpty(summary.LastHeadingPolicySummary),
            FirstNonEmpty(summary.PatchMutationFamily),
            FirstNonEmpty(summary.PatchAllowedEditScope),
            string.Join(",", summary.PatchTargetFiles),
            FirstNonEmpty(summary.RetrievalBackend),
            FirstNonEmpty(summary.RetrievalEmbedderModel),
            FirstNonEmpty(summary.RetrievalQueryKind),
            summary.RetrievalHitCount.ToString(),
            string.Join(",", summary.RetrievalSourceKinds),
            FirstNonEmpty(summary.TerminalNote)
        });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSeed));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public string BuildSummaryText(TaskboardRunTerminalSummaryRecord? summary)
    {
        if (summary is null || string.IsNullOrWhiteSpace(summary.SummaryId))
            return "A deterministic run summary will appear after the current run reaches a truthful terminal state.";

        var lines = new List<string>
        {
            $"Status: {DisplayValue(summary.FinalStatus)} ({DisplayValue(summary.TerminalCategory)})",
            $"Progress: batches {summary.CompletedBatchCount}/{summary.TotalBatchCount} work_items {summary.CompletedWorkItemCount}/{summary.TotalWorkItemCount}"
        };

        if (!string.IsNullOrWhiteSpace(summary.TerminalBatchTitle) || !string.IsNullOrWhiteSpace(summary.TerminalWorkItemTitle))
        {
            lines.Add(
                $"Terminal work: batch={DisplayValue(summary.TerminalBatchTitle)} item={DisplayValue(summary.TerminalWorkItemTitle)}");
        }

        if (!string.IsNullOrWhiteSpace(summary.BlockerReason))
        {
            lines.Add(
                $"Blocker: family={DisplayValue(summary.BlockerWorkFamily)} phrase={DisplayValue(summary.BlockerPhraseFamily)} operation={DisplayValue(summary.BlockerOperationKind)} lane={DisplayValue(summary.BlockerLaneKind)}");
            AppendDetailBlock(lines, "Reason", summary.BlockerReason);
        }

        if (!string.IsNullOrWhiteSpace(summary.PreferredCompletionProofTemplateId))
            lines.Add($"Completion proof: {summary.PreferredCompletionProofTemplateId}");
        if (summary.PreferredCompletionProofToolNames.Count > 0)
            lines.Add($"Completion proof tools: {string.Join(", ", summary.PreferredCompletionProofToolNames)}");
        if (!string.IsNullOrWhiteSpace(summary.PreferredCompletionProofStrength)
            || !string.IsNullOrWhiteSpace(summary.PreferredCompletionProofQuality)
            || !string.IsNullOrWhiteSpace(summary.PreferredCompletionProofProfile))
        {
            lines.Add(
                $"Completion quality: strength={DisplayValue(summary.PreferredCompletionProofStrength)} quality={DisplayValue(summary.PreferredCompletionProofQuality)} profile={DisplayValue(summary.PreferredCompletionProofProfile)} stronger_behavior_proof_missing={summary.PreferredCompletionProofStrongerBehaviorMissing.ToString().ToLowerInvariant()}");
        }
        if (!string.IsNullOrWhiteSpace(summary.PreferredCompletionProofReason))
            AppendDetailBlock(lines, "Completion proof reason", summary.PreferredCompletionProofReason);
        if (summary.PreferredCompletionProofInsufficiencyReasons.Count > 0)
            lines.Add($"Completion proof insufficiency: {string.Join(", ", summary.PreferredCompletionProofInsufficiencyReasons)}");
        lines.Add(BuildCompletionScopeLine(summary));
        if (!string.IsNullOrWhiteSpace(summary.LastSuccessfulChainTemplateId))
            lines.Add($"Last chain: {summary.LastSuccessfulChainTemplateId}");
        if (summary.LastSuccessfulToolNames.Count > 0)
            lines.Add($"Last tools: {string.Join(", ", summary.LastSuccessfulToolNames)}");
        if (!string.IsNullOrWhiteSpace(summary.LastVerificationOutcome))
            lines.Add($"Verification: {summary.LastVerificationOutcome} target={DisplayValue(summary.LastVerificationTarget)}");
        if (!string.IsNullOrWhiteSpace(summary.MaintenanceBaselineSolutionPath) || summary.MaintenanceAllowedRoots.Count > 0)
        {
            lines.Add(
                $"Baseline: solution={DisplayValue(summary.MaintenanceBaselineSolutionPath)} allowed_roots={DisplayValue(summary.MaintenanceAllowedRoots.Count == 0 ? "" : string.Join(", ", summary.MaintenanceAllowedRoots))}");
            if (summary.MaintenanceExcludedRoots.Count > 0)
                lines.Add($"Excluded generated roots: {string.Join(", ", summary.MaintenanceExcludedRoots)}");
        }
        if (!string.IsNullOrWhiteSpace(summary.MaintenanceGuardSummary))
            AppendDetailBlock(lines, "Maintenance guard", summary.MaintenanceGuardSummary);
        if (!string.IsNullOrWhiteSpace(summary.LastHeadingPolicySummary))
            AppendDetailBlock(lines, "Heading policy", $"normalized={summary.LastHeadingPolicyNormalizedTitle} class={summary.LastHeadingPolicyClass} treatment={summary.LastHeadingPolicyTreatment} reason={summary.LastHeadingPolicyReasonCode}{Environment.NewLine}{summary.LastHeadingPolicySummary}");
        if (!string.IsNullOrWhiteSpace(summary.WarningPolicyMode) || summary.LastVerificationWarningCount > 0 || summary.LastVerificationWarningCodes.Count > 0)
        {
            lines.Add(
                $"Warnings: mode={DisplayValue(summary.WarningPolicyMode)} count={summary.LastVerificationWarningCount} codes={DisplayValue(summary.LastVerificationWarningCodes.Count == 0 ? "" : string.Join(", ", summary.LastVerificationWarningCodes))}");
        }
        if (!string.IsNullOrWhiteSpace(summary.PatchMutationFamily) || !string.IsNullOrWhiteSpace(summary.PatchAllowedEditScope))
        {
            lines.Add(
                $"Patch: family={DisplayValue(summary.PatchMutationFamily)} scope={DisplayValue(summary.PatchAllowedEditScope)} files={DisplayValue(summary.PatchTargetFiles.Count == 0 ? "" : string.Join(", ", summary.PatchTargetFiles))}");
        }
        if (!string.IsNullOrWhiteSpace(summary.RetrievalBackend) || summary.RetrievalHitCount > 0)
        {
            lines.Add(
                $"Retrieval: backend={DisplayValue(summary.RetrievalBackend)} embedder={DisplayValue(summary.RetrievalEmbedderModel)} query={DisplayValue(summary.RetrievalQueryKind)} hits={summary.RetrievalHitCount}");
            if (summary.RetrievalSourceKinds.Count > 0)
                lines.Add($"Retrieval sources: {string.Join(", ", summary.RetrievalSourceKinds)}");
        }
        if (!string.IsNullOrWhiteSpace(summary.RepairAttemptSummary))
            AppendDetailBlock(lines, "Repair", summary.RepairAttemptSummary);
        if (summary.RepairMutationObserved)
        {
            lines.Add(
                $"Mutation proof: tool={DisplayValue(summary.RepairMutationToolName)} files={DisplayValue(summary.RepairMutatedFiles.Count == 0 ? "" : string.Join(", ", summary.RepairMutatedFiles))} at={DisplayValue(summary.RepairMutationUtc)}");
            if (!string.IsNullOrWhiteSpace(summary.VerificationAfterMutationOutcome))
                lines.Add($"Verification after mutation: {summary.VerificationAfterMutationOutcome} at {DisplayValue(summary.VerificationAfterMutationUtc)}");
        }
        if (!string.IsNullOrWhiteSpace(summary.PatchContractArtifactRelativePath))
            AppendDetailBlock(lines, "Patch contract", summary.PatchContractArtifactRelativePath, treatAsPath: true);
        if (!string.IsNullOrWhiteSpace(summary.PatchPlanArtifactRelativePath))
            AppendDetailBlock(lines, "Patch plan", summary.PatchPlanArtifactRelativePath, treatAsPath: true);
        if (!string.IsNullOrWhiteSpace(summary.RetrievalContextPacketArtifactRelativePath))
            AppendDetailBlock(lines, "Context packet", summary.RetrievalContextPacketArtifactRelativePath, treatAsPath: true);
        if (!string.IsNullOrWhiteSpace(summary.RetrievalResultArtifactRelativePath))
            AppendDetailBlock(lines, "Retrieval result", summary.RetrievalResultArtifactRelativePath, treatAsPath: true);
        if (summary.ChangedFileCount > 0)
            AppendDetailBlock(lines, "Changed files", $"{summary.ChangedFileCount} ({string.Join(", ", summary.KeyChangedPaths)})", treatAsPath: true);
        if (summary.FileTouchCount > 0 || summary.TouchedFileCount > 0)
        {
            lines.Add(
                $"File touches: {summary.FileTouchCount} across {summary.TouchedFileCount} file(s) repeated={summary.RepeatedFileTouchCount}");
            if (summary.TopRepeatedTouchPaths.Count > 0)
                AppendDetailBlock(lines, "Top repeated paths", string.Join(", ", summary.TopRepeatedTouchPaths), treatAsPath: true);
        }
        if (summary.SatisfactionSkipCount > 0)
        {
            lines.Add(
                $"Satisfied skips: {summary.SatisfactionSkipCount} avoided_repeated_touches={summary.RepeatedFileTouchesAvoidedCount}");
            if (!string.IsNullOrWhiteSpace(summary.LastSatisfactionSkipWorkItemTitle))
            {
                lines.Add(
                    $"Last skipped step: {summary.LastSatisfactionSkipWorkItemTitle} reason={DisplayValue(summary.LastSatisfactionSkipReasonCode)}");
            }

            if (summary.TopSatisfactionSkipReasonCodes.Count > 0)
                lines.Add($"Top skip reasons: {string.Join(", ", summary.TopSatisfactionSkipReasonCodes)}");
        }
        if (!string.IsNullOrWhiteSpace(summary.NormalizedRunArtifactRelativePath))
            AppendDetailBlock(lines, "Run record", summary.NormalizedRunArtifactRelativePath, treatAsPath: true);
        if (!string.IsNullOrWhiteSpace(summary.IndexExportArtifactRelativePath))
            AppendDetailBlock(lines, "Index export", summary.IndexExportArtifactRelativePath, treatAsPath: true);
        if (!string.IsNullOrWhiteSpace(summary.CorpusExportArtifactRelativePath))
            AppendDetailBlock(lines, "Corpus export", summary.CorpusExportArtifactRelativePath, treatAsPath: true);
        AppendDetailBlock(lines, "Note", DisplayValue(summary.TerminalNote));
        AppendDetailBlock(lines, "Summary artifact", DisplayValue(summary.SummaryArtifactRelativePath), treatAsPath: true);

        return string.Join(Environment.NewLine, lines);
    }

    private static (string TemplateId, List<string> ToolNames, string Reason, string Profile, string Quality, string Strength, bool StrongerBehaviorMissing) SelectPreferredCompletionProof(
        IReadOnlyList<CompletionProofSourceRecord> successfulChains,
        TaskboardPlanRunStateRecord runState)
    {
        var ranked = successfulChains
            .Select(item =>
            {
                var toolNames = item.Record?.Steps
                    .Where(step => step.ExecutionAttempted && !string.IsNullOrWhiteSpace(step.ToolName))
                    .Select(step => step.ToolName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? [];
                var generationSignal = ResolveGenerationSignal(item.Record?.Steps.Select(step => step.StructuredDataJson));
                var score = ScoreCompletionProof(item.Record, toolNames, generationSignal);
                return new CompletionProofCandidate
                {
                    SourceKind = "chain",
                    TemplateId = FirstNonEmpty(item.Record?.SelectedTemplateName),
                    Record = item.Record,
                    ToolNames = toolNames,
                    Score = score,
                    Profile = generationSignal.Profile,
                    Quality = generationSignal.Quality,
                    Strength = generationSignal.Strength,
                    StrongerBehaviorMissing = generationSignal.StrongerBehaviorMissing,
                    CompletedUtc = ParseUtc(FirstNonEmpty(item.Record?.CompletedUtc, item.Artifact.UpdatedUtc, item.Artifact.CreatedUtc))
                };
            })
            .Concat(BuildRunStateCompletionProofCandidates(runState))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.CompletedUtc)
            .ToList();

        if (ranked.Count == 0)
            return ("", [], "", "", "", "", false);

        var selected = ranked.First();
        selected = MaybeDowngradeForBehaviorFollowUp(selected, runState);
        var latestChain = ranked
            .Where(item => string.Equals(item.SourceKind, "chain", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CompletedUtc)
            .FirstOrDefault();
        var selectedTemplate = FirstNonEmpty(selected.TemplateId, selected.Record?.SelectedTemplateName, "(none)");
        var reason = !string.IsNullOrWhiteSpace(selected.Reason)
            ? selected.Reason
            : latestChain is not null && selected.Score >= 3 && latestChain.Score < selected.Score
                ? $"Preferred actionable completion proof `{selectedTemplate}` over later inspection-only success `{FirstNonEmpty(latestChain.TemplateId, latestChain.Record?.SelectedTemplateName, "(none)")}`."
                : selected.StrongerBehaviorMissing
                    ? $"Selected `{selectedTemplate}` as the strongest completion proof currently available, but stronger validated behavior proof is still missing for this run."
                    : $"Selected `{selectedTemplate}` as the strongest meaningful completion proof available for this run.";
        return (
            FirstNonEmpty(selected.TemplateId, selected.Record?.SelectedTemplateName),
            selected.ToolNames,
            reason,
            selected.Profile,
            selected.Quality,
            selected.Strength,
            selected.StrongerBehaviorMissing);
    }

    private static IEnumerable<CompletionProofCandidate> BuildRunStateCompletionProofCandidates(TaskboardPlanRunStateRecord runState)
    {
        var generationSignal = ResolveGenerationSignal(
            runState.ExecutedToolCalls
                .Where(call => string.Equals(call.Stage, "completed", StringComparison.OrdinalIgnoreCase))
                .Select(call => call.StructuredDataJson));

        if (!string.IsNullOrWhiteSpace(runState.LastMutationToolName))
        {
            var toolNames = runState.ExecutedToolCalls
                .Where(call =>
                    string.Equals(call.Stage, "completed", StringComparison.OrdinalIgnoreCase)
                    && (!string.IsNullOrWhiteSpace(call.ToolName))
                    && (call.MutationObserved
                        || string.Equals(call.ToolName, runState.LastMutationToolName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(call.ToolName, "verify_patch_draft", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(call.ToolName, "dotnet_build", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(call.ToolName, "dotnet_test", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(call.ToolName, "cmake_build", StringComparison.OrdinalIgnoreCase)))
                .Select(call => call.ToolName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (toolNames.Count == 0)
                toolNames.Add(runState.LastMutationToolName);

            var hasVerificationAfterMutation = !string.IsNullOrWhiteSpace(runState.LastVerificationAfterMutationOutcome);
            if (!string.IsNullOrWhiteSpace(generationSignal.Strength))
            {
                yield return BuildGenerationBackedRunStateCandidate(
                    runState,
                    toolNames,
                    generationSignal,
                    hasVerificationAfterMutation,
                    FirstNonEmpty(runState.LastObservedChainTemplateId, runState.LastPlannedChainTemplateId, "repair_execution_chain"),
                    FirstNonEmpty(runState.LastVerificationAfterMutationUtc, runState.LastMutationUtc, runState.UpdatedUtc));
                yield break;
            }

            yield return new CompletionProofCandidate
            {
                SourceKind = "run_state",
                TemplateId = FirstNonEmpty(runState.LastObservedChainTemplateId, runState.LastPlannedChainTemplateId, "repair_execution_chain"),
                ToolNames = toolNames,
                Score = hasVerificationAfterMutation ? 5 : 2,
                Profile = hasVerificationAfterMutation ? "verified_integrated_behavior" : "mutation_only",
                Quality = hasVerificationAfterMutation ? "verified_integrated_behavior" : "accepted_structural_impl",
                Strength = hasVerificationAfterMutation ? "verified_integrated_behavior" : "accepted_structural_impl",
                StrongerBehaviorMissing = !hasVerificationAfterMutation,
                CompletedUtc = ParseUtc(FirstNonEmpty(runState.LastVerificationAfterMutationUtc, runState.LastMutationUtc, runState.UpdatedUtc)),
                Reason = hasVerificationAfterMutation
                    ? "Selected current-run mutation and verification evidence as the strongest completion proof available."
                    : "Selected current-run mutation evidence, but stronger verified behavior proof is still missing for this run."
            };
            yield break;
        }

        var verificationTools = runState.ExecutedToolCalls
            .Where(call =>
                string.Equals(call.Stage, "completed", StringComparison.OrdinalIgnoreCase)
                && call.ToolName is "verify_patch_draft" or "dotnet_build" or "dotnet_test" or "cmake_build" or "ninja_build" or "make_build")
            .Select(call => call.ToolName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasVerificationProof =
            !string.IsNullOrWhiteSpace(runState.LastVerificationAfterMutationOutcome)
            || string.Equals(runState.LastCompletedWorkFamily, "build_verify", StringComparison.OrdinalIgnoreCase)
            || string.Equals(runState.LastCompletedOperationKind, "build_solution", StringComparison.OrdinalIgnoreCase)
            || verificationTools.Count > 0;
        if (hasVerificationProof)
        {
            if (!string.IsNullOrWhiteSpace(generationSignal.Strength))
            {
                yield return BuildGenerationBackedRunStateCandidate(
                    runState,
                    verificationTools,
                    generationSignal,
                    verificationOccurred: true,
                    FirstNonEmpty(runState.LastObservedChainTemplateId, runState.LastPlannedChainTemplateId, "workspace.build_verify.v1"),
                    FirstNonEmpty(runState.LastVerificationAfterMutationUtc, runState.UpdatedUtc));
                yield break;
            }

            yield return new CompletionProofCandidate
            {
                SourceKind = "run_state",
                TemplateId = FirstNonEmpty(runState.LastObservedChainTemplateId, runState.LastPlannedChainTemplateId, "workspace.build_verify.v1"),
                ToolNames = verificationTools,
                Score = 4,
                Profile = "verified_integrated_behavior",
                Quality = "verified_integrated_behavior",
                Strength = "verified_integrated_behavior",
                StrongerBehaviorMissing = false,
                CompletedUtc = ParseUtc(FirstNonEmpty(runState.LastVerificationAfterMutationUtc, runState.UpdatedUtc)),
                Reason = "Selected bounded verification evidence as the strongest completion proof available for this run."
            };
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(runState.LastSatisfactionSkipReasonCode))
        {
            yield return new CompletionProofCandidate
            {
                SourceKind = "run_state",
                TemplateId = "state_satisfaction_fast_path",
                ToolNames = [],
                Score = 2,
                Profile = "state_satisfaction",
                Quality = "covered_without_reexecution",
                Strength = "satisfied_state_completion",
                StrongerBehaviorMissing = true,
                CompletedUtc = ParseUtc(FirstNonEmpty(runState.UpdatedUtc, runState.StartedUtc)),
                Reason = $"Selected state-satisfaction proof `{runState.LastSatisfactionSkipReasonCode}` because the required state was already satisfied without redundant execution."
            };
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(runState.LastSupportCoverageSummary))
        {
            yield return new CompletionProofCandidate
            {
                SourceKind = "run_state",
                TemplateId = "structural_support_coverage",
                ToolNames = [],
                Score = 1,
                Profile = "support_heading_coverage",
                Quality = "structural_support_only",
                Strength = "weak_structural_completion",
                StrongerBehaviorMissing = true,
                CompletedUtc = ParseUtc(FirstNonEmpty(runState.UpdatedUtc, runState.StartedUtc)),
                Reason = "Selected structural support coverage as completion proof because no stronger actionable or verification evidence was recorded for this run."
            };
        }
    }

    private static CompletionProofCandidate BuildGenerationBackedRunStateCandidate(
        TaskboardPlanRunStateRecord runState,
        IReadOnlyList<string> toolNames,
        CompletionProofGenerationSignal generationSignal,
        bool verificationOccurred,
        string defaultTemplateId,
        string completedUtc)
    {
        var templateId = FirstNonEmpty(defaultTemplateId, "workspace.build_verify.v1");
        if (verificationOccurred
            && string.Equals(generationSignal.Strength, "integrated_behavior_impl", StringComparison.OrdinalIgnoreCase))
        {
            return new CompletionProofCandidate
            {
                SourceKind = "run_state",
                TemplateId = templateId,
                ToolNames = [.. toolNames],
                Score = 5,
                Profile = "verified_integrated_behavior",
                Quality = "verified_integrated_behavior",
                Strength = "verified_integrated_behavior",
                StrongerBehaviorMissing = false,
                CompletedUtc = ParseUtc(completedUtc),
                Reason = "Selected integrated post-write behavior plus bounded verification as the strongest completion proof available."
            };
        }

        var score = generationSignal.Strength switch
        {
            "family_aligned_impl" => verificationOccurred ? 4 : 3,
            "accepted_behavior_impl" => verificationOccurred ? 3 : 2,
            "accepted_structural_impl" => verificationOccurred ? 2 : 1,
            "accepted_write_only" => 1,
            _ => verificationOccurred ? 3 : 2
        };

        var reason = verificationOccurred
            ? $"Selected bounded verification evidence for `{FirstNonEmpty(generationSignal.Profile, templateId)}`, but stronger integrated behavior proof is still missing for this run."
            : $"Selected accepted generation evidence for `{FirstNonEmpty(generationSignal.Profile, templateId)}`, but bounded verification and integrated behavior proof are still missing for this run.";

        return new CompletionProofCandidate
        {
            SourceKind = "run_state",
            TemplateId = templateId,
            ToolNames = [.. toolNames],
            Score = score,
            Profile = FirstNonEmpty(generationSignal.Profile),
            Quality = FirstNonEmpty(generationSignal.BehaviorDepthTier, generationSignal.Quality),
            Strength = FirstNonEmpty(generationSignal.Strength),
            StrongerBehaviorMissing = true,
            CompletedUtc = ParseUtc(completedUtc),
            Reason = reason
        };
    }

    private static int ScoreCompletionProof(ToolChainRecord? record, IReadOnlyList<string> toolNames, CompletionProofGenerationSignal generationSignal)
    {
        if (record is null)
            return 0;

        var template = FirstNonEmpty(record.SelectedTemplateName);
        var hasMutation = record.Steps.Any(step => step.MutationObserved)
            || toolNames.Any(tool => tool is "apply_patch_draft" or "write_file" or "replace_in_file" or "append_file" or "create_file");
        var hasVerificationTools = template is "repair_execution_chain" or "workspace.build_verify.v1" or "workspace.native_build_verify.v1"
            || toolNames.Any(tool => tool is "plan_repair" or "verify_patch_draft" or "dotnet_build" or "dotnet_test" or "cmake_build" or "ninja_build" or "make_build");

        if (string.Equals(generationSignal.Strength, "verified_integrated_behavior", StringComparison.OrdinalIgnoreCase))
            return 5;

        if (string.Equals(generationSignal.Strength, "integrated_behavior_impl", StringComparison.OrdinalIgnoreCase))
            return hasVerificationTools ? 4 : 3;

        if (string.Equals(generationSignal.Strength, "family_aligned_impl", StringComparison.OrdinalIgnoreCase))
            return hasVerificationTools ? 3 : 2;

        if (string.Equals(generationSignal.Strength, "accepted_behavior_impl", StringComparison.OrdinalIgnoreCase))
            return hasVerificationTools ? 2 : 2;

        if (string.Equals(generationSignal.Strength, "accepted_structural_impl", StringComparison.OrdinalIgnoreCase))
            return hasVerificationTools ? 2 : 1;

        if (string.Equals(generationSignal.Strength, "accepted_write_only", StringComparison.OrdinalIgnoreCase))
            return 1;

        if (hasVerificationTools)
            return 4;

        if (hasMutation)
            return 2;

        var inspectionOnly = string.Equals(template, "artifact_inspection_single_step", StringComparison.OrdinalIgnoreCase)
            || (toolNames.Count > 0 && toolNames.All(tool => tool is "show_artifacts" or "show_memory"));
        return inspectionOnly ? 1 : 2;
    }

    private static CompletionProofGenerationSignal ResolveGenerationSignal(IEnumerable<string?>? structuredDataJsonValues)
    {
        if (structuredDataJsonValues is null)
            return CompletionProofGenerationSignal.Empty;

        foreach (var json in structuredDataJsonValues.Reverse())
        {
            if (!TryParseGenerationGuardrail(json, out var evaluation))
                continue;

            return new CompletionProofGenerationSignal
            {
                Profile = FormatGenerationProfile(evaluation.Contract?.Profile ?? CSharpGenerationProfile.None),
                Quality = FirstNonEmpty(evaluation.OutputQuality),
                Strength = FirstNonEmpty(evaluation.CompletionStrength),
                StrongerBehaviorMissing = evaluation.StrongerBehaviorProofStillMissing,
                BehaviorDepthTier = FirstNonEmpty(evaluation.BehaviorDepthTier, evaluation.OutputQuality),
                FamilyAlignmentStatus = FirstNonEmpty(evaluation.FamilyAlignmentStatus),
                IntegrationStatus = FirstNonEmpty(evaluation.IntegrationStatus)
            };
        }

        return CompletionProofGenerationSignal.Empty;
    }

    private static string AppendCurrentScaffoldContinuationNote(string reason, TaskboardPlanRunStateRecord runState)
    {
        if (runState is null)
            return reason;

        var hasCurrentScaffoldBlocker =
            runState.PlanStatus is TaskboardPlanRuntimeStatus.Blocked or TaskboardPlanRuntimeStatus.Failed
            && !runState.LastProjectAttachProjectExistedAtDecision
            && !string.IsNullOrWhiteSpace(runState.LastProjectAttachTargetPath)
            && (string.Equals(runState.LastProjectAttachContinuationStatus, "attach_deferred_missing_project", StringComparison.OrdinalIgnoreCase)
                || string.Equals(runState.LastProjectAttachContinuationStatus, "attach_missing_project", StringComparison.OrdinalIgnoreCase)
                || ContainsIgnoreCase(runState.LastBlockerReason, "could not find project or directory")
                || ContainsIgnoreCase(runState.LastBlockerReason, "solution update validation failed"));
        if (!hasCurrentScaffoldBlocker)
            return reason;

        var continuationNote = $"Current scaffold blocker: `{runState.LastProjectAttachTargetPath}` did not exist at attach time, so scaffold continuity still depends on `{FirstNonEmpty(runState.LastProjectAttachInsertedStep, "create_dotnet_project")}` before solution attach can complete.";
        if (string.IsNullOrWhiteSpace(reason))
            return continuationNote;

        return $"{reason} {continuationNote}";
    }

    private static List<string> BuildProofInsufficiencyReasons(TaskboardPlanRunStateRecord runState)
    {
        var reasons = new List<string>();
        if (runState is null)
            return reasons;

        var hasVerifiedRepairClosure = HasVerifiedRepairClosureProof(runState);

        if (runState.PlanStatus is TaskboardPlanRuntimeStatus.Blocked or TaskboardPlanRuntimeStatus.Failed
            && !string.IsNullOrWhiteSpace(FirstNonEmpty(runState.LastBlockerReason, runState.LastResultSummary)))
        {
            reasons.Add("live_blocker_unresolved");
        }

        if (!hasVerifiedRepairClosure
            && (string.Equals(runState.LastRepairContinuationStatus, "stopped_inspect_only", StringComparison.OrdinalIgnoreCase)
                || string.Equals(runState.LastRepairDraftKind, "inspect_only", StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add("repair_preview_only");
        }

        if (!hasVerifiedRepairClosure
            && (string.Equals(runState.LastRepairSymbolRecoveryStatus, "generated_symbol_not_reconciled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(runState.LastRepairSymbolRecoveryStatus, "generated_symbol_namespace_unproven", StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add("generated_symbol_not_reconciled");
        }

        var generationSignal = ResolveGenerationSignal(runState.ExecutedToolCalls
            .Where(call => string.Equals(call.Stage, "completed", StringComparison.OrdinalIgnoreCase))
            .Select(call => call.StructuredDataJson));
        if (generationSignal.StrongerBehaviorMissing
            && !string.IsNullOrWhiteSpace(generationSignal.Strength)
            && !string.Equals(generationSignal.Strength, "verified_integrated_behavior", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("accepted_write_without_closure");
        }

        if (HasBehaviorDepthFollowUpRequirement(runState))
        {
            reasons.Add("required_adjacent_integration_not_attempted");

            var integrationGapKind = NormalizeBehaviorDepthGapKind(runState.LastBehaviorDepthIntegrationGapKind);
            if (!string.IsNullOrWhiteSpace(integrationGapKind))
                reasons.Add(integrationGapKind);

            if (ContainsAny(runState.LastBehaviorDepthFollowUpRecommendation, "consumer", "registration")
                || integrationGapKind is "missing_repository_consumer"
                    or "missing_repository_store_consumer"
                    or "missing_service_registration")
                reasons.Add("missing_consumer_surface");

            if (string.Equals(runState.LastBehaviorDepthCompletionRecommendation, "accepted_behavior_without_closure", StringComparison.OrdinalIgnoreCase)
                || string.Equals(runState.LastBehaviorDepthCompletionRecommendation, "followup_required_for_behavior_depth", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("local_only_success");
            }
        }

        return reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool HasVerifiedRepairClosureProof(TaskboardPlanRunStateRecord runState)
    {
        if (runState is null)
            return false;

        if (string.Equals(runState.LastRepairContinuationStatus, "verified_after_symbol_reconciliation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(runState.LastRepairContinuationStatus, "verified_after_mutation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var latestVerification = runState.ExecutedToolCalls
            .Where(call =>
                string.Equals(call.Stage, "completed", StringComparison.OrdinalIgnoreCase)
                && string.Equals(call.ToolName, "verify_patch_draft", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(call => ParseUtc(call.CreatedUtc))
            .FirstOrDefault();

        return latestVerification is not null
            && string.Equals(latestVerification.ResultClassification, "verified_fixed", StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendProofInsufficiencyReasonNote(
        string reason,
        IReadOnlyList<string> insufficiencyReasons,
        TaskboardPlanRunStateRecord runState)
    {
        var effectiveReason = AppendCurrentScaffoldContinuationNote(reason, runState);
        if (insufficiencyReasons.Count == 0)
            return effectiveReason;

        var insufficiencyNote = $"Proof insufficiency: {string.Join(", ", insufficiencyReasons)}.";
        return string.IsNullOrWhiteSpace(effectiveReason)
            ? insufficiencyNote
            : $"{effectiveReason} {insufficiencyNote}";
    }

    private static CompletionProofCandidate MaybeDowngradeForBehaviorFollowUp(
        CompletionProofCandidate selected,
        TaskboardPlanRunStateRecord runState)
    {
        if (!HasBehaviorDepthFollowUpRequirement(runState))
            return selected;

        var alreadyLocalOnly =
            string.Equals(selected.Strength, "local_verified_only", StringComparison.OrdinalIgnoreCase)
            || (selected.Score <= 3 && selected.StrongerBehaviorMissing);
        if (alreadyLocalOnly)
            return selected;

        return new CompletionProofCandidate
        {
            SourceKind = selected.SourceKind,
            TemplateId = selected.TemplateId,
            Record = selected.Record,
            ToolNames = [.. selected.ToolNames],
            Score = Math.Min(selected.Score, 3),
            Profile = FirstNonEmpty(selected.Profile, "local_verified_only"),
            Quality = FirstNonEmpty(selected.Quality, "local_verified_only"),
            Strength = "local_verified_only",
            StrongerBehaviorMissing = true,
            CompletedUtc = selected.CompletedUtc,
            Reason = $"Selected `{FirstNonEmpty(selected.TemplateId, selected.Record?.SelectedTemplateName, "(none)")}` as the strongest local proof currently available, but adjacent integration follow-up is still required: {FirstNonEmpty(runState.LastBehaviorDepthFollowUpRecommendation, runState.LastBehaviorDepthCompletionRecommendation, "follow-up required")}. {BuildBehaviorDepthFollowThroughDescriptor(runState)}"
        };
    }

    private static string BuildCompletionScopeLine(TaskboardRunTerminalSummaryRecord summary)
    {
        if (summary is null)
            return "Completion scope: (none)";

        if (string.Equals(summary.FinalStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return summary.PreferredCompletionProofStrongerBehaviorMissing || summary.PreferredCompletionProofInsufficiencyReasons.Count > 0
                ? "Completion scope: local work completed, but feature-level follow-through is still insufficiently proven."
                : "Completion scope: feature- and plan-level completion are currently supported by the strongest available proof.";
        }

        return "Completion scope: current run remains below feature-level completion.";
    }

    private static bool HasBehaviorDepthFollowUpRequirement(TaskboardPlanRunStateRecord runState)
    {
        if (runState is null)
            return false;

        if (string.Equals(runState.LastBehaviorDepthCompletionRecommendation, "followup_required_for_behavior_depth", StringComparison.OrdinalIgnoreCase)
            || string.Equals(runState.LastBehaviorDepthCompletionRecommendation, "accepted_behavior_without_closure", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(runState.LastBehaviorDepthFollowUpRecommendation)
            && !string.Equals(runState.LastBehaviorDepthFollowUpRecommendation, "no_additional_followup_required", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeBehaviorDepthGapKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "integration_satisfied", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return value.Trim();
    }

    private static string BuildBehaviorDepthFollowThroughDescriptor(TaskboardPlanRunStateRecord runState)
    {
        var parts = new List<string>();
        var gapKind = NormalizeBehaviorDepthGapKind(runState.LastBehaviorDepthIntegrationGapKind);
        if (!string.IsNullOrWhiteSpace(runState.LastBehaviorDepthNextFollowThroughHint))
            parts.Add($"next_followthrough={runState.LastBehaviorDepthNextFollowThroughHint}");
        if (!string.IsNullOrWhiteSpace(gapKind))
            parts.Add($"gap={gapKind}");
        if (!string.IsNullOrWhiteSpace(runState.LastBehaviorDepthTargetPath))
            parts.Add($"source={runState.LastBehaviorDepthTargetPath}");

        return parts.Count == 0
            ? ""
            : $"Follow-through detail: {string.Join(" ", parts)}.";
    }

    private static bool TryParseGenerationGuardrail(string? json, out CSharpGenerationGuardrailEvaluationRecord evaluation)
    {
        evaluation = new CSharpGenerationGuardrailEvaluationRecord();
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("generation_guardrail", out var section))
                return false;

            var parsed = JsonSerializer.Deserialize<CSharpGenerationGuardrailEvaluationRecord>(section.GetRawText());
            if (parsed is null)
                return false;

            evaluation = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatGenerationProfile(CSharpGenerationProfile profile)
    {
        return profile switch
        {
            CSharpGenerationProfile.ContractGeneration => "contract_generation",
            CSharpGenerationProfile.SimpleImplementation => "simple_implementation",
            CSharpGenerationProfile.TestRegistryImplementation => "test_registry_impl",
            CSharpGenerationProfile.SnapshotBuilderImplementation => "snapshot_builder_impl",
            CSharpGenerationProfile.FindingsNormalizerImplementation => "findings_normalizer_impl",
            CSharpGenerationProfile.TestHelperImplementation => "test_helper_impl",
            CSharpGenerationProfile.BuilderImplementation => "builder_impl",
            CSharpGenerationProfile.NormalizerImplementation => "normalizer_impl",
            CSharpGenerationProfile.RepositoryImplementation => "repository_implementation",
            CSharpGenerationProfile.ViewmodelGeneration => "viewmodel_generation",
            CSharpGenerationProfile.WpfXamlStubOnly => "wpf_xaml_stub_only",
            CSharpGenerationProfile.WpfXamlLayoutImplementation => "wpf_xaml_layout_impl",
            CSharpGenerationProfile.WpfViewmodelImplementation => "wpf_viewmodel_impl",
            CSharpGenerationProfile.WpfShellIntegration => "wpf_shell_integration",
            CSharpGenerationProfile.RuntimeWiring => "runtime_wiring",
            _ => "none"
        };
    }

    private sealed class CompletionProofSourceRecord
    {
        public ArtifactRecord Artifact { get; set; } = new();
        public ToolChainRecord? Record { get; set; }
    }

    private sealed class CompletionProofCandidate
    {
        public string SourceKind { get; set; } = "";
        public string TemplateId { get; set; } = "";
        public ToolChainRecord? Record { get; set; }
        public List<string> ToolNames { get; set; } = [];
        public int Score { get; set; }
        public string Profile { get; set; } = "";
        public string Quality { get; set; } = "";
        public string Strength { get; set; } = "";
        public bool StrongerBehaviorMissing { get; set; }
        public DateTime CompletedUtc { get; set; }
        public string Reason { get; set; } = "";
    }

    private sealed class CompletionProofGenerationSignal
    {
        public static CompletionProofGenerationSignal Empty { get; } = new();

        public string Profile { get; set; } = "";
        public string Quality { get; set; } = "";
        public string Strength { get; set; } = "";
        public string BehaviorDepthTier { get; set; } = "";
        public string FamilyAlignmentStatus { get; set; } = "";
        public string IntegrationStatus { get; set; } = "";
        public bool StrongerBehaviorMissing { get; set; }
    }

    public string BuildPrimaryShellSummaryText(TaskboardRunTerminalSummaryRecord? summary)
    {
        if (summary is null || string.IsNullOrWhiteSpace(summary.SummaryId))
            return "";

        var lines = new List<string>
        {
            $"{DisplayValue(summary.ActionName)} terminal summary",
            $"Status: {DisplayValue(summary.FinalStatus)} ({DisplayValue(summary.TerminalCategory)})",
            $"Progress: batches {summary.CompletedBatchCount}/{summary.TotalBatchCount} work_items {summary.CompletedWorkItemCount}/{summary.TotalWorkItemCount}"
        };

        if (!string.IsNullOrWhiteSpace(summary.TerminalBatchTitle) || !string.IsNullOrWhiteSpace(summary.TerminalWorkItemTitle))
            lines.Add($"Terminal work: {DisplayValue(summary.TerminalBatchTitle)} -> {DisplayValue(summary.TerminalWorkItemTitle)}");

        if (!string.IsNullOrWhiteSpace(summary.BlockerReason))
        {
            lines.Add($"Blocker: family={DisplayValue(summary.BlockerWorkFamily)} phrase={DisplayValue(summary.BlockerPhraseFamily)} operation={DisplayValue(summary.BlockerOperationKind)} lane={DisplayValue(summary.BlockerLaneKind)}");
            lines.Add($"Reason: {DisplayValue(summary.BlockerReason)}");
        }

        if (!string.IsNullOrWhiteSpace(summary.PreferredCompletionProofTemplateId))
            lines.Add($"Completion proof: {DisplayValue(summary.PreferredCompletionProofTemplateId)}");
        if (summary.PreferredCompletionProofInsufficiencyReasons.Count > 0)
            lines.Add($"Completion proof insufficiency: {string.Join(", ", summary.PreferredCompletionProofInsufficiencyReasons)}");
        if (!string.IsNullOrWhiteSpace(summary.LastVerificationOutcome))
            lines.Add($"Verification: {DisplayValue(summary.LastVerificationOutcome)} target={DisplayValue(summary.LastVerificationTarget)}");
        if (!string.IsNullOrWhiteSpace(summary.MaintenanceBaselineSolutionPath) || summary.MaintenanceAllowedRoots.Count > 0)
            lines.Add($"Baseline: solution={DisplayValue(summary.MaintenanceBaselineSolutionPath)} allowed_roots={DisplayValue(summary.MaintenanceAllowedRoots.Count == 0 ? "" : string.Join(", ", summary.MaintenanceAllowedRoots))}");
        if (!string.IsNullOrWhiteSpace(summary.MaintenanceGuardSummary))
            lines.Add($"Maintenance guard: {DisplayValue(summary.MaintenanceGuardSummary)}");
        if (!string.IsNullOrWhiteSpace(summary.RepairAttemptSummary))
            lines.Add($"Repair: {DisplayValue(summary.RepairAttemptSummary)}");
        if (!string.IsNullOrWhiteSpace(summary.TerminalNote))
            lines.Add($"Note: {DisplayValue(summary.TerminalNote)}");
        if (!string.IsNullOrWhiteSpace(summary.SummaryArtifactRelativePath))
            lines.Add($"Summary artifact: {DisplayValue(summary.SummaryArtifactRelativePath)}");

        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendDetailBlock(List<string> lines, string label, string? value, bool treatAsPath = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        lines.Add($"{label}:");
        lines.Add($"  {PrepareDisplayValue(value, treatAsPath)}");
    }

    private static string PrepareDisplayValue(string value, bool treatAsPath)
    {
        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
        if (treatAsPath)
        {
            normalized = normalized
                .Replace("\\", "\\\u200B", StringComparison.Ordinal)
                .Replace("/", "/\u200B", StringComparison.Ordinal)
                .Replace(", ", ",\u200B ", StringComparison.Ordinal)
                .Replace(" | ", " |\u200B ", StringComparison.Ordinal);
        }

        return normalized.Replace("\n", $"{Environment.NewLine}  ", StringComparison.Ordinal);
    }

    private static (string summary, string target, int warningCount, List<string> warningCodes, string warningPolicyMode) BuildVerificationOutcome(ArtifactRecord? artifact, TaskboardPlanRunStateRecord runState)
    {
        if (artifact is null)
        {
            if (!string.IsNullOrWhiteSpace(runState.LastFailureOutcomeType))
            {
                return (
                    $"{runState.LastFailureOutcomeType} {FirstNonEmpty(runState.LastFailureErrorCode, runState.LastFailureNormalizedSummary)}".Trim(),
                    runState.LastFailureTargetPath,
                    0,
                    [],
                    "");
            }

            return runState.PlanStatus == TaskboardPlanRuntimeStatus.Completed
                ? (runState.LastResultSummary, "", 0, [], "")
                : ("", "", 0, [], "");
        }

        if (string.Equals(artifact.ArtifactType, "verification_result", StringComparison.OrdinalIgnoreCase))
        {
            var verification = TryDeserialize<VerificationOutcomeRecord>(artifact.Content);
            if (verification is not null)
            {
                return (
                    FirstNonEmpty(verification.OutcomeClassification, verification.Explanation, artifact.Summary),
                    verification.ResolvedTarget,
                    verification.AfterWarningCount ?? 0,
                    verification.WarningCodes,
                    verification.WarningPolicyMode);
            }
        }

        if (string.Equals(artifact.ArtifactType, "auto_validation_result", StringComparison.OrdinalIgnoreCase))
        {
            var validation = TryDeserialize<AutoValidationResultRecord>(artifact.Content);
            if (validation is not null)
            {
                return (
                    FirstNonEmpty(validation.OutcomeClassification, validation.Summary, artifact.Summary),
                    validation.ResolvedTarget,
                    0,
                    [],
                    "");
            }
        }

        return (artifact.Summary, "", 0, [], "");
    }

    private static (string mutationFamily, string allowedEditScope, List<string> targetFiles, string contractArtifactPath, string planArtifactPath) BuildPatchFoundationSnapshot(IReadOnlyList<ArtifactRecord> artifacts)
    {
        var contractArtifact = artifacts
            .Where(artifact => string.Equals(artifact.ArtifactType, "csharp_patch_contract", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(artifact => ParseUtc(FirstNonEmpty(artifact.UpdatedUtc, artifact.CreatedUtc)))
            .FirstOrDefault();
        var contract = TryDeserialize<CSharpPatchWorkContractRecord>(contractArtifact?.Content);

        var planArtifact = artifacts
            .Where(artifact => string.Equals(artifact.ArtifactType, "csharp_patch_plan", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(artifact => ParseUtc(FirstNonEmpty(artifact.UpdatedUtc, artifact.CreatedUtc)))
            .FirstOrDefault();
        var plan = TryDeserialize<CSharpPatchPlanRecord>(planArtifact?.Content);

        var targetFiles = contract?.TargetFiles?.Count > 0
            ? contract.TargetFiles.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : plan?.TargetFiles?.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                ?? [];

        return (
            FirstNonEmpty(contract?.MutationFamily, plan?.MutationFamily),
            FirstNonEmpty(contract?.AllowedEditScope, plan?.AllowedEditScope),
            targetFiles,
            contractArtifact?.RelativePath ?? "",
            planArtifact?.RelativePath ?? "");
    }

    private static (string backend, string embedderModel, string queryKind, int hitCount, List<string> sourceKinds, List<string> sourcePaths, string queryArtifactPath, string resultArtifactPath, string contextArtifactPath, string indexBatchArtifactPath) BuildRetrievalSnapshot(IReadOnlyList<ArtifactRecord> artifacts)
    {
        var contextArtifact = artifacts
            .Where(artifact => string.Equals(artifact.ArtifactType, "coder_context_packet", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(artifact => ParseUtc(FirstNonEmpty(artifact.UpdatedUtc, artifact.CreatedUtc)))
            .FirstOrDefault();
        var context = TryDeserialize<RamCoderContextPacketRecord>(contextArtifact?.Content);

        var resultArtifact = artifacts
            .Where(artifact => string.Equals(artifact.ArtifactType, "retrieval_result", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(artifact => ParseUtc(FirstNonEmpty(artifact.UpdatedUtc, artifact.CreatedUtc)))
            .FirstOrDefault();
        var result = TryDeserialize<RamRetrievalResultRecord>(resultArtifact?.Content);

        var queryArtifact = artifacts
            .Where(artifact => string.Equals(artifact.ArtifactType, "retrieval_query_packet", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(artifact => ParseUtc(FirstNonEmpty(artifact.UpdatedUtc, artifact.CreatedUtc)))
            .FirstOrDefault();
        var query = TryDeserialize<RamRetrievalQueryPacketRecord>(queryArtifact?.Content);

        return (
            FirstNonEmpty(context?.BackendType, result?.BackendType),
            FirstNonEmpty(context?.EmbedderModel, result?.EmbedderModel),
            FirstNonEmpty(context?.QueryKind, query?.QueryKind),
            context?.HitCount ?? result?.HitCount ?? 0,
            context?.SourceKinds ?? result?.SourceKinds ?? [],
            context?.SourcePaths ?? [],
            FirstNonEmpty(context?.QueryArtifactRelativePath, queryArtifact?.RelativePath),
            FirstNonEmpty(context?.RetrievalResultArtifactRelativePath, resultArtifact?.RelativePath),
            FirstNonEmpty(contextArtifact?.RelativePath),
            FirstNonEmpty(context?.IndexBatchArtifactRelativePath, result?.IndexBatchArtifactRelativePath));
    }

    private static string BuildRepairAttemptSummary(IReadOnlyList<ArtifactRecord> artifacts)
    {
        var repairClosure = artifacts
            .Where(artifact => string.Equals(artifact.ArtifactType, "repair_loop_closure", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(artifact => ParseUtc(FirstNonEmpty(artifact.UpdatedUtc, artifact.CreatedUtc)))
            .FirstOrDefault();
        if (repairClosure is not null)
            return repairClosure.Summary;

        var verification = artifacts
            .Where(artifact => string.Equals(artifact.ArtifactType, "verification_result", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(artifact => ParseUtc(FirstNonEmpty(artifact.UpdatedUtc, artifact.CreatedUtc)))
            .FirstOrDefault();
        if (verification is not null)
            return verification.Summary;

        var apply = artifacts
            .Where(artifact => string.Equals(artifact.ArtifactType, "patch_apply_result", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(artifact => ParseUtc(FirstNonEmpty(artifact.UpdatedUtc, artifact.CreatedUtc)))
            .FirstOrDefault();
        if (apply is not null)
            return apply.Summary;

        var draft = artifacts
            .Where(artifact => string.Equals(artifact.ArtifactType, "patch_draft", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(artifact => ParseUtc(FirstNonEmpty(artifact.UpdatedUtc, artifact.CreatedUtc)))
            .FirstOrDefault();
        if (draft is null)
            return "";

        var parsedDraft = TryDeserialize<PatchDraftRecord>(draft.Content);
        if (parsedDraft is not null
            && (!parsedDraft.CanApplyLocally
                || string.Equals(parsedDraft.DraftKind, "inspect_only", StringComparison.OrdinalIgnoreCase)))
        {
            var target = FirstNonEmpty(parsedDraft.TargetFilePath, parsedDraft.TargetProjectPath, "(none)");
            return $"Repair inspection brief only: no deterministic local patch was available for `{target}`; no mutation was applied.";
        }

        return draft.Summary;
    }

    private static SortedSet<string> CollectChangedPaths(IReadOnlyList<ArtifactRecord> artifacts)
    {
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts)
        {
            if (!string.IsNullOrWhiteSpace(artifact.RelativePath)
                && !artifact.RelativePath.StartsWith(".ram/", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(NormalizePath(artifact.RelativePath));
            }

            if (string.Equals(artifact.ArtifactType, "auto_validation_result", StringComparison.OrdinalIgnoreCase))
            {
                var validation = TryDeserialize<AutoValidationResultRecord>(artifact.Content);
                if (validation is not null)
                {
                    foreach (var path in validation.ChangedFilePaths)
                        AddPath(paths, path);
                }
            }

            if (string.Equals(artifact.ArtifactType, "patch_apply_result", StringComparison.OrdinalIgnoreCase))
            {
                var apply = TryDeserialize<PatchApplyResultRecord>(artifact.Content);
                if (apply is not null)
                {
                    AddPath(paths, apply.Draft.TargetFilePath);
                    AddPath(paths, apply.Draft.TargetProjectPath);
                }
            }
        }

        return paths;
    }

    private static string ResolveTerminalCategory(
        TaskboardPlanRunStateRecord runState,
        string finalStatus,
        IReadOnlyList<ArtifactRecord> artifacts)
    {
        var repairedAndResumed = artifacts.Any(artifact =>
            string.Equals(artifact.ArtifactType, "verification_result", StringComparison.OrdinalIgnoreCase)
            && string.Equals(TryDeserialize<VerificationOutcomeRecord>(artifact.Content)?.OutcomeClassification, "verified_fixed", StringComparison.OrdinalIgnoreCase));
        if (repairedAndResumed && string.Equals(finalStatus, "completed", StringComparison.OrdinalIgnoreCase))
            return "repaired_and_resumed_successfully";

        return finalStatus switch
        {
            "completed" => "completed_successfully",
            "blocked" => "blocked_truthful_unresolved_work",
            "paused" => "paused_missing_prerequisite",
            _ => "failed_tool_or_runtime"
        };
    }

    private static string NormalizeFinalStatus(TaskboardPlanRunStateRecord runState, string terminalStatusCode, string selectedBatchId)
    {
        if (string.Equals(terminalStatusCode, "selected_batch_completed", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(runState.PlanStatus.ToString(), nameof(TaskboardPlanRuntimeStatus.Active), StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(selectedBatchId)
                && string.Equals(terminalStatusCode, "completed", StringComparison.OrdinalIgnoreCase)))
        {
            return "completed";
        }

        return runState.PlanStatus switch
        {
            TaskboardPlanRuntimeStatus.Completed => "completed",
            TaskboardPlanRuntimeStatus.Blocked => "blocked",
            TaskboardPlanRuntimeStatus.PausedManualOnly => "paused",
            TaskboardPlanRuntimeStatus.Failed => "failed",
            _ => string.Equals(terminalStatusCode, "completed", StringComparison.OrdinalIgnoreCase)
                ? "completed"
                : "failed"
        };
    }

    private static TaskboardBatchRunStateRecord? ResolveTerminalBatch(
        TaskboardPlanRunStateRecord runState,
        string finalStatus,
        string selectedBatchId)
    {
        if (string.Equals(finalStatus, "completed", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(selectedBatchId))
        {
            return runState.Batches.FirstOrDefault(batch =>
                string.Equals(batch.BatchId, selectedBatchId, StringComparison.OrdinalIgnoreCase));
        }

        var blockerBatch = FindBatchContainingWorkItem(runState, runState.LastBlockerWorkItemId);
        if (blockerBatch is not null)
            return blockerBatch;

        var currentBatch = runState.Batches.FirstOrDefault(batch =>
            string.Equals(batch.BatchId, runState.CurrentBatchId, StringComparison.OrdinalIgnoreCase));
        if (currentBatch is not null)
            return currentBatch;

        var completedBatch = FindBatchContainingWorkItem(runState, runState.LastCompletedWorkItemId);
        if (completedBatch is not null)
            return completedBatch;

        return runState.Batches.OrderByDescending(batch => batch.BatchNumber).FirstOrDefault();
    }

    private static TaskboardWorkItemRunStateRecord? ResolveTerminalWorkItem(
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord? terminalBatch,
        string finalStatus)
    {
        if (terminalBatch is null)
            return null;

        if (!string.IsNullOrWhiteSpace(runState.LastBlockerWorkItemId))
        {
            var blocker = terminalBatch.WorkItems.FirstOrDefault(item =>
                string.Equals(item.WorkItemId, runState.LastBlockerWorkItemId, StringComparison.OrdinalIgnoreCase));
            if (blocker is not null)
                return blocker;
        }

        if (string.Equals(finalStatus, "completed", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(runState.LastCompletedWorkItemId))
        {
            var completed = terminalBatch.WorkItems.FirstOrDefault(item =>
                string.Equals(item.WorkItemId, runState.LastCompletedWorkItemId, StringComparison.OrdinalIgnoreCase));
            if (completed is not null)
                return completed;
        }

        if (!string.IsNullOrWhiteSpace(terminalBatch.CurrentWorkItemId))
        {
            var current = terminalBatch.WorkItems.FirstOrDefault(item =>
                string.Equals(item.WorkItemId, terminalBatch.CurrentWorkItemId, StringComparison.OrdinalIgnoreCase));
            if (current is not null)
                return current;
        }

        return terminalBatch.WorkItems
            .OrderByDescending(item => ParseUtc(item.UpdatedUtc))
            .FirstOrDefault();
    }

    private static TaskboardBatchRunStateRecord? FindBatchContainingWorkItem(TaskboardPlanRunStateRecord runState, string? workItemId)
    {
        if (string.IsNullOrWhiteSpace(workItemId))
            return null;

        return runState.Batches.FirstOrDefault(batch =>
            batch.WorkItems.Any(item => string.Equals(item.WorkItemId, workItemId, StringComparison.OrdinalIgnoreCase)));
    }

    private static string ResolveBlockerLaneKind(TaskboardPlanRunStateRecord runState)
    {
        var lane = runState.LastExecutionGoalResolution?.LaneResolution;
        if (lane is null)
            return "";

        if (!string.IsNullOrWhiteSpace(runState.LastBlockerWorkItemId)
            && !string.Equals(lane.SourceWorkItemId, runState.LastBlockerWorkItemId, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return lane.LaneKind == TaskboardExecutionLaneKind.Unknown
            ? ""
            : lane.LaneKind.ToString().ToLowerInvariant();
    }

    private static string BuildTerminalNote(
        TaskboardPlanRunStateRecord runState,
        string finalStatus,
        string terminalCategory,
        TaskboardBatchRunStateRecord? terminalBatch,
        TaskboardWorkItemRunStateRecord? terminalWorkItem,
        string verificationSummary,
        string repairAttemptSummary)
    {
        if (string.Equals(terminalCategory, "repaired_and_resumed_successfully", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonEmpty(
                verificationSummary,
                repairAttemptSummary,
                $"Repair verification succeeded and the run resumed to {finalStatus}.");
        }

        if (string.Equals(finalStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonEmpty(
                runState.LastResultSummary,
                runState.LastCompletedStepSummary,
                $"Completed through {DisplayValue(terminalBatch?.Title)} / {DisplayValue(terminalWorkItem?.Title)}.");
        }

        if (string.Equals(finalStatus, "blocked", StringComparison.OrdinalIgnoreCase))
            return FirstNonEmpty(runState.LastBlockerReason, runState.LastResultSummary, "Blocked on the current unresolved work item.");

        if (string.Equals(finalStatus, "paused", StringComparison.OrdinalIgnoreCase))
            return FirstNonEmpty(runState.LastBlockerReason, "Paused awaiting a deterministic prerequisite or manual boundary.");

        return FirstNonEmpty(
            runState.LastFailureNormalizedSummary,
            runState.LastResultSummary,
            runState.LastBlockerReason,
            "Execution failed on the current work item.");
    }

    private static void AddPath(ISet<string> paths, string? value)
    {
        var normalized = NormalizePath(value);
        if (!string.IsNullOrWhiteSpace(normalized))
            paths.Add(normalized);
    }

    private static string NormalizePath(string? value)
    {
        return (value ?? "").Replace('\\', '/').Trim().Trim('"');
    }

    private static DateTime ParseUtc(string? value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.MinValue;
    }

    private static T? TryDeserialize<T>(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(content);
        }
        catch
        {
            return default;
        }
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

    private static bool ContainsIgnoreCase(string? value, string fragment)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !string.IsNullOrWhiteSpace(fragment)
            && value.Contains(fragment, StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
