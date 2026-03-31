using System.Text;
using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private bool TryExecuteGenericSlashTool(string toolName, string argumentText)
    {
        if (!_toolRegistryService.HasTool(toolName))
            return false;

        var request = new ToolRequest
        {
            ToolName = toolName,
            Reason = "User issued a slash tool command."
        };

        if (!TryParseSlashToolArguments(argumentText, request.Arguments, out var errorMessage))
        {
            AddMessage("error", errorMessage);
            AppendOutput(errorMessage);
            return true;
        }

        ExecuteToolRequest(request, "Slash tool request");
        return true;
    }

    private static bool TryParseSlashToolArguments(
        string argumentText,
        Dictionary<string, string> arguments,
        out string errorMessage)
    {
        arguments.Clear();

        foreach (var token in TokenizeSlashArguments(argumentText))
        {
            var separatorIndex = token.IndexOf('=');
            if (separatorIndex <= 0)
            {
                errorMessage = $"Invalid tool argument: {token}. Use key=value pairs.";
                return false;
            }

            var key = token[..separatorIndex].Trim();
            var value = token[(separatorIndex + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(key))
            {
                errorMessage = $"Invalid tool argument: {token}. Argument name is required.";
                return false;
            }

            arguments[key] = value;
        }

        errorMessage = "";
        return true;
    }

    private static List<string> TokenizeSlashArguments(string input)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
            return tokens;

        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }
}
