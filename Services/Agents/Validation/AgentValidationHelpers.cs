using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RAM.Services;

public static class AgentValidationHelpers
{
    public static string ComputeHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static AgentValidationResult ParseRootObject(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.EmptyResponse,
                Message = "The agent returned an empty response."
            };
        }

        try
        {
            using var document = JsonDocument.Parse(rawText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new AgentValidationResult
                {
                    IsValid = false,
                    RejectionReason = AgentRejectionReason.NonJsonResponse,
                    Message = "The agent response root was not a JSON object."
                };
            }

            return new AgentValidationResult { IsValid = true };
        }
        catch (JsonException ex)
        {
            return new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.NonJsonResponse,
                Message = $"The agent response was not valid JSON: {ex.Message}"
            };
        }
    }

    public static bool ContainsForbiddenNarrative(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("TOOL_REQUEST", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Tool request:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("name=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("reason=", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetRequiredString(
        JsonElement root,
        string propertyName,
        int maxLength,
        out string value,
        out AgentValidationResult validation)
    {
        value = "";
        if (!root.TryGetProperty(propertyName, out var property))
        {
            validation = MissingField(propertyName);
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            validation = SchemaMismatch(propertyName, "Expected a string field.");
            return false;
        }

        value = property.GetString() ?? "";
        if (value.Length > maxLength)
        {
            validation = new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.FieldOverflow,
                Message = $"Field `{propertyName}` exceeded the {maxLength}-character limit."
            };
            return false;
        }

        if (ContainsForbiddenNarrative(value))
        {
            validation = new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.ForbiddenContent,
                Message = $"Field `{propertyName}` contained forbidden tool-style or schema-breaking text."
            };
            return false;
        }

        validation = new AgentValidationResult { IsValid = true };
        return true;
    }

    public static bool TryGetStringArray(
        JsonElement root,
        string propertyName,
        int maxItems,
        int maxItemLength,
        bool required,
        out List<string> values,
        out AgentValidationResult validation)
    {
        values = [];
        if (!root.TryGetProperty(propertyName, out var property))
        {
            validation = required ? MissingField(propertyName) : new AgentValidationResult { IsValid = true };
            return !required;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            validation = SchemaMismatch(propertyName, "Expected an array field.");
            return false;
        }

        var array = property.EnumerateArray().ToList();
        if (array.Count > maxItems)
        {
            validation = new AgentValidationResult
            {
                IsValid = false,
                RejectionReason = AgentRejectionReason.FieldOverflow,
                Message = $"Array `{propertyName}` exceeded the {maxItems}-item limit."
            };
            return false;
        }

        foreach (var item in array)
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                validation = SchemaMismatch(propertyName, "Expected only string array items.");
                return false;
            }

            var text = item.GetString() ?? "";
            if (text.Length > maxItemLength)
            {
                validation = new AgentValidationResult
                {
                    IsValid = false,
                    RejectionReason = AgentRejectionReason.FieldOverflow,
                    Message = $"Array `{propertyName}` contained an item over the {maxItemLength}-character limit."
                };
                return false;
            }

            if (ContainsForbiddenNarrative(text))
            {
                validation = new AgentValidationResult
                {
                    IsValid = false,
                    RejectionReason = AgentRejectionReason.ForbiddenContent,
                    Message = $"Array `{propertyName}` contained forbidden tool-style or schema-breaking text."
                };
                return false;
            }

            values.Add(text);
        }

        validation = new AgentValidationResult { IsValid = true };
        return true;
    }

    public static AgentValidationResult EnsureOnlyKnownProperties(JsonElement root, params string[] knownProperties)
    {
        var known = knownProperties.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unexpected = root.EnumerateObject()
            .Select(property => property.Name)
            .Where(name => !known.Contains(name))
            .ToList();

        if (unexpected.Count == 0)
            return new AgentValidationResult { IsValid = true };

        return new AgentValidationResult
        {
            IsValid = false,
            RejectionReason = AgentRejectionReason.UnknownProperty,
            Message = $"The agent response included unknown properties: {string.Join(", ", unexpected)}."
        };
    }

    public static AgentValidationResult MissingField(string propertyName)
    {
        return new AgentValidationResult
        {
            IsValid = false,
            RejectionReason = AgentRejectionReason.FieldMissing,
            Message = $"Required field `{propertyName}` was missing."
        };
    }

    public static AgentValidationResult SchemaMismatch(string propertyName, string message)
    {
        return new AgentValidationResult
        {
            IsValid = false,
            RejectionReason = AgentRejectionReason.SchemaMismatch,
            Message = $"Field `{propertyName}` was invalid. {message}"
        };
    }
}
