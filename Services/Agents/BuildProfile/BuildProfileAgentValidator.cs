using System.Text.Json;

namespace RAM.Services;

public sealed class BuildProfileAgentValidator
{
    private static readonly HashSet<string> AllowedStackFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet_desktop",
        "native_cpp_desktop",
        "web_app",
        "rust_app",
        "unknown"
    };

    private static readonly HashSet<string> AllowedConfidence = new(StringComparer.OrdinalIgnoreCase)
    {
        "low",
        "medium",
        "high"
    };

    public bool TryValidate(
        string rawText,
        BuildProfileAgentRequestPayload requestPayload,
        out BuildProfileAgentResponsePayload payload,
        out AgentValidationResult validation)
    {
        payload = new BuildProfileAgentResponsePayload();

        validation = AgentValidationHelpers.ParseRootObject(rawText);
        if (!validation.IsValid)
            return false;

        using var document = JsonDocument.Parse(rawText);
        var root = document.RootElement;

        validation = AgentValidationHelpers.EnsureOnlyKnownProperties(
            root,
            "stack_family",
            "language",
            "framework",
            "ui_shell_kind",
            "confidence",
            "missing_evidence",
            "rationale_codes");
        if (!validation.IsValid)
            return false;

        if (!AgentValidationHelpers.TryGetRequiredString(root, "stack_family", 40, out var stackFamily, out validation))
            return false;
        if (!AllowedStackFamilies.Contains(stackFamily))
        {
            validation = new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.EnumViolation,
                Message = $"Field `stack_family` contained unsupported value `{stackFamily}`."
            };
            return false;
        }

        if (!requestPayload.AllowedStackFamilies.Contains(stackFamily, StringComparer.OrdinalIgnoreCase))
        {
            validation = new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.ForbiddenContent,
                Message = $"Field `stack_family` proposed `{stackFamily}` outside the allowed advisory families."
            };
            return false;
        }

        if (!AgentValidationHelpers.TryGetRequiredString(root, "language", 40, out var language, out validation))
            return false;
        if (!AgentValidationHelpers.TryGetRequiredString(root, "framework", 60, out var framework, out validation))
            return false;
        if (!AgentValidationHelpers.TryGetRequiredString(root, "ui_shell_kind", 60, out var uiShellKind, out validation))
            return false;
        if (!AgentValidationHelpers.TryGetRequiredString(root, "confidence", 20, out var confidence, out validation))
            return false;
        if (!AllowedConfidence.Contains(confidence))
        {
            validation = new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.EnumViolation,
                Message = $"Field `confidence` contained unsupported value `{confidence}`."
            };
            return false;
        }

        if (!AgentValidationHelpers.TryGetStringArray(root, "missing_evidence", 6, 120, required: true, out var missingEvidence, out validation))
            return false;
        if (!AgentValidationHelpers.TryGetStringArray(root, "rationale_codes", 6, 60, required: true, out var rationaleCodes, out validation))
            return false;

        payload = new BuildProfileAgentResponsePayload
        {
            StackFamily = stackFamily,
            Language = language,
            Framework = framework,
            UiShellKind = uiShellKind,
            Confidence = confidence,
            MissingEvidence = missingEvidence,
            RationaleCodes = rationaleCodes
        };

        validation = new AgentValidationResult
        {
            IsValid = true,
            NormalizedPayloadJson = JsonSerializer.Serialize(payload)
        };
        return true;
    }
}
