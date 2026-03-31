using RAM.Models;
using RAM.Services;
using System.Text.Json;

namespace RAM;

public partial class MainWindow
{
    private async Task<ToolChainRunResult> ExecuteControlledToolChainAsync(
        ToolRequest initialRequest,
        string source,
        string userPrompt,
        bool allowModelSummary,
        ResponseMode responseMode,
        Func<TaskboardLiveProgressUpdate, Task>? liveProgressObserver = null,
        UiExecutionContextSnapshot? uiExecutionContext = null)
    {
        var workspaceRoot = _workspaceService.HasWorkspace() ? _workspaceService.WorkspaceRoot : "";
        var effectiveUiExecutionContext = uiExecutionContext ?? CaptureUiExecutionContextSnapshot(nameof(ExecuteControlledToolChainAsync));
        var chain = _toolChainControllerService.StartChain(
            workspaceRoot,
            userPrompt,
            initialRequest,
            allowModelSummary,
            responseMode);
        var template = _toolChainControllerService.GetTemplate(chain);

        AppendOutput(
            $"Tool chain started: id={chain.ChainId} type={FormatChainType(chain.ChainType)} template={chain.SelectedTemplateName} response_mode={FormatResponseMode(chain.ResponseMode)} max_steps={template.MaxStepCount}");
        if (liveProgressObserver is not null)
        {
            await liveProgressObserver(new TaskboardLiveProgressUpdate
            {
                PhaseCode = "starting_chain",
                PhaseText = $"Starting chain `{chain.SelectedTemplateName}`.",
                EventKind = "chain_started",
                ActivitySummary = $"Started chain {DisplayChainName(chain.SelectedTemplateName)}.",
                ChainTemplateId = chain.SelectedTemplateName
            });
        }

        ToolRequest? previousRequest = null;
        ToolResult? lastResult = null;
        var stopReason = ToolChainStopReason.Unknown;
        var stopSummary = "";

        while (true)
        {
            var nextRequest = _toolChainControllerService.GetNextStepRequest(
                chain,
                initialRequest,
                previousRequest,
                lastResult,
                workspaceRoot,
                _ramDbService,
                out stopReason,
                out stopSummary);

            if (nextRequest is null)
                break;

            var validation = _toolChainControllerService.ValidateNextStep(chain, nextRequest, previousRequest);
            if (!validation.Allowed)
            {
                stopReason = ToolChainStopReason.PolicyBlockedNextStep;
                stopSummary = validation.Message;
                _toolChainControllerService.RecordBlockedStep(chain, nextRequest, stopReason, stopSummary, validation);
                AppendOutput(stopSummary);
                if (liveProgressObserver is not null)
                {
                    await liveProgressObserver(new TaskboardLiveProgressUpdate
                    {
                        PhaseCode = "blocked",
                        PhaseText = "Blocked while validating the next chain step.",
                        EventKind = "chain_step_blocked",
                        ActivitySummary = stopSummary,
                        ToolName = nextRequest.ToolName,
                        ChainTemplateId = chain.SelectedTemplateName
                    });
                }
                break;
            }

            var stepSource = BuildChainStepSourceLabel(source, chain.Steps.Count + 1);
            if (liveProgressObserver is not null)
            {
                await liveProgressObserver(new TaskboardLiveProgressUpdate
                {
                    PhaseCode = "executing_step",
                    PhaseText = $"Executing `{nextRequest.ToolName}`.",
                    EventKind = "chain_step_started",
                    ActivitySummary = BuildToolStepStartSummary(nextRequest),
                    ToolName = nextRequest.ToolName,
                    ChainTemplateId = chain.SelectedTemplateName
                });
            }
            var beforeTraceIds = CaptureExecutionTraceIds();
            lastResult = ExecuteToolRequest(nextRequest, stepSource, userPrompt);
            var stepFacts = BuildChainStepFacts(beforeTraceIds, stepSource, workspaceRoot, lastResult);
            _toolChainControllerService.RecordExecutedStep(
                chain,
                nextRequest,
                lastResult,
                stepFacts.ExecutionAttempted,
                stepFacts.ExecutionBlockedReason,
                stepFacts.MutationObserved,
                stepFacts.TouchedFilePaths,
                stepFacts.LinkedArtifactPaths,
                validation);
            if (liveProgressObserver is not null)
            {
                await liveProgressObserver(new TaskboardLiveProgressUpdate
                {
                    PhaseCode = lastResult.Success ? "validating_result" : "blocked",
                    PhaseText = lastResult.Success
                        ? $"Completed `{nextRequest.ToolName}`."
                        : $"`{nextRequest.ToolName}` reported a failure.",
                    EventKind = lastResult.Success ? "chain_step_completed" : "chain_step_failed",
                    ActivitySummary = BuildToolStepCompletedSummary(nextRequest, lastResult),
                    ToolName = nextRequest.ToolName,
                    ChainTemplateId = chain.SelectedTemplateName
                });
            }

            previousRequest = nextRequest;
        }

        _toolChainControllerService.FinalizeChain(chain, stopReason, lastResult, workspaceRoot, _ramDbService);
        var summary = await BuildToolChainSummaryAsync(chain, effectiveUiExecutionContext);

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var chainArtifact = _toolChainControllerService.SaveChainArtifact(_ramDbService, workspaceRoot, chain);
            var summaryArtifact = _toolChainControllerService.SaveSummaryArtifact(_ramDbService, workspaceRoot, chain, summary);
            var contractArtifact = _toolChainControllerService.SaveChainContractArtifact(_ramDbService, workspaceRoot, chain);
            var rejectionArtifact = _toolChainControllerService.SaveChainRejectionArtifact(_ramDbService, workspaceRoot, chain);
            AppendOutput($"Tool chain recorded: {chainArtifact.RelativePath}");
            AppendOutput($"Tool chain summary recorded: {summaryArtifact.RelativePath}");
            AppendOutput($"Tool chain contract recorded: {contractArtifact.RelativePath}");
            if (rejectionArtifact is not null)
                AppendOutput($"Tool chain rejection recorded: {rejectionArtifact.RelativePath}");
            AppendPendingDatabaseMessages();
        }

        AppendOutput(
            $"Tool chain completed: id={chain.ChainId} steps={chain.Steps.Count} stop={FormatStopReason(chain.StopReason)} summary={(chain.ModelSummaryUsed ? "model" : "fallback")}");
        if (liveProgressObserver is not null)
        {
            await liveProgressObserver(new TaskboardLiveProgressUpdate
            {
                PhaseCode = chain.StopReason == ToolChainStopReason.GoalCompleted ? "validating_result" : "reconciling_followup",
                PhaseText = chain.StopReason == ToolChainStopReason.GoalCompleted
                    ? "Chain completed successfully."
                    : $"Chain stopped with {FormatStopReason(chain.StopReason)}.",
                EventKind = chain.StopReason == ToolChainStopReason.GoalCompleted ? "chain_completed" : "chain_stopped",
                ActivitySummary = ToSingleLineSummary(FirstNonEmpty(chain.FinalOutcomeSummary, summary, "Controlled chain completed.")),
                ChainTemplateId = chain.SelectedTemplateName
            });
        }

        return new ToolChainRunResult
        {
            Chain = chain,
            LastResult = lastResult,
            Summary = summary
        };
    }

    private async Task<string> BuildToolChainSummaryAsync(
        ToolChainRecord chain,
        UiExecutionContextSnapshot? uiExecutionContext = null)
    {
        var effectiveUiExecutionContext = uiExecutionContext ?? CaptureUiExecutionContextSnapshot(nameof(BuildToolChainSummaryAsync));
        var input = _toolChainControllerService.BuildSummaryInput(chain);
        var suggestionPresentation = await _suggestionAgentService.PresentAsync(
            effectiveUiExecutionContext.Endpoint,
            effectiveUiExecutionContext.SelectedModel,
            _settings,
            chain.WorkspaceRoot,
            chain.ChainId,
            input.ActionableSuggestions,
            chain.ModelSummaryRequested);
        AppendOutput(DescribeSuggestionAgentResult(suggestionPresentation));

        var deterministicSummarySection = _toolChainSummaryService.BuildDeterministicSummarySection(input);
        var fallbackSummary = _toolChainSummaryService.BuildDeterministicFallbackSummary(input, suggestionPresentation);
        var summaryPresentation = await _summaryAgentService.FormatAsync(
            effectiveUiExecutionContext.Endpoint,
            effectiveUiExecutionContext.SelectedModel,
            _settings,
            chain.WorkspaceRoot,
            input,
            suggestionPresentation,
            chain.ModelSummaryRequested,
            deterministicSummarySection);
        AppendOutput(DescribeSummaryAgentResult(summaryPresentation));

        if (summaryPresentation.Accepted)
        {
            chain.ModelSummaryUsed = true;
            return _toolChainSummaryService.RenderSummary(input, summaryPresentation.Payload, suggestionPresentation);
        }

        chain.ModelSummaryRejectionReason = FirstNonEmpty(
            summaryPresentation.Invocation.Validation.Message,
            summaryPresentation.Invocation.RejectionReason == AgentRejectionReason.None
                ? summaryPresentation.SkipReason
                : summaryPresentation.Invocation.RejectionReason.ToString());
        chain.FallbackSummaryUsed = true;
        return fallbackSummary;
    }

    private static HashSet<string> CaptureExecutionTraceIds()
    {
        return ExecutionTraceService.Snapshot()
            .Select(item => item.EventId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static ToolChainStepFacts BuildChainStepFacts(
        HashSet<string> beforeTraceIds,
        string sourceName,
        string workspaceRoot,
        ToolResult result)
    {
        var includeAutoValidationEvents = result.ToolName is "write_file"
            or "replace_in_file"
            or "save_output"
            or "apply_patch_draft";
        var newEvents = ExecutionTraceService.Snapshot()
            .Where(item =>
                !beforeTraceIds.Contains(item.EventId)
                && (string.IsNullOrWhiteSpace(workspaceRoot)
                    || string.Equals(item.WorkspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase))
                && (string.Equals(item.SourceName, sourceName, StringComparison.OrdinalIgnoreCase)
                    || (includeAutoValidationEvents
                        && string.Equals(item.SourceType, "auto_validation", StringComparison.OrdinalIgnoreCase))))
            .ToList();

        var executionAttempted = newEvents.Any(item =>
            string.Equals(item.EventKind, "execution_attempted", StringComparison.OrdinalIgnoreCase));
        var executionBlockedReason = newEvents
            .FirstOrDefault(item => string.Equals(item.EventKind, "execution_blocked", StringComparison.OrdinalIgnoreCase))
            ?.Message;

        if (!executionAttempted
            && !string.IsNullOrWhiteSpace(result.ToolName)
            && (result.Success
                || !string.IsNullOrWhiteSpace(result.OutcomeType)
                || !string.IsNullOrWhiteSpace(result.Summary)
                || !string.IsNullOrWhiteSpace(result.ErrorMessage)
                || !string.IsNullOrWhiteSpace(result.Output)))
        {
            executionAttempted = true;
        }

        if (string.IsNullOrWhiteSpace(executionBlockedReason)
            && result.OutcomeType is "manual_only" or "scope_blocked" or "safety_blocked" or "execution_gate_blocked")
        {
            executionBlockedReason = FirstNonEmpty(result.Summary, result.ErrorMessage);
        }

        return new ToolChainStepFacts
        {
            ExecutionAttempted = executionAttempted,
            ExecutionBlockedReason = executionBlockedReason ?? "",
            MutationObserved = IsMutationObserved(result),
            TouchedFilePaths = ExtractTouchedFilePaths(result, ExtractLinkedArtifactPaths(result)),
            LinkedArtifactPaths = ExtractLinkedArtifactPaths(result)
        };
    }

    private static List<string> ExtractLinkedArtifactPaths(ToolResult result)
    {
        var text = FirstNonEmpty(result.Output, result.ErrorMessage);
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text.Split(Environment.NewLine)
            .Where(line => line.StartsWith("Artifact synced:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["Artifact synced:".Length..].Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsMutationObserved(ToolResult result)
    {
        if (result is null || !result.Success)
            return false;

        return result.ToolName switch
        {
            "write_file"
                or "replace_in_file"
                or "append_file"
                or "create_file"
                or "apply_patch_draft"
                or "create_dotnet_page_view"
                or "create_dotnet_viewmodel"
                or "register_navigation"
                or "register_di_service"
                or "initialize_sqlite_storage_boundary"
                or "create_cmake_project"
                or "create_cpp_source_file"
                or "create_cpp_header_file"
                or "create_c_source_file"
                or "create_c_header_file" => true,
            _ => false
        };
    }

    private static List<string> ExtractTouchedFilePaths(ToolResult result, IReadOnlyList<string> linkedArtifactPaths)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in linkedArtifactPaths)
        {
            var normalized = NormalizeRuntimePath(path);
            if (!string.IsNullOrWhiteSpace(normalized) && !normalized.StartsWith(".ram/", StringComparison.OrdinalIgnoreCase))
                paths.Add(normalized);
        }

        if (string.IsNullOrWhiteSpace(result?.StructuredDataJson))
            return paths.ToList();

        try
        {
            using var document = JsonDocument.Parse(result.StructuredDataJson);
            AddTargetPath(document.RootElement, "draft", "TargetFilePath", paths);
            AddTargetPath(document.RootElement, "proposal", "TargetFilePath", paths);
            AddTargetPath(document.RootElement, "file_artifact", "RelativePath", paths);
        }
        catch
        {
        }

        return paths.ToList();
    }

    private static void AddTargetPath(JsonElement root, string sectionName, string propertyName, ISet<string> paths)
    {
        if (!root.TryGetProperty(sectionName, out var section))
            return;

        if (!section.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return;

        var normalized = NormalizeRuntimePath(property.GetString());
        if (!string.IsNullOrWhiteSpace(normalized) && !normalized.StartsWith(".ram/", StringComparison.OrdinalIgnoreCase))
            paths.Add(normalized);
    }

    private static string NormalizeRuntimePath(string? value)
    {
        return (value ?? "")
            .Replace('\\', '/')
            .Trim()
            .Trim('"');
    }

    private static string BuildChainStepSourceLabel(string source, int stepIndex)
    {
        return $"Controlled chain step {stepIndex}";
    }

    private static string BuildToolStepStartSummary(ToolRequest request)
    {
        var path = FirstNonEmpty(
            ReadArgument(request, "path"),
            ReadArgument(request, "project"),
            ReadArgument(request, "solution_path"),
            ReadArgument(request, "project_path"));
        var summary = string.IsNullOrWhiteSpace(path)
            ? $"Started {request.ToolName}."
            : $"Started {request.ToolName} for {path}.";
        return ToSingleLineSummary(summary);
    }

    private static string BuildToolStepCompletedSummary(ToolRequest request, ToolResult result)
    {
        return ToSingleLineSummary(FirstNonEmpty(
            result.Summary,
            result.ErrorMessage,
            result.Output,
            $"{request.ToolName} {(result.Success ? "completed" : "failed")}."));
    }

    private static string FormatChainType(ToolChainType chainType)
    {
        return chainType switch
        {
            ToolChainType.FileEdit => "file_edit",
            ToolChainType.Repair => "repair",
            ToolChainType.Build => "build",
            ToolChainType.Verification => "verification",
            ToolChainType.ArtifactInspection => "artifact_inspection",
            ToolChainType.AutoValidation => "auto_validation",
            ToolChainType.CustomControlled => "custom_controlled",
            _ => "none"
        };
    }

    private static string FormatStopReason(ToolChainStopReason stopReason)
    {
        return stopReason switch
        {
            ToolChainStopReason.GoalCompleted => "goal_completed",
            ToolChainStopReason.PolicyBlockedNextStep => "policy_blocked_next_step",
            ToolChainStopReason.ManualOnly => "manual_only",
            ToolChainStopReason.SafetyBlocked => "safety_blocked",
            ToolChainStopReason.ScopeBlocked => "scope_blocked",
            ToolChainStopReason.ToolFailed => "tool_failed",
            ToolChainStopReason.InvalidModelStep => "invalid_model_step",
            ToolChainStopReason.ChainLimitReached => "chain_limit_reached",
            ToolChainStopReason.NoFurtherStepAllowed => "no_further_step_allowed",
            _ => "unknown"
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

    private static string DescribeSuggestionAgentResult(SuggestionPresentationResult result)
    {
        if (result.Accepted)
            return $"Suggestion Agent: accepted trace={result.TraceId}.";

        if (result.Skipped)
            return $"Suggestion Agent: skipped. {result.SkipReason}";

        return $"Suggestion Agent: rejected or failed trace={result.TraceId}. Falling back to deterministic suggestion groups.";
    }

    private static string DescribeSummaryAgentResult(SummaryPresentationResult result)
    {
        if (result.Accepted)
            return $"Summary Agent: accepted trace={result.TraceId}.";

        if (result.Skipped)
            return $"Summary Agent: skipped. {result.SkipReason}";

        return $"Summary Agent: rejected or failed trace={result.TraceId}. Falling back to deterministic summary section.";
    }

    private sealed class ToolChainRunResult
    {
        public ToolChainRecord Chain { get; init; } = new();
        public ToolResult? LastResult { get; init; }
        public string Summary { get; init; } = "";
    }

    private sealed class ToolChainStepFacts
    {
        public bool ExecutionAttempted { get; init; }
        public string ExecutionBlockedReason { get; init; } = "";
        public bool MutationObserved { get; init; }
        public List<string> TouchedFilePaths { get; init; } = [];
        public List<string> LinkedArtifactPaths { get; init; } = [];
    }

    private static string ReadArgument(ToolRequest request, string key)
    {
        return request.Arguments.TryGetValue(key, out var value)
            ? value ?? ""
            : "";
    }

    private static string DisplayChainName(string templateName)
    {
        return string.IsNullOrWhiteSpace(templateName) ? "(unnamed chain)" : $"`{templateName}`";
    }

    private static string ToSingleLineSummary(string text)
    {
        var firstLine = (text ?? "")
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";
        firstLine = firstLine.Trim();
        return firstLine.Length <= 180 ? firstLine : firstLine[..180] + "...";
    }
}
