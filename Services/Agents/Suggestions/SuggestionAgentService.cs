using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class SuggestionAgentService
{
    private const string SchemaName = "suggestion_presentation";
    private const string SchemaVersion = "1";

    private readonly IAgentRuntimeClient _runtimeClient;
    private readonly IAgentTraceWriter _traceWriter;
    private readonly AgentCallPolicyService _callPolicyService;
    private readonly SuggestionAgentValidator _validator = new();

    public SuggestionAgentService(
        IAgentRuntimeClient runtimeClient,
        IAgentTraceWriter traceWriter,
        AgentCallPolicyService callPolicyService)
    {
        _runtimeClient = runtimeClient;
        _traceWriter = traceWriter;
        _callPolicyService = callPolicyService;
    }

    public async Task<SuggestionPresentationResult> PresentAsync(
        string endpoint,
        string selectedModel,
        AppSettings settings,
        string workspaceRoot,
        string chainId,
        IReadOnlyList<ActionableSuggestionRecord> candidates,
        bool modelSummaryRequested,
        CancellationToken cancellationToken = default)
    {
        var fallback = BuildDeterministicFallback(candidates);
        var requestPayload = new SuggestionAgentRequestPayload
        {
            ChainId = chainId,
            WorkspaceRoot = workspaceRoot,
            Candidates = candidates
                .Select(candidate => new SuggestionAgentCandidate
                {
                    SuggestionId = candidate.SuggestionId,
                    Title = candidate.Title,
                    PromptText = candidate.PromptText,
                    Readiness = FormatReadiness(candidate.Readiness),
                    BlockedReason = candidate.BlockedReason,
                    ManualOnly = candidate.ManualOnly
                })
                .ToList()
        };

        var inputJson = JsonSerializer.Serialize(requestPayload);
        var requestId = Guid.NewGuid().ToString("N");
        var startedUtc = DateTime.UtcNow.ToString("O");

        var decision = _callPolicyService.Decide(
            AgentRole.Suggestions,
            settings,
            selectedModel,
            new AgentCallContext
            {
                WorkspaceRoot = workspaceRoot,
                WorkflowType = "tool_chain_summary",
                ResponseMode = ResponseMode.SummaryOnly,
                DeterministicFallbackAvailable = true,
                HasCompleteInputs = candidates.Count > 0,
                NativeManualOnlyState = candidates.Any(candidate => candidate.ManualOnly),
                ModelSummaryRequested = modelSummaryRequested,
                CandidateCount = candidates.Count
            });

        var skipReason = !decision.ShouldCall
            ? decision.Reason
            : string.IsNullOrWhiteSpace(endpoint)
                ? "Ollama endpoint is not configured."
                : string.IsNullOrWhiteSpace(decision.ModelName)
                    ? "No advisory model is configured for Suggestion Agent."
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

            fallback.Skipped = true;
            fallback.FallbackUsed = true;
            fallback.SkipReason = skipReason;
            fallback.TraceId = trace.TraceId;
            fallback.Invocation = new AgentInvocationResult
            {
                TraceId = trace.TraceId,
                RequestId = requestId,
                ResultCategory = "skipped",
                FallbackUsed = true,
                Skipped = true,
                SkipReason = skipReason
            };
            return fallback;
        }

        var envelope = new AgentRequestEnvelope
        {
            AgentRole = "suggestions",
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
                "do not invent suggestion ids",
                "do not change readiness or safety state",
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
                parsedPayloadJson: "");

            fallback.FallbackUsed = true;
            fallback.TraceId = invocation.TraceId;
            fallback.Invocation = invocation;
            return fallback;
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

            fallback.FallbackUsed = true;
            fallback.TraceId = invocation.TraceId;
            fallback.Invocation = invocation;
            return fallback;
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

        return BuildAcceptedResult(invocation, payload, candidates);
    }

    public SuggestionPresentationResult BuildDeterministicFallback(IReadOnlyList<ActionableSuggestionRecord> candidates)
    {
        var groups = new List<SuggestionPresentationGroup>();
        AddFallbackGroup(groups, "Ready now", candidates, SuggestionReadiness.ReadyNow);
        AddFallbackGroup(groups, "Needs prerequisite", candidates, SuggestionReadiness.NeedsPrerequisite);
        AddFallbackGroup(groups, "Manual-only", candidates, SuggestionReadiness.ManualOnly);
        AddFallbackGroup(groups, "Blocked", candidates, SuggestionReadiness.Blocked);
        AddFallbackGroup(groups, "Informational", candidates, SuggestionReadiness.InformationalOnly);

        return new SuggestionPresentationResult
        {
            Accepted = false,
            FallbackUsed = true,
            Groups = groups
        };
    }

    private SuggestionPresentationResult BuildAcceptedResult(
        AgentInvocationResult invocation,
        SuggestionAgentResponsePayload payload,
        IReadOnlyList<ActionableSuggestionRecord> candidates)
    {
        var candidateMap = candidates.ToDictionary(candidate => candidate.SuggestionId, StringComparer.OrdinalIgnoreCase);
        var orderedMap = payload.OrderedSuggestionIds
            .Where(candidateMap.ContainsKey)
            .Select(id => candidateMap[id])
            .ToDictionary(candidate => candidate.SuggestionId, StringComparer.OrdinalIgnoreCase);

        var groups = payload.DisplayGroups
            .Select(group => new SuggestionPresentationGroup
            {
                Title = group.Title,
                Suggestions = group.SuggestionIds
                    .Where(orderedMap.ContainsKey)
                    .Select(id => orderedMap[id])
                    .ToList()
            })
            .Where(group => group.Suggestions.Count > 0)
            .ToList();

        return new SuggestionPresentationResult
        {
            Accepted = true,
            TraceId = invocation.TraceId,
            Invocation = invocation,
            Groups = groups,
            PresentationNotes = payload.PresentationNotes
        };
    }

    private static void AddFallbackGroup(
        List<SuggestionPresentationGroup> groups,
        string title,
        IReadOnlyList<ActionableSuggestionRecord> candidates,
        SuggestionReadiness readiness)
    {
        var matches = candidates
            .Where(candidate => candidate.Readiness == readiness)
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matches.Count == 0)
            return;

        groups.Add(new SuggestionPresentationGroup
        {
            Title = title,
            Suggestions = matches
        });
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
            AgentRole = "suggestions",
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

    private static string FormatReadiness(SuggestionReadiness readiness)
    {
        return readiness switch
        {
            SuggestionReadiness.ReadyNow => "ready_now",
            SuggestionReadiness.NeedsPrerequisite => "needs_prerequisite",
            SuggestionReadiness.ManualOnly => "manual_only",
            SuggestionReadiness.Blocked => "blocked",
            _ => "informational_only"
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
