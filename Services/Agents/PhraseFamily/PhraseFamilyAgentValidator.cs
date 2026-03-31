using System.Text.Json;

namespace RAM.Services;

public sealed class PhraseFamilyAgentValidator
{
    private static readonly HashSet<string> AllowedConfidence = new(StringComparer.OrdinalIgnoreCase)
    {
        "low",
        "medium",
        "high"
    };

    public bool TryValidate(
        string rawText,
        PhraseFamilyAgentRequestPayload requestPayload,
        out PhraseFamilyAgentResponsePayload payload,
        out AgentValidationResult validation)
    {
        payload = new PhraseFamilyAgentResponsePayload();

        validation = AgentValidationHelpers.ParseRootObject(rawText);
        if (!validation.IsValid)
            return false;

        using var document = JsonDocument.Parse(rawText);
        var root = document.RootElement;

        validation = AgentValidationHelpers.EnsureOnlyKnownProperties(
            root,
            "phrase_family",
            "confidence",
            "rationale_codes");
        if (!validation.IsValid)
            return false;

        if (!AgentValidationHelpers.TryGetRequiredString(root, "phrase_family", 80, out var phraseFamily, out validation))
            return false;
        if (!requestPayload.AllowedPhraseFamilies.Contains(phraseFamily, StringComparer.OrdinalIgnoreCase))
        {
            validation = new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.ForbiddenContent,
                Message = $"Field `phrase_family` proposed `{phraseFamily}` outside the allowed phrase-family candidates."
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

        if (!AgentValidationHelpers.TryGetStringArray(root, "rationale_codes", 6, 60, required: true, out var rationaleCodes, out validation))
            return false;

        payload = new PhraseFamilyAgentResponsePayload
        {
            PhraseFamily = phraseFamily,
            Confidence = confidence,
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
