using System.Text.Json;

namespace RAM.Services;

public sealed class SummaryAgentValidator
{
    public bool TryValidate(
        string rawText,
        out SummaryAgentResponsePayload payload,
        out AgentValidationResult validation)
    {
        payload = new SummaryAgentResponsePayload();
        validation = AgentValidationHelpers.ParseRootObject(rawText);
        if (!validation.IsValid)
            return false;

        using var document = JsonDocument.Parse(rawText);
        var root = document.RootElement;
        validation = AgentValidationHelpers.EnsureOnlyKnownProperties(root, "summary_title", "status_line", "summary_lines", "warnings");
        if (!validation.IsValid)
            return false;

        if (!AgentValidationHelpers.TryGetRequiredString(root, "summary_title", 120, out var title, out validation))
            return false;
        if (!AgentValidationHelpers.TryGetRequiredString(root, "status_line", 200, out var statusLine, out validation))
            return false;
        if (!AgentValidationHelpers.TryGetStringArray(root, "summary_lines", 8, 240, required: true, out var summaryLines, out validation))
            return false;
        if (!AgentValidationHelpers.TryGetStringArray(root, "warnings", 4, 200, required: false, out var warnings, out validation))
            return false;

        payload = new SummaryAgentResponsePayload
        {
            SummaryTitle = title,
            StatusLine = statusLine,
            SummaryLines = summaryLines,
            Warnings = warnings
        };
        validation = new AgentValidationResult
        {
            IsValid = true,
            NormalizedPayloadJson = JsonSerializer.Serialize(payload)
        };
        return true;
    }
}
