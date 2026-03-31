using RAM.Models;

namespace RAM.Services;

public sealed class ToolErrorTranslator
{
    private readonly ToolRegistryService _toolRegistryService;

    public ToolErrorTranslator(ToolRegistryService toolRegistryService)
    {
        _toolRegistryService = toolRegistryService;
    }

    public string Translate(ToolRequest request, ToolResult result)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        if (result is null)
            throw new ArgumentNullException(nameof(result));

        if (result.Success)
            return result.Output;

        var toolName = NormalizeToolName(request.ToolName);
        var rawError = (result.ErrorMessage ?? "").Trim();

        if (string.IsNullOrWhiteSpace(rawError))
            return $"{toolName} failed.";

        if (rawError.StartsWith($"{toolName} failed:", StringComparison.OrdinalIgnoreCase)
            || rawError.StartsWith("Unknown tool:", StringComparison.OrdinalIgnoreCase))
        {
            return rawError;
        }

        if (rawError.StartsWith("Tool argument is required:", StringComparison.OrdinalIgnoreCase))
        {
            var argument = ExtractArgumentName(rawError, "Tool argument is required:");
            return BuildMissingArgumentMessage(toolName, argument);
        }

        if (rawError.StartsWith("Tool argument must be an integer:", StringComparison.OrdinalIgnoreCase))
        {
            var argument = ExtractArgumentName(rawError, "Tool argument must be an integer:");
            return $"{toolName} failed: argument '{argument}' must be an integer." +
                   Environment.NewLine +
                   BuildExpectedArguments(toolName);
        }

        if (rawError.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
        {
            return $"{toolName} failed: current workspace is not a git repository (.git folder not found)." +
                   Environment.NewLine +
                   "Raw error:" +
                   Environment.NewLine +
                   rawError;
        }

        if (rawError.StartsWith("Unsupported command:", StringComparison.OrdinalIgnoreCase))
        {
            return $"run_command failed: {rawError}";
        }

        if (rawError.Contains("inside the active workspace", StringComparison.OrdinalIgnoreCase))
        {
            return $"{toolName} failed: path must stay inside the active workspace." +
                   Environment.NewLine +
                   BuildExpectedArguments(toolName);
        }

        if (rawError.StartsWith("File not found.", StringComparison.OrdinalIgnoreCase))
        {
            return $"{toolName} failed: target file was not found." +
                   Environment.NewLine +
                   BuildExpectedArguments(toolName);
        }

        if (rawError.StartsWith("Directory not found:", StringComparison.OrdinalIgnoreCase))
        {
            return $"{toolName} failed: target directory was not found." +
                   Environment.NewLine +
                   "Raw error:" +
                   Environment.NewLine +
                   rawError;
        }

        return rawError;
    }

    private string BuildExpectedArguments(string toolName)
    {
        var definition = _toolRegistryService.GetToolDefinition(toolName);
        if (definition is null || string.IsNullOrWhiteSpace(definition.ArgumentsDescription) || definition.ArgumentsDescription == "none")
            return "Expected: no arguments.";

        var arguments = definition.ArgumentsDescription
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(argument => $"{argument}=<{DescribeArgument(argument)}>")
            .ToArray();

        return "Expected: " + string.Join(", ", arguments) + ".";
    }

    private static string DescribeArgument(string argument)
    {
        return argument.ToLowerInvariant() switch
        {
            "path" => "relative workspace path",
            "working_directory" => "relative workspace folder",
            "pattern" => "search text",
            "content" => "text",
            "old_text" => "exact text",
            "new_text" => "replacement text",
            "project" => "relative project or solution path",
            "configuration" => "Debug or Release",
            "filter" => "test filter",
            "scope" => "auto, build, or test",
            "kind" => "all, solutions, projects, or test_projects",
            "timeout_seconds" => "seconds",
            "command" => "dotnet, git, echo, dir, or ls",
            "arguments" => "command arguments",
            "artifact_type" => "artifact type",
            "title" => "display title",
            "source_proposal_id" => "repair proposal id",
            _ => "value"
        };
    }

    private static string NormalizeToolName(string toolName)
    {
        return (toolName ?? "").Trim().ToLowerInvariant();
    }

    private string BuildMissingArgumentMessage(string toolName, string argument)
    {
        var lines = new List<string>
        {
            $"{toolName} failed: missing required argument '{argument}'.",
            BuildExpectedArguments(toolName)
        };

        if (argument == "path" && IsFileOperationTool(toolName))
        {
            lines.Add("If you already created or opened a file, say `append hello to that file`. Otherwise provide path=<relative workspace path>.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsFileOperationTool(string toolName)
    {
        return toolName is "create_file"
            or "write_file"
            or "append_file"
            or "read_file"
            or "save_output";
    }

    private static string ExtractArgumentName(string rawError, string prefix)
    {
        var argument = rawError[prefix.Length..].Trim();
        var parameterSuffixIndex = argument.IndexOf(" (", StringComparison.Ordinal);
        if (parameterSuffixIndex >= 0)
            argument = argument[..parameterSuffixIndex].Trim();

        return argument;
    }
}
