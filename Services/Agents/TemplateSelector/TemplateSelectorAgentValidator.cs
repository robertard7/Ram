using System.Text.Json;

namespace RAM.Services;

public sealed class TemplateSelectorAgentValidator
{
    private static readonly HashSet<string> AllowedConfidence = new(StringComparer.OrdinalIgnoreCase)
    {
        "low",
        "medium",
        "high"
    };

    public bool TryValidate(
        string rawText,
        TemplateSelectorAgentRequestPayload requestPayload,
        out TemplateSelectorAgentResponsePayload payload,
        out AgentValidationResult validation)
    {
        payload = new TemplateSelectorAgentResponsePayload();

        validation = AgentValidationHelpers.ParseRootObject(rawText);
        if (!validation.IsValid)
            return false;

        using var document = JsonDocument.Parse(rawText);
        var root = document.RootElement;

        validation = AgentValidationHelpers.EnsureOnlyKnownProperties(
            root,
            "template_id",
            "confidence",
            "reason_codes");
        if (!validation.IsValid)
            return false;

        if (!AgentValidationHelpers.TryGetRequiredString(root, "template_id", 100, out var templateId, out validation))
            return false;
        if (!requestPayload.CandidateTemplateIds.Contains(templateId, StringComparer.OrdinalIgnoreCase))
        {
            validation = new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.ForbiddenContent,
                Message = $"Field `template_id` proposed `{templateId}` outside the candidate template ids."
            };
            return false;
        }

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

        if (!AgentValidationHelpers.TryGetStringArray(root, "reason_codes", 6, 60, required: true, out var reasonCodes, out validation))
            return false;

        payload = new TemplateSelectorAgentResponsePayload
        {
            TemplateId = templateId,
            Confidence = confidence,
            ReasonCodes = reasonCodes
        };

        validation = new AgentValidationResult
        {
            IsValid = true,
            NormalizedPayloadJson = JsonSerializer.Serialize(payload)
        };
        return true;
    }
}
