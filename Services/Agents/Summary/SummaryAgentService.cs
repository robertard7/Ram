using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class SummaryAgentService
{
    private const string SchemaName = "chain_summary";
    private const string SchemaVersion = "1";

    private readonly IAgentRuntimeClient _runtimeClient;
    private readonly IAgentTraceWriter _traceWriter;
    private readonly AgentCallPolicyService _callPolicyService;
    private readonly SummaryAgentValidator _validator = new();

    public SummaryAgentService(
        IAgentRuntimeClient runtimeClient,
        IAgentTraceWriter traceWriter,
        AgentCallPolicyService callPolicyService)
    {
        _runtimeClient = runtimeClient;
        _traceWriter = traceWriter;
        _callPolicyService = callPolicyService;
    }

    public async Task<SummaryPresentationResult> FormatAsync(
        string endpoint,
        string selectedModel,
        AppSettings settings,
        string workspaceRoot,
        ToolChainSummaryInput input,
        SuggestionPresentationResult suggestionPresentation,
        bool modelSummaryRequested,
        SummaryAgentResponsePayload deterministicFallback,
        CancellationToken cancellationToken = default)
    {
        var requestPayload = new SummaryAgentRequestPayload
        {
            ChainId = input.ChainId,
            ChainType = FormatChainType(input.ChainType),
            TemplateName = input.TemplateName,
            UserGoal = input.UserGoal,
            Status = FormatStatus(input.Status),
            StopReason = FormatStopReason(input.StopReason),
            FinalOutcomeSummary = input.FinalOutcomeSummary,
            ExecutionOccurred = input.ExecutionOccurred,
            ExecutionBlocked = input.ExecutionBlocked,
            ReadySuggestionCount = input.ActionableSuggestions.Count(suggestion => suggestion.Readiness == SuggestionReadiness.ReadyNow),
            BlockedSuggestionCount = input.ActionableSuggestions.Count(suggestion => suggestion.Readiness == SuggestionReadiness.Blocked),
            ManualOnlySuggestionCount = input.ActionableSuggestions.Count(suggestion => suggestion.Readiness == SuggestionReadiness.ManualOnly),
            Steps = input.Steps
                .Select(step => new SummaryAgentStepFact
                {
                    StepIndex = step.StepIndex,
                    ToolName = step.ToolName,
                    ResultClassification = step.ResultClassification,
                    ResultSummary = step.ResultSummary,
                    ExecutionAttempted = step.ExecutionAttempted,
                    ExecutionBlockedReason = step.ExecutionBlockedReason
                })
                .ToList()
        };

        var inputJson = JsonSerializer.Serialize(requestPayload);
        var requestId = Guid.NewGuid().ToString("N");
        var startedUtc = DateTime.UtcNow.ToString("O");

        var decision = _callPolicyService.Decide(
            AgentRole.Summary,
            settings,
            selectedModel,
            new AgentCallContext
            {
                WorkspaceRoot = workspaceRoot,
                WorkflowType = "tool_chain_summary",
                ResponseMode = input.ResponseMode,
                DeterministicFallbackAvailable = true,
                HasCompleteInputs = input.Steps.Count > 0 || !string.IsNullOrWhiteSpace(input.FinalOutcomeSummary),
                NativeManualOnlyState = suggestionPresentation.Groups.Any(group => string.Equals(group.Title, "Manual-only", StringComparison.OrdinalIgnoreCase)),
                ModelSummaryRequested = modelSummaryRequested,
                CandidateCount = input.ActionableSuggestions.Count
            });

        var skipReason = !decision.ShouldCall
            ? decision.Reason
            : string.IsNullOrWhiteSpace(endpoint)
                ? "Ollama endpoint is not configured."
                : string.IsNullOrWhiteSpace(decision.ModelName)
                    ? "No advisory model is configured for Summary Agent."
                    : "";

        if (!decision.ShouldCall || string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(decision.ModelName))
        {
            var trace = BuildTrace(
                traceId: Guid.NewGuid().ToString("N"),
                requestId,
                selectedModel: decision.ModelName,
                inputJson,
                startedUtc,
                elapsedMs: 0,
                resultCategory: "skipped",
                accepted: false,
                fallbackUsed: true,
                rejectionReason: AgentRejectionReason.None,
                rawModelText: "",
                validationMessage: skipReason,
                parsedPayloadJson: JsonSerializer.Serialize(deterministicFallback));
            _traceWriter.WriteTrace(workspaceRoot, trace);

            return new SummaryPresentationResult
            {
                Accepted = false,
                FallbackUsed = true,
                Skipped = true,
                SkipReason = skipReason,
                TraceId = trace.TraceId,
                Invocation = new AgentInvocationResult
                {
                    TraceId = trace.TraceId,
                    RequestId = requestId,
                    ResultCategory = "skipped",
                    FallbackUsed = true,
                    Skipped = true,
                    SkipReason = skipReason
                },
                Payload = deterministicFallback
            };
        }

        var envelope = new AgentRequestEnvelope
        {
            AgentRole = "summary",
            SchemaName = SchemaName,
            SchemaVersion = SchemaVersion,
            WorkspaceId = workspaceRoot,
            RequestId = requestId,
            TimestampUtc = startedUtc,
            InputPayloadJson = inputJson,
            Constraints =
            [
                "output JSON only",
                "no tool usage",
                "no extra fields",
                "do not invent steps or facts",
                "do not propose tools or new actions",
                "bounded array counts only"
            ]
        };

        var invocation = await _runtimeClient.InvokeAsync(
            endpoint,
            envelope,
            new AgentInvocationOptions
            {
                ModelName = decision.ModelName,
                Timeout = decision.Timeout
            },
            cancellationToken);

        if (!invocation.Success)
        {
            WriteTrace(
                workspaceRoot,
                invocation,
                decision.ModelName,
                inputJson,
                invocation.ResultCategory,
                accepted: false,
                fallbackUsed: true,
                validationMessage: invocation.RejectionReason.ToString(),
                parsedPayloadJson: JsonSerializer.Serialize(deterministicFallback));

            return new SummaryPresentationResult
            {
                Accepted = false,
                FallbackUsed = true,
                TraceId = invocation.TraceId,
                Invocation = invocation,
                Payload = deterministicFallback
            };
        }

        if (!_validator.TryValidate(invocation.RawModelText, out var payload, out var validation))
        {
            invocation.Validation = validation;
            invocation.RejectionReason = validation.RejectionReason;
            invocation.ResultCategory = RejectionReasonToCategory(validation.RejectionReason);
            invocation.FallbackUsed = true;

            WriteTrace(
                workspaceRoot,
                invocation,
                decision.ModelName,
                inputJson,
                invocation.ResultCategory,
                accepted: false,
                fallbackUsed: true,
                validationMessage: validation.Message,
                parsedPayloadJson: JsonSerializer.Serialize(deterministicFallback));

            return new SummaryPresentationResult
            {
                Accepted = false,
                FallbackUsed = true,
                TraceId = invocation.TraceId,
                Invocation = invocation,
                Payload = deterministicFallback
            };
        }

        invocation.Accepted = true;
        invocation.Success = true;
        invocation.Validation = validation;
        invocation.ParsedPayloadJson = validation.NormalizedPayloadJson;
        invocation.ResultCategory = "accepted";

        WriteTrace(
            workspaceRoot,
            invocation,
            decision.ModelName,
            inputJson,
            "accepted",
            accepted: true,
            fallbackUsed: false,
            validationMessage: "",
            parsedPayloadJson: validation.NormalizedPayloadJson);

        return new SummaryPresentationResult
        {
            Accepted = true,
            TraceId = invocation.TraceId,
            Invocation = invocation,
            Payload = payload
        };
    }

    private void WriteTrace(
        string workspaceRoot,
        AgentInvocationResult invocation,
        string selectedModel,
        string inputJson,
        string resultCategory,
        bool accepted,
        bool fallbackUsed,
        string validationMessage,
        string parsedPayloadJson)
    {
        _traceWriter.WriteTrace(
            workspaceRoot,
            BuildTrace(
                invocation.TraceId,
                invocation.RequestId,
                selectedModel,
                inputJson,
                invocation.Request.TimestampUtc,
                invocation.ElapsedMs,
                resultCategory,
                accepted,
                fallbackUsed,
                invocation.RejectionReason,
                invocation.RawModelText,
                validationMessage,
                parsedPayloadJson));
    }

    private static AgentTraceRecord BuildTrace(
        string traceId,
        string requestId,
        string selectedModel,
        string inputJson,
        string startedUtc,
        long elapsedMs,
        string resultCategory,
        bool accepted,
        bool fallbackUsed,
        AgentRejectionReason rejectionReason,
        string rawModelText,
        string validationMessage,
        string parsedPayloadJson)
    {
        return new AgentTraceRecord
        {
            TraceId = traceId,
            RequestId = requestId,
            AgentRole = "summary",
            Model = selectedModel,
            SchemaName = SchemaName,
            SchemaVersion = SchemaVersion,
            InputHash = AgentValidationHelpers.ComputeHash(inputJson),
            StartedUtc = startedUtc,
            ElapsedMs = elapsedMs,
            ResultCategory = resultCategory,
            Accepted = accepted,
            FallbackUsed = fallbackUsed,
            RejectionReason = rejectionReason,
            RawRequestJson = inputJson,
            RawModelText = rawModelText,
            ValidationMessage = validationMessage,
            ParsedPayloadJson = parsedPayloadJson
        };
    }

    private static string RejectionReasonToCategory(AgentRejectionReason reason)
    {
        return reason switch
        {
            AgentRejectionReason.Timeout => "timeout",
            AgentRejectionReason.EmptyResponse => "empty_response",
            AgentRejectionReason.NonJsonResponse => "non_json_response",
            AgentRejectionReason.EnumViolation => "enum_violation",
            AgentRejectionReason.FieldMissing => "field_missing",
            AgentRejectionReason.FieldOverflow => "field_overflow",
            AgentRejectionReason.ForbiddenContent => "forbidden_content",
            AgentRejectionReason.TransportFailure => "transport_failure",
            AgentRejectionReason.RuntimeException => "runtime_exception",
            _ => "schema_mismatch"
        };
    }

    private static string FormatChainType(ToolChainType chainType)
    {
        return chainType.ToString().ToLowerInvariant();
    }

    private static string FormatStatus(ToolChainStatus status)
    {
        return status.ToString().ToLowerInvariant();
    }

    private static string FormatStopReason(ToolChainStopReason stopReason)
    {
        return stopReason.ToString().ToLowerInvariant();
    }
}
