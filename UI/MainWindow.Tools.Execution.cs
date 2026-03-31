using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private ToolResult ExecuteToolRequest(ToolRequest request, string source, string userPrompt = "")
    {
        var recovery = _toolArgumentRecoveryService.Recover(
            request,
            userPrompt,
            GetActiveTargetRelativePath(),
            _activeArtifact);

        var executionRequest = recovery.Request;
        var activeTarget = GetActiveTargetRelativePath();
        if (!string.IsNullOrWhiteSpace(activeTarget) && !executionRequest.TryGetArgument("active_target", out _))
        {
            executionRequest.Arguments["active_target"] = activeTarget;
        }

        if (_activeArtifact is not null && !string.IsNullOrWhiteSpace(_activeArtifact.RelativePath))
        {
            if (!executionRequest.TryGetArgument("active_artifact_path", out _))
                executionRequest.Arguments["active_artifact_path"] = _activeArtifact.RelativePath;

            if (!executionRequest.TryGetArgument("active_artifact_type", out _))
                executionRequest.Arguments["active_artifact_type"] = _activeArtifact.ArtifactType;
        }

        AttachExecutionSourceMetadata(executionRequest, source);

        AddMessage("tool", $"{source}: {executionRequest.ToolName}");

        var result = string.IsNullOrWhiteSpace(recovery.FailureMessage)
            ? _toolExecutionService.Execute(executionRequest)
            : new ToolResult
            {
                ToolName = executionRequest.ToolName,
                Success = false,
                OutcomeType = "resolution_failure",
                Summary = recovery.FailureMessage,
                Output = "",
                ErrorMessage = recovery.FailureMessage
            };
        AppendPendingDatabaseMessages();

        RefreshToolStateAfterExecution(executionRequest, result);

        var lines = new List<string>
        {
            $"{source}: {executionRequest.ToolName}"
        };

        if (!string.IsNullOrWhiteSpace(executionRequest.Reason))
        {
            lines.Add($"Reason: {executionRequest.Reason}");
        }

        if (recovery.Notes.Count > 0)
        {
            lines.Add("Recovered arguments: " + string.Join("; ", recovery.Notes));
        }

        lines.Add(result.Success
            ? $"Tool executed successfully: {result.ToolName}"
            : $"Tool execution failed: {result.ToolName}");

        if (!string.IsNullOrWhiteSpace(result.Summary))
        {
            lines.Add($"Summary: {result.Summary}");
        }

        var details = result.Success ? result.Output : result.ErrorMessage;
        if (!string.IsNullOrWhiteSpace(details))
        {
            lines.Add(details);
        }

        AppendOutput(string.Join(Environment.NewLine, lines));

        AddMessage(
            result.Success ? "tool" : "error",
            result.Success
                ? $"{executionRequest.ToolName} succeeded."
                : $"{executionRequest.ToolName} failed: {result.ErrorMessage}");

        return result;
    }

    private static void AttachExecutionSourceMetadata(ToolRequest request, string source)
    {
        if (request.ExecutionSourceType != ExecutionSourceType.Unknown)
            return;

        request.ExecutionSourceType = IsExecutionBackedTool(request.ToolName)
            ? ExecutionSourceType.BuildTool
            : ExecutionSourceType.ManualUserRequest;
        request.ExecutionSourceName = string.IsNullOrWhiteSpace(source) ? request.ToolName : source;
        request.ExecutionAllowed = true;
        if (!request.IsAutomaticTrigger && string.IsNullOrWhiteSpace(request.ExecutionPolicyMode))
            request.ExecutionPolicyMode = "explicit_manual";
    }

    private static bool IsExecutionBackedTool(string toolName)
    {
        var normalized = (toolName ?? "").Trim().ToLowerInvariant();
        return normalized is "run_command"
            or "git_status"
            or "git_diff"
            or "dotnet_build"
            or "dotnet_test"
            or "cmake_configure"
            or "cmake_build"
            or "make_build"
            or "ninja_build"
            or "run_build_script";
    }
}
