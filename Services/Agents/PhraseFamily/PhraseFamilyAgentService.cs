using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class PhraseFamilyAgentService
{
    private const string SchemaName = "taskboard_phrase_family";
    private const string SchemaVersion = "1";

    private readonly IAgentRuntimeClient _runtimeClient;
    private readonly IAgentTraceWriter _traceWriter;
    private readonly AgentCallPolicyService _callPolicyService;
    private readonly PhraseFamilyAgentValidator _validator = new();

    public PhraseFamilyAgentService(
        IAgentRuntimeClient runtimeClient,
        IAgentTraceWriter traceWriter,
        AgentCallPolicyService callPolicyService)
    {
        _runtimeClient = runtimeClient;
        _traceWriter = traceWriter;
        _callPolicyService = callPolicyService;
    }

    public async Task<PhraseFamilyAgentPresentationResult> ClassifyAsync(
        string endpoint,
        string selectedModel,
        AppSettings settings,
        string workspaceRoot,
        PhraseFamilyAgentRequestPayload requestPayload,
        CancellationToken cancellationToken = default)
    {
        var inputJson = JsonSerializer.Serialize(requestPayload);
        var requestId = Guid.NewGuid().ToString("N");
        var startedUtc = DateTime.UtcNow.ToString("O");
        var decision = _callPolicyService.Decide(
            AgentRole.PhraseFamily,
            settings,
            selectedModel,
            new AgentCallContext
            {
                WorkspaceRoot = workspaceRoot,
                WorkflowType = "taskboard_phrase_family",
                ResponseMode = ResponseMode.None,
                DeterministicFallbackAvailable = true,
                HasCompleteInputs = !string.IsNullOrWhiteSpace(requestPayload.WorkItemTitle)
                    && requestPayload.AllowedPhraseFamilies.Count > 0,
                CandidateCount = requestPayload.AllowedPhraseFamilies.Count
            });

        var skipReason = !decision.ShouldCall
            ? decision.Reason
            : string.IsNullOrWhiteSpace(endpoint)
                ? "Ollama endpoint is not configured."
                : string.IsNullOrWhiteSpace(decision.ModelName)
                    ? "No advisory model is configured for Phrase Family Agent."
                    : "";
        if (!decision.ShouldCall || string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(decision.ModelName))
            return BuildSkippedResult(workspaceRoot, requestId, startedUtc, decision.ModelName, inputJson, skipReason);

        var envelope = new AgentRequestEnvelope
        {
            AgentRole = "phrase_family",
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
                "choose only from the allowed phrase_family values",
                "bounded rationale codes only"
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
            return BuildRejectedResult(workspaceRoot, invocation, decision.ModelName, inputJson, invocation.ResultCategory, invocation.RejectionReason.ToString());

        if (!_validator.TryValidate(invocation.RawModelText, requestPayload, out var payload, out var validation))
        {
            invocation.Validation = validation;
            invocation.RejectionReason = validation.RejectionReason;
            invocation.ResultCategory = RejectionReasonToCategory(validation.RejectionReason);
            invocation.FallbackUsed = true;
            return BuildRejectedResult(workspaceRoot, invocation, decision.ModelName, inputJson, invocation.ResultCategory, validation.Message);
        }

        invocation.Accepted = true;
        invocation.Success = true;
        invocation.Validation = validation;
        invocation.ParsedPayloadJson = validation.NormalizedPayloadJson;
        invocation.ResultCategory = "accepted";
        WriteTrace(workspaceRoot, invocation, decision.ModelName, inputJson, "accepted", true, false, "", validation.NormalizedPayloadJson);

        return new PhraseFamilyAgentPresentationResult
        {
            Accepted = true,
            TraceId = invocation.TraceId,
            Invocation = invocation,
            Payload = payload
        };
    }

    private PhraseFamilyAgentPresentationResult BuildSkippedResult(
        string workspaceRoot,
        string requestId,
        string startedUtc,
        string selectedModel,
        string inputJson,
        string skipReason)
    {
        var trace = BuildTrace(Guid.NewGuid().ToString("N"), requestId, selectedModel, inputJson, startedUtc, 0, "skipped", false, true, AgentRejectionReason.None, "", skipReason, "");
        _traceWriter.WriteTrace(workspaceRoot, trace);
        return new PhraseFamilyAgentPresentationResult
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
            }
        };
    }

    private PhraseFamilyAgentPresentationResult BuildRejectedResult(
        string workspaceRoot,
        AgentInvocationResult invocation,
        string selectedModel,
        string inputJson,
        string resultCategory,
        string validationMessage)
    {
        WriteTrace(workspaceRoot, invocation, selectedModel, inputJson, resultCategory, false, true, validationMessage, "");
        return new PhraseFamilyAgentPresentationResult
        {
            Accepted = false,
            FallbackUsed = true,
            TraceId = invocation.TraceId,
            Invocation = invocation
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
        _traceWriter.WriteTrace(workspaceRoot, BuildTrace(
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
            AgentRole = "phrase_family",
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
}
