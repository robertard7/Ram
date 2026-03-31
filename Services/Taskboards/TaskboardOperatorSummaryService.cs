using System.Text;
using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardOperatorSummaryService
{
    private readonly Func<string, string, string, CancellationToken, Task<string>> _generateAsync;

    public TaskboardOperatorSummaryService(Func<string, string, string, CancellationToken, Task<string>>? generateAsync = null)
    {
        _generateAsync = generateAsync
            ?? ((endpoint, model, prompt, cancellationToken) => new OllamaClient().GenerateAsync(endpoint, model, prompt, cancellationToken));
    }

    public TaskboardOperatorSummaryPacket? BuildPacket(TaskboardRunTerminalSummaryRecord? summary)
    {
        if (summary is null || string.IsNullOrWhiteSpace(summary.SummaryId))
            return null;

        var artifactPaths = new List<string>();
        AddArtifactPath(artifactPaths, summary.SummaryArtifactRelativePath);
        AddArtifactPath(artifactPaths, summary.NormalizedRunArtifactRelativePath);
        AddArtifactPath(artifactPaths, summary.IndexExportArtifactRelativePath);
        AddArtifactPath(artifactPaths, summary.CorpusExportArtifactRelativePath);
        AddArtifactPath(artifactPaths, summary.PatchContractArtifactRelativePath);
        AddArtifactPath(artifactPaths, summary.PatchPlanArtifactRelativePath);
        AddArtifactPath(artifactPaths, summary.RetrievalContextPacketArtifactRelativePath);
        AddArtifactPath(artifactPaths, summary.RetrievalResultArtifactRelativePath);

        var evidenceLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary.LastVerificationOutcome))
        {
            evidenceLines.Add(string.IsNullOrWhiteSpace(summary.LastVerificationTarget)
                ? $"Verification: {summary.LastVerificationOutcome}"
                : $"Verification: {summary.LastVerificationOutcome} on {summary.LastVerificationTarget}");
        }

        if (!string.IsNullOrWhiteSpace(summary.PreferredCompletionProofTemplateId))
            evidenceLines.Add($"Completion proof: {summary.PreferredCompletionProofTemplateId}");
        if (summary.PreferredCompletionProofToolNames.Count > 0)
            evidenceLines.Add($"Completion proof tools: {string.Join(", ", summary.PreferredCompletionProofToolNames)}");
        if (!string.IsNullOrWhiteSpace(summary.PreferredCompletionProofReason))
            evidenceLines.Add(summary.PreferredCompletionProofReason);
        if (!string.IsNullOrWhiteSpace(summary.LastSuccessfulChainTemplateId)
            && !string.Equals(summary.LastSuccessfulChainTemplateId, summary.PreferredCompletionProofTemplateId, StringComparison.OrdinalIgnoreCase))
        {
            evidenceLines.Add($"Latest successful chain: {summary.LastSuccessfulChainTemplateId}");
        }
        if (summary.LastVerificationWarningCount > 0 || summary.LastVerificationWarningCodes.Count > 0)
        {
            evidenceLines.Add(
                $"Warnings tracked: {summary.LastVerificationWarningCount}"
                + (summary.LastVerificationWarningCodes.Count == 0 ? "" : $" ({string.Join(", ", summary.LastVerificationWarningCodes)})"));
        }

        var baselineAuthority = BuildBaselineAuthority(summary);
        var guardState = FirstNonEmpty(summary.MaintenanceGuardSummary);
        var protectionSummary = BuildProtectionSummary(summary);
        var terminalWork = BuildTerminalWorkText(summary);
        var whatHappenedFacts = BuildWhatHappenedFacts(summary, terminalWork);
        var blockerIdentity = BuildBlockerIdentity(summary);
        var failureIdentity = BuildFailureIdentity(summary);
        var nextAction = BuildNextDeterministicAction(summary);
        var headline = BuildHeadline(summary, terminalWork);
        var progressText = $"Batches {summary.CompletedBatchCount}/{summary.TotalBatchCount}; work items {summary.CompletedWorkItemCount}/{summary.TotalWorkItemCount}.";

        return new TaskboardOperatorSummaryPacket
        {
            SummaryId = summary.SummaryId,
            TerminalFingerprint = FirstNonEmpty(summary.TerminalFingerprint, summary.SummaryId),
            PlanTitle = summary.PlanTitle,
            ActionName = summary.ActionName,
            FinalStatus = summary.FinalStatus,
            TerminalCategory = summary.TerminalCategory,
            Headline = headline,
            ProgressText = progressText,
            TerminalWorkText = terminalWork,
            WhatHappenedFacts = whatHappenedFacts,
            BlockerIdentity = blockerIdentity,
            BlockerReason = summary.BlockerReason,
            FailureIdentity = failureIdentity,
            VerificationResult = BuildVerificationResult(summary),
            BaselineAuthority = baselineAuthority,
            GuardState = guardState,
            ProtectionSummary = protectionSummary,
            NextDeterministicAction = nextAction,
            TerminalNote = summary.TerminalNote,
            EvidenceLines = evidenceLines,
            ArtifactPaths = artifactPaths,
            TraceabilityText = BuildTraceabilityText(summary)
        };
    }

    public string BuildDeterministicSummaryText(TaskboardOperatorSummaryPacket? packet)
    {
        if (packet is null || string.IsNullOrWhiteSpace(packet.SummaryId))
            return "A deterministic run summary will appear after the current run reaches a truthful terminal state.";

        var lines = new List<string>
        {
            packet.Headline
        };

        AppendSection(lines, "What happened", BuildSectionLines(packet.WhatHappenedFacts, packet.ProgressText, packet.TerminalWorkText));
        AppendSection(lines, "Why it stopped", BuildSectionLines(packet.BlockerIdentity, packet.BlockerReason, packet.FailureIdentity, packet.VerificationResult, packet.TerminalNote));
        AppendSection(lines, "Bounds and protection", BuildSectionLines(packet.BaselineAuthority, packet.GuardState, packet.ProtectionSummary));
        AppendSection(lines, "What happens next", BuildSectionLines(packet.NextDeterministicAction));
        AppendSection(lines, "Evidence", BuildSectionLines(packet.EvidenceLines));
        AppendSection(lines, "Artifacts", packet.ArtifactPaths.Select(FormatBreakFriendlyPath));
        AppendSection(lines, "Traceability", BuildSectionLines(packet.TraceabilityText));

        return string.Join(Environment.NewLine, lines);
    }

    public async Task<TaskboardOperatorSummaryRenderResult> RenderAsync(
        TaskboardOperatorSummaryPacket? packet,
        string endpoint,
        string intakeModel,
        CancellationToken cancellationToken = default)
    {
        if (packet is null || string.IsNullOrWhiteSpace(packet.SummaryId))
        {
            return new TaskboardOperatorSummaryRenderResult
            {
                RenderedText = "",
                ModeLabel = "Summary mode: (pending)",
                UsedFallback = true,
                FallbackReason = "No terminal summary packet was available."
            };
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return BuildFallback(packet, "Intake-model rendering skipped because no endpoint is configured.");
        }

        if (string.IsNullOrWhiteSpace(intakeModel))
        {
            return BuildFallback(packet, "Intake-model rendering skipped because no intake model is configured.");
        }

        try
        {
            var prompt = BuildPrompt(packet);
            var rawText = await _generateAsync(endpoint, intakeModel, prompt, cancellationToken);
            if (!TryParseResponse(rawText, out var response, out var failureReason))
                return BuildFallback(packet, failureReason);

            return new TaskboardOperatorSummaryRenderResult
            {
                RenderedText = BuildRenderedText(packet, response),
                ModeLabel = $"Summary mode: operator summary (intake model: {intakeModel})",
                ModelAttempted = true
            };
        }
        catch (Exception ex)
        {
            return BuildFallback(packet, $"Intake-model rendering failed: {ex.Message}");
        }
    }

    private TaskboardOperatorSummaryRenderResult BuildFallback(TaskboardOperatorSummaryPacket packet, string reason)
    {
        return new TaskboardOperatorSummaryRenderResult
        {
            RenderedText = BuildDeterministicSummaryText(packet),
            ModeLabel = "Summary mode: operator summary (deterministic fallback)",
            UsedFallback = true,
            ModelAttempted = !string.IsNullOrWhiteSpace(reason),
            FallbackReason = reason
        };
    }

    private string BuildRenderedText(TaskboardOperatorSummaryPacket packet, TaskboardOperatorSummaryModelResponse response)
    {
        var lines = new List<string>
        {
            packet.Headline
        };

        AppendSection(lines, "What happened", BuildSectionLines(response.WhatHappened, packet.ProgressText, packet.TerminalWorkText));
        AppendSection(lines, "Why it stopped", BuildSectionLines(response.WhyItStopped, packet.BlockerIdentity, packet.BlockerReason, packet.FailureIdentity, packet.VerificationResult));
        AppendSection(lines, "Bounds and protection", BuildSectionLines(response.Protections, packet.BaselineAuthority, packet.GuardState, packet.ProtectionSummary));
        AppendSection(lines, "What happens next", BuildSectionLines(response.NextStep, packet.NextDeterministicAction));
        AppendSection(lines, "Evidence", BuildSectionLines(packet.EvidenceLines));
        AppendSection(lines, "Artifacts", packet.ArtifactPaths.Select(FormatBreakFriendlyPath));
        AppendSection(lines, "Traceability", BuildSectionLines(packet.TraceabilityText));

        return string.Join(Environment.NewLine, lines);
    }

    private string BuildPrompt(TaskboardOperatorSummaryPacket packet)
    {
        var packetJson = JsonSerializer.Serialize(packet, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        return $$"""
You are formatting a terminal operator summary for a debugging shell.

Rules:
- Use only the facts in the JSON packet.
- Do not invent causes, files, blockers, guards, baselines, next steps, or outcomes.
- Do not change the final status, blocker identity, guard identity, or baseline identity.
- If a section has no supporting facts, return an empty string for that field.
- Keep each string concise and readable for an operator.
- Return JSON only. No markdown fences.

Required JSON schema:
{
  "what_happened": "string <= 220 chars",
  "why_it_stopped": "string <= 220 chars",
  "protections": "string <= 220 chars",
  "next_step": "string <= 220 chars"
}

Terminal summary packet:
{{packetJson}}
""";
    }

    private static bool TryParseResponse(string rawText, out TaskboardOperatorSummaryModelResponse response, out string failureReason)
    {
        response = new TaskboardOperatorSummaryModelResponse();
        failureReason = "";

        var validation = AgentValidationHelpers.ParseRootObject(rawText);
        if (!validation.IsValid)
        {
            failureReason = validation.Message;
            return false;
        }

        using var document = JsonDocument.Parse(rawText);
        var root = document.RootElement;
        validation = AgentValidationHelpers.EnsureOnlyKnownProperties(root, "what_happened", "why_it_stopped", "protections", "next_step");
        if (!validation.IsValid)
        {
            failureReason = validation.Message;
            return false;
        }

        if (!TryGetOptionalString(root, "what_happened", 220, out var whatHappened, out failureReason)
            || !TryGetOptionalString(root, "why_it_stopped", 220, out var whyItStopped, out failureReason)
            || !TryGetOptionalString(root, "protections", 220, out var protections, out failureReason)
            || !TryGetOptionalString(root, "next_step", 220, out var nextStep, out failureReason))
        {
            return false;
        }

        response = new TaskboardOperatorSummaryModelResponse
        {
            WhatHappened = whatHappened,
            WhyItStopped = whyItStopped,
            Protections = protections,
            NextStep = nextStep
        };
        return true;
    }

    private static bool TryGetOptionalString(JsonElement root, string propertyName, int maxLength, out string value, out string failureReason)
    {
        value = "";
        failureReason = "";
        if (!root.TryGetProperty(propertyName, out var property))
            return true;

        if (property.ValueKind != JsonValueKind.String)
        {
            failureReason = $"Field `{propertyName}` was invalid.";
            return false;
        }

        value = property.GetString() ?? "";
        if (value.Length > maxLength)
        {
            failureReason = $"Field `{propertyName}` exceeded the {maxLength}-character limit.";
            return false;
        }

        if (AgentValidationHelpers.ContainsForbiddenNarrative(value))
        {
            failureReason = $"Field `{propertyName}` contained forbidden tool-style text.";
            return false;
        }

        return true;
    }

    private static IEnumerable<string> BuildSectionLines(params string?[] values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim());
    }

    private static IEnumerable<string> BuildSectionLines(IEnumerable<string>? values)
    {
        return values?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()) ?? [];
    }

    private static void AppendSection(List<string> lines, string heading, IEnumerable<string> bodyLines)
    {
        var sectionLines = bodyLines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (sectionLines.Count == 0)
            return;

        lines.Add("");
        lines.Add(heading);
        foreach (var line in sectionLines)
            lines.Add(line);
    }

    private static string BuildHeadline(TaskboardRunTerminalSummaryRecord summary, string terminalWork)
    {
        var status = Capitalize(FirstNonEmpty(summary.FinalStatus, "unknown"));
        if (!string.IsNullOrWhiteSpace(summary.BlockerReason))
            return $"{status}: {terminalWork}";

        return string.IsNullOrWhiteSpace(terminalWork)
            ? $"{status}: {summary.PlanTitle}"
            : $"{status}: {terminalWork}";
    }

    private static string BuildTerminalWorkText(TaskboardRunTerminalSummaryRecord summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.TerminalBatchTitle) && !string.IsNullOrWhiteSpace(summary.TerminalWorkItemTitle))
            return $"{summary.TerminalBatchTitle} -> {summary.TerminalWorkItemTitle}";
        if (!string.IsNullOrWhiteSpace(summary.TerminalWorkItemTitle))
            return summary.TerminalWorkItemTitle;
        if (!string.IsNullOrWhiteSpace(summary.TerminalBatchTitle))
            return summary.TerminalBatchTitle;
        return summary.PlanTitle;
    }

    private static string BuildWhatHappenedFacts(TaskboardRunTerminalSummaryRecord summary, string terminalWork)
    {
        var facts = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary.ActionName) || !string.IsNullOrWhiteSpace(summary.PlanTitle))
        {
            facts.Add($"RAM ran {FirstNonEmpty(summary.ActionName, "the active plan")} for {FirstNonEmpty(summary.PlanTitle, "the current taskboard")}.");
        }

        if (!string.IsNullOrWhiteSpace(terminalWork))
            facts.Add($"The terminal work was {terminalWork}.");

        return string.Join(" ", facts.Where(fact => !string.IsNullOrWhiteSpace(fact)));
    }

    private static string BuildBlockerIdentity(TaskboardRunTerminalSummaryRecord summary)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary.BlockerWorkFamily))
            parts.Add($"family={summary.BlockerWorkFamily}");
        if (!string.IsNullOrWhiteSpace(summary.BlockerPhraseFamily))
            parts.Add($"phrase={summary.BlockerPhraseFamily}");
        if (!string.IsNullOrWhiteSpace(summary.BlockerOperationKind))
            parts.Add($"operation={summary.BlockerOperationKind}");
        if (!string.IsNullOrWhiteSpace(summary.BlockerLaneKind))
            parts.Add($"lane={summary.BlockerLaneKind}");

        return parts.Count == 0 ? "" : $"Blocker identity: {string.Join(", ", parts)}.";
    }

    private static string BuildFailureIdentity(TaskboardRunTerminalSummaryRecord summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.LastVerificationOutcome))
            return "";

        return summary.FinalStatus switch
        {
            "failed" => FirstNonEmpty(summary.BlockerReason, summary.TerminalNote),
            _ => ""
        };
    }

    private static string BuildVerificationResult(TaskboardRunTerminalSummaryRecord summary)
    {
        if (string.IsNullOrWhiteSpace(summary.LastVerificationOutcome))
            return "";

        return string.IsNullOrWhiteSpace(summary.LastVerificationTarget)
            ? $"Verification result: {summary.LastVerificationOutcome}."
            : $"Verification result: {summary.LastVerificationOutcome} on {summary.LastVerificationTarget}.";
    }

    private static string BuildBaselineAuthority(TaskboardRunTerminalSummaryRecord summary)
    {
        if (string.IsNullOrWhiteSpace(summary.MaintenanceBaselineSolutionPath) && summary.MaintenanceAllowedRoots.Count == 0)
            return "";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary.MaintenanceBaselineSolutionPath))
            parts.Add($"solution {summary.MaintenanceBaselineSolutionPath}");
        if (summary.MaintenanceAllowedRoots.Count > 0)
            parts.Add($"allowed roots {string.Join(", ", summary.MaintenanceAllowedRoots)}");

        return parts.Count == 0 ? "" : $"Baseline authority: {string.Join("; ", parts)}.";
    }

    private static string BuildProtectionSummary(TaskboardRunTerminalSummaryRecord summary)
    {
        var parts = new List<string>();
        if (summary.MaintenanceExcludedRoots.Count > 0)
            parts.Add($"Excluded generated roots: {string.Join(", ", summary.MaintenanceExcludedRoots)}.");
        if (!string.IsNullOrWhiteSpace(summary.PatchAllowedEditScope))
            parts.Add($"Patch scope stayed inside {summary.PatchAllowedEditScope}.");
        if (summary.RetrievalHitCount > 0 && !string.IsNullOrWhiteSpace(summary.RetrievalBackend))
            parts.Add($"Retrieval stayed on {summary.RetrievalBackend} before mutation.");
        if (summary.SatisfactionSkipCount > 0)
            parts.Add($"State-satisfaction skips remained active with {summary.SatisfactionSkipCount} skipped step(s).");

        return string.Join(" ", parts);
    }

    private static string BuildNextDeterministicAction(TaskboardRunTerminalSummaryRecord summary)
    {
        if (string.Equals(summary.FinalStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return summary.PreferredCompletionProofStrongerBehaviorMissing || summary.PreferredCompletionProofInsufficiencyReasons.Count > 0
                ? $"Further bounded follow-up is still required because completion proof remains insufficient: {string.Join(", ", summary.PreferredCompletionProofInsufficiencyReasons.DefaultIfEmpty("stronger_behavior_proof_missing"))}."
                : "No further action is required for this run.";
        }

        if (!string.IsNullOrWhiteSpace(summary.MaintenanceGuardSummary))
            return "Resolve the active maintenance guard condition before retrying the blocked step.";

        if (!string.IsNullOrWhiteSpace(summary.BlockerOperationKind) && !string.IsNullOrWhiteSpace(summary.TerminalWorkItemTitle))
            return $"Resume through {summary.BlockerOperationKind} for {summary.TerminalWorkItemTitle}.";

        if (!string.IsNullOrWhiteSpace(summary.TerminalNote))
            return summary.TerminalNote;

        return "";
    }

    private static string BuildTraceabilityText(TaskboardRunTerminalSummaryRecord summary)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary.RunStateId))
            parts.Add($"run_state={summary.RunStateId}");
        if (!string.IsNullOrWhiteSpace(summary.SummaryArtifactRelativePath))
            parts.Add($"summary={summary.SummaryArtifactRelativePath}");
        if (!string.IsNullOrWhiteSpace(summary.NormalizedRunArtifactRelativePath))
            parts.Add($"run_record={summary.NormalizedRunArtifactRelativePath}");
        return string.Join(" | ", parts);
    }

    private static void AddArtifactPath(List<string> paths, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            paths.Add(value.Trim());
    }

    private static string FormatBreakFriendlyPath(string value)
    {
        return value
            .Replace("\\", "\\\u200B", StringComparison.Ordinal)
            .Replace("/", "/\u200B", StringComparison.Ordinal)
            .Replace(", ", ",\u200B ", StringComparison.Ordinal)
            .Trim();
    }

    private static string Capitalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private sealed class TaskboardOperatorSummaryModelResponse
    {
        public string WhatHappened { get; set; } = "";
        public string WhyItStopped { get; set; } = "";
        public string Protections { get; set; } = "";
        public string NextStep { get; set; } = "";
    }
}
