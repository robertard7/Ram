using System.Text.Json;

namespace RAM.Services;

public sealed class ForensicsAgentValidator
{
    public bool TryValidate(
        string rawText,
        ForensicsAgentRequestPayload requestPayload,
        out ForensicsAgentResponsePayload payload,
        out AgentValidationResult validation)
    {
        payload = new ForensicsAgentResponsePayload();

        validation = AgentValidationHelpers.ParseRootObject(rawText);
        if (!validation.IsValid)
            return false;

        using var document = JsonDocument.Parse(rawText);
        var root = document.RootElement;

        validation = AgentValidationHelpers.EnsureOnlyKnownProperties(
            root,
            "explanation",
            "missing_piece_category",
            "recommended_next_action_category");
        if (!validation.IsValid)
            return false;

        if (!AgentValidationHelpers.TryGetRequiredString(root, "explanation", 220, out var explanation, out validation))
            return false;
        if (!AgentValidationHelpers.TryGetRequiredString(root, "missing_piece_category", 60, out var missingPieceCategory, out validation))
            return false;
        if (!requestPayload.AllowedMissingPieceCategories.Contains(missingPieceCategory, StringComparer.OrdinalIgnoreCase))
        {
            validation = new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.ForbiddenContent,
                Message = $"Field `missing_piece_category` proposed `{missingPieceCategory}` outside the allowed categories."
            };
            return false;
        }

        if (!AgentValidationHelpers.TryGetRequiredString(root, "recommended_next_action_category", 60, out var nextActionCategory, out validation))
            return false;
        if (!requestPayload.AllowedNextActionCategories.Contains(nextActionCategory, StringComparer.OrdinalIgnoreCase))
        {
            validation = new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.ForbiddenContent,
                Message = $"Field `recommended_next_action_category` proposed `{nextActionCategory}` outside the allowed categories."
            };
            return false;
        }

        payload = new ForensicsAgentResponsePayload
        {
            Explanation = explanation,
            MissingPieceCategory = missingPieceCategory,
            RecommendedNextActionCategory = nextActionCategory
        };

        validation = new AgentValidationResult
        {
            IsValid = true,
            NormalizedPayloadJson = JsonSerializer.Serialize(payload)
        };
        return true;
    }
}
