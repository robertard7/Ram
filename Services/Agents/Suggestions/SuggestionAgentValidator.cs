using System.Text.Json;

namespace RAM.Services;

public sealed class SuggestionAgentValidator
{
    private const int MaxGroups = 5;
    private const int MaxNotes = 4;
    private const int MaxTitleLength = 80;

    public bool TryValidate(
        string rawText,
        SuggestionAgentRequestPayload request,
        out SuggestionAgentResponsePayload payload,
        out AgentValidationResult validation)
    {
        payload = new SuggestionAgentResponsePayload();
        validation = AgentValidationHelpers.ParseRootObject(rawText);
        if (!validation.IsValid)
            return false;

        using var document = JsonDocument.Parse(rawText);
        var root = document.RootElement;

        validation = AgentValidationHelpers.EnsureOnlyKnownProperties(root, "ordered_suggestion_ids", "display_groups", "presentation_notes");
        if (!validation.IsValid)
            return false;

        if (!AgentValidationHelpers.TryGetStringArray(root, "ordered_suggestion_ids", request.Candidates.Count, 64, required: true, out var orderedIds, out validation))
            return false;

        if (!AgentValidationHelpers.TryGetStringArray(root, "presentation_notes", MaxNotes, 160, required: false, out var notes, out validation))
            return false;

        if (!root.TryGetProperty("display_groups", out var groupsElement))
        {
            validation = AgentValidationHelpers.MissingField("display_groups");
            return false;
        }

        if (groupsElement.ValueKind != JsonValueKind.Array)
        {
            validation = AgentValidationHelpers.SchemaMismatch("display_groups", "Expected an array field.");
            return false;
        }

        var candidateIds = request.Candidates.Select(candidate => candidate.SuggestionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (orderedIds.Count != candidateIds.Count || orderedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != orderedIds.Count)
        {
            validation = new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.SchemaMismatch,
                Message = "ordered_suggestion_ids must list each known candidate exactly once."
            };
            return false;
        }

        if (orderedIds.Any(id => !candidateIds.Contains(id)))
        {
            validation = new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.SchemaMismatch,
                Message = "ordered_suggestion_ids contained an unknown suggestion id."
            };
            return false;
        }

        var groups = new List<SuggestionAgentDisplayGroup>();
        var groupElements = groupsElement.EnumerateArray().ToList();
        if (groupElements.Count > MaxGroups)
        {
            validation = new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.FieldOverflow,
                Message = $"display_groups exceeded the {MaxGroups}-group limit."
            };
            return false;
        }

        var groupedIds = new List<string>();
        foreach (var element in groupElements)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                validation = AgentValidationHelpers.SchemaMismatch("display_groups", "Each group must be an object.");
                return false;
            }

            validation = AgentValidationHelpers.EnsureOnlyKnownProperties(element, "title", "suggestion_ids");
            if (!validation.IsValid)
                return false;

            if (!AgentValidationHelpers.TryGetRequiredString(element, "title", MaxTitleLength, out var title, out validation))
                return false;

            if (!AgentValidationHelpers.TryGetStringArray(element, "suggestion_ids", request.Candidates.Count, 64, required: true, out var groupIds, out validation))
                return false;

            if (groupIds.Any(id => !candidateIds.Contains(id)))
            {
                validation = new AgentValidationResult
                {
                    IsValid = false,
                    RejectionReason = AgentRejectionReason.SchemaMismatch,
                    Message = "display_groups contained an unknown suggestion id."
                };
                return false;
            }

            groupedIds.AddRange(groupIds);
            groups.Add(new SuggestionAgentDisplayGroup
            {
                Title = title,
                SuggestionIds = groupIds
            });
        }

        if (groupedIds.Count != candidateIds.Count
            || groupedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != groupedIds.Count
            || groupedIds.Any(id => !orderedIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            validation = new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.SchemaMismatch,
                Message = "display_groups must cover each ordered suggestion exactly once."
            };
            return false;
        }

        payload = new SuggestionAgentResponsePayload
        {
            OrderedSuggestionIds = orderedIds,
            DisplayGroups = groups,
            PresentationNotes = notes
        };
        validation = new AgentValidationResult
        {
            IsValid = true,
            NormalizedPayloadJson = JsonSerializer.Serialize(payload)
        };
        return true;
    }
}
