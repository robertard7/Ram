using RAM.Models;

namespace RAM.Services;

public sealed class ToolRequestParser
{
    public bool LooksLikeToolRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.TrimStart().StartsWith("TOOL_REQUEST", StringComparison.OrdinalIgnoreCase);
    }

    public ToolRequest? Parse(string text)
    {
        if (!LooksLikeToolRequest(text))
            return null;

        var lines = text.Replace("\r", "")
            .Split('\n', StringSplitOptions.None)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (lines.Count == 0 || !string.Equals(lines[0], "TOOL_REQUEST", StringComparison.OrdinalIgnoreCase))
            return null;

        var request = new ToolRequest();

        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
                return null;

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                return null;

            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
            {
                request.ToolName = value;
                continue;
            }

            if (string.Equals(key, "reason", StringComparison.OrdinalIgnoreCase))
            {
                request.Reason = value;
                continue;
            }

            request.Arguments[key] = value;
        }

        if (string.IsNullOrWhiteSpace(request.ToolName))
            return null;

        return request;
    }
}
