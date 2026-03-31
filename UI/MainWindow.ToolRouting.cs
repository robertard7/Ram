using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private async Task<bool> TryHandleDeterministicToolRequestAsync(
        string prompt,
        ResolvedUserIntent? resolvedIntent,
        ResponseModeSelectionResult responseMode)
    {
        if (resolvedIntent is null)
            return false;

        AppendOutput(IsFileOperationTool(resolvedIntent.ToolRequest.ToolName)
            ? "Deterministic file-operation routing selected."
            : "Deterministic tool routing selected.");
        AppendOutput(resolvedIntent.ResolutionReason);
        AppendOutput($"Response mode selected: {FormatResponseMode(responseMode.Mode)}. {responseMode.Reason}");
        AppendDeterministicRecoveryLog(prompt, resolvedIntent.ToolRequest);

        var chainRun = await ExecuteControlledToolChainAsync(
            resolvedIntent.ToolRequest,
            "Resolved tool request",
            prompt,
            allowModelSummary: false,
            responseMode.Mode);
        if (chainRun.LastResult is not null && !chainRun.LastResult.Success)
            AppendOutput($"Deterministic route blocked: {chainRun.LastResult.Summary}");
        AddMessage("assistant", chainRun.Summary);
        return true;
    }

    private bool TryHandleToolFailure(ToolResult result)
    {
        if (result.Success || IsUnknownToolFailure(result))
            return false;

        AppendOutput("Tool execution failed. Skipping second model pass.");
        AddMessage("assistant", BuildOperationalFailureAssistantMessage(result.ErrorMessage));
        return true;
    }

    private string BuildMalformedToolRequestResponse()
    {
        var activeTarget = GetActiveTargetRelativePath();
        var lines = new List<string>
        {
            "I could not construct a valid tool request for that."
        };

        if (!string.IsNullOrWhiteSpace(activeTarget))
        {
            lines.Add($"Current active file: {activeTarget}.");
        }

        lines.Add("For file actions, include a relative path or refer to the current active file. For searches, include the filename pattern or text to search.");
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildDeterministicToolResponse(ToolRequest request, ToolResult result)
    {
        if (string.Equals(request.ToolName, "open_failure_context", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.ToolName, "plan_repair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.ToolName, "preview_patch_draft", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.ToolName, "apply_patch_draft", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.ToolName, "verify_patch_draft", StringComparison.OrdinalIgnoreCase))
        {
            return result.Success ? result.Output : BuildOperationalFailureAssistantMessage(result.ErrorMessage);
        }

        return result.Success
            ? $"I routed this directly to `{request.ToolName}`. See the output pane for the result."
            : BuildOperationalFailureAssistantMessage(result.ErrorMessage);
    }

    private static bool IsFileOperationTool(string toolName)
    {
        var normalized = (toolName ?? "").Trim().ToLowerInvariant();
        return normalized is "create_file"
            or "write_file"
            or "append_file"
            or "replace_in_file"
            or "read_file"
            or "read_file_chunk"
            or "file_info"
            or "make_dir"
            or "apply_patch_draft"
            or "save_output";
    }

    private string BuildOperationalFailureAssistantMessage(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return "The deterministic operation failed.";

        if (!errorMessage.Contains("missing required argument 'path'", StringComparison.OrdinalIgnoreCase))
            return errorMessage;

        var activeTarget = GetActiveTargetRelativePath();
        if (!string.IsNullOrWhiteSpace(activeTarget))
        {
            return errorMessage
                + Environment.NewLine
                + $"Current active file: {activeTarget}. You can say `append hello to that file` or use path={activeTarget}.";
        }

        return errorMessage
            + Environment.NewLine
            + "No active file is set. Say `append hello to notes/test.txt` or create/read a file first.";
    }

    private static string FormatResponseMode(ResponseMode mode)
    {
        return mode switch
        {
            ResponseMode.ToolRequired => "tool_required",
            ResponseMode.ChainRequired => "chain_required",
            ResponseMode.SummaryOnly => "summary_only",
            ResponseMode.ModelAllowed => "model_allowed",
            ResponseMode.ModelOptional => "model_optional",
            _ => "none"
        };
    }

    private void AppendDeterministicRecoveryLog(string prompt, ToolRequest request)
    {
        if (!request.TryGetArgument("path", out var path))
            return;

        if (!ReferencesCurrentFile(prompt))
            return;

        var activeTarget = GetActiveTargetRelativePath();
        if (string.IsNullOrWhiteSpace(activeTarget))
            return;

        if (!string.Equals(activeTarget, path, StringComparison.OrdinalIgnoreCase))
            return;

        AppendOutput($"Recovered active target path: {activeTarget}");
    }

    private static bool ReferencesCurrentFile(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var normalized = $" {prompt.Trim().ToLowerInvariant()} ";
        return normalized.Contains(" that file ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" this file ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" append to it ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" write it ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" read it ", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(" to it ", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(" it ", StringComparison.OrdinalIgnoreCase);
    }
}
