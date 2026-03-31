using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class BuildProfileAgentService
{
    private const string SchemaName = "taskboard_build_profile";
    private const string SchemaVersion = "1";

    private readonly IAgentRuntimeClient _runtimeClient;
    private readonly IAgentTraceWriter _traceWriter;
    private readonly AgentCallPolicyService _callPolicyService;
    private readonly BuildProfileAgentValidator _validator = new();

    public BuildProfileAgentService(
        IAgentRuntimeClient runtimeClient,
        IAgentTraceWriter traceWriter,
        AgentCallPolicyService callPolicyService)
    {
        _runtimeClient = runtimeClient;
        _traceWriter = traceWriter;
        _callPolicyService = callPolicyService;
    }

    public async Task<BuildProfileAgentPresentationResult> InferAsync(
        string endpoint,
        string selectedModel,
        AppSettings settings,
        string workspaceRoot,
        BuildProfileAgentRequestPayload requestPayload,
        CancellationToken cancellationToken = default)
    {
        var inputJson = JsonSerializer.Serialize(requestPayload);
        var requestId = Guid.NewGuid().ToString("N");
        var startedUtc = DateTime.UtcNow.ToString("O");

        var decision = _callPolicyService.Decide(
            AgentRole.BuildProfile,
            settings,
            selectedModel,
            new AgentCallContext
            {
                WorkspaceRoot = workspaceRoot,
                WorkflowType = "taskboard_build_profile_resolution",
                ResponseMode = ResponseMode.None,
                DeterministicFallbackAvailable = true,
                HasCompleteInputs = !string.IsNullOrWhiteSpace(requestPayload.WorkItemTitle)
                    && requestPayload.AllowedStackFamilies.Count > 0,
                CandidateCount = requestPayload.AllowedStackFamilies.Count
            });

        var skipReason = !decision.ShouldCall
            ? decision.Reason
            : string.IsNullOrWhiteSpace(endpoint)
                ? "Ollama endpoint is not configured."
                : string.IsNullOrWhiteSpace(decision.ModelName)
                    ? "No advisory model is configured for Build Profile Agent."
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
                parsedPayloadJson: "");
            _traceWriter.WriteTrace(workspaceRoot, trace);

            return new BuildProfileAgentPresentationResult
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

        var envelope = new AgentRequestEnvelope
        {
            AgentRole = "build_profile",
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
                "choose only from the allowed stack_family values",
                "do not override explicit taskboard intent",
                "bounded arrays only"
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
                parsedPayloadJson: "");

            return new BuildProfileAgentPresentationResult
            {
                Accepted = false,
                FallbackUsed = true,
                TraceId = invocation.TraceId,
                Invocation = invocation
            };
        }

        if (!_validator.TryValidate(invocation.RawModelText, requestPayload, out var payload, out var validation))
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
                parsedPayloadJson: "");

            return new BuildProfileAgentPresentationResult
            {
                Accepted = false,
                FallbackUsed = true,
                TraceId = invocation.TraceId,
                Invocation = invocation
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

        return new BuildProfileAgentPresentationResult
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
            AgentRole = "build_profile",
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
