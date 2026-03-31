using System.Text;
using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private bool TryHandleBuilderRequest(string prompt, BuilderRequestKind requestKind)
    {
        if (requestKind != BuilderRequestKind.BuildRequest)
            return false;

        AppendOutput("Builder request detected." + Environment.NewLine + "Skipping tool execution path.");

        var draft = _intentDraftService.CreateDraft(
            prompt,
            _workspaceService.HasWorkspace() ? _workspaceService.WorkspaceRoot : "");

        var intentSaved = TrySaveIntentCandidate(draft);
        AppendOutput("Intent candidate created.");

        AddMessage("assistant", _intentDraftService.BuildBuilderResponse(draft, intentSaved));
        return true;
    }

    private bool TrySaveIntentCandidate(IntentDraft draft)
    {
        if (!_workspaceService.HasWorkspace())
            return false;

        if (!_intentDraftService.IsClearEnough(draft))
            return false;

        SaveIntentDraftToWorkspace(draft);
        AddMessage("system", $"Intent candidate saved: {draft.Title}");
        return true;
    }

    private async Task<string> RecoverUnknownToolAsync(
        string userPrompt,
        ToolRequest toolRequest,
        ToolResult toolResult,
        BuilderRequestKind requestKind)
    {
        AppendOutput("Unknown tool requested. Running recovery path.");

        var recoveryResponse = await _ollamaClient.GenerateAsync(
            EndpointTextBox.Text.Trim(),
            GetSelectedModel(),
            BuildUnknownToolRecoveryPrompt(userPrompt, toolRequest, toolResult));

        if (_toolRequestParser.LooksLikeToolRequest(recoveryResponse) || string.IsNullOrWhiteSpace(recoveryResponse))
        {
            AppendOutput("Recovery response still looked like a tool request. Falling back to local recovery response.");

            if (requestKind == BuilderRequestKind.BuildRequest)
            {
                var draft = _intentDraftService.CreateDraft(
                    userPrompt,
                    _workspaceService.HasWorkspace() ? _workspaceService.WorkspaceRoot : "");
                var intentSaved = TrySaveIntentCandidate(draft);
                return _intentDraftService.BuildUnknownToolFallbackResponse(toolRequest.ToolName, draft, intentSaved);
            }

            return BuildGenericUnknownToolResponse(toolRequest.ToolName);
        }

        return recoveryResponse;
    }

    private string BuildUnknownToolRecoveryPrompt(string userPrompt, ToolRequest toolRequest, ToolResult toolResult)
    {
        var sb = new StringBuilder();
        AppendPromptContext(sb);
        sb.AppendLine("The previous tool request failed because the requested tool is not in the current registry.");
        sb.AppendLine(_toolRegistryService.BuildPromptToolBlock());
        sb.AppendLine();
        sb.AppendLine("Recovery rules:");
        sb.AppendLine($"- The tool `{toolRequest.ToolName}` does not exist here.");
        sb.AppendLine("- Do not emit another tool request unless it uses one exact tool name from the registry.");
        sb.AppendLine("- If the user is describing a new capability, answer as the builder for this app.");
        sb.AppendLine("- Give one user-facing answer only.");
        sb.AppendLine();
        sb.AppendLine("Failed tool request:");
        sb.AppendLine($"name={toolRequest.ToolName}");

        if (!string.IsNullOrWhiteSpace(toolRequest.Reason))
        {
            sb.AppendLine($"reason={toolRequest.Reason}");
        }

        foreach (var argument in toolRequest.Arguments.Take(6))
        {
            sb.AppendLine($"{argument.Key}={TrimToolArgumentForPrompt(argument.Value)}");
        }

        sb.AppendLine();
        sb.AppendLine($"Tool error: {toolResult.ErrorMessage}");
        sb.AppendLine();
        sb.AppendLine("User request:");
        sb.AppendLine(userPrompt);

        return sb.ToString();
    }

    private static bool IsUnknownToolFailure(ToolResult result)
    {
        return !result.Success
            && !string.IsNullOrWhiteSpace(result.ErrorMessage)
            && result.ErrorMessage.StartsWith("Unknown tool:", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildGenericUnknownToolResponse(string toolName)
    {
        var availableTools = string.Join(", ", _toolRegistryService.GetAvailableTools().Select(x => x.Name));
        return $"The requested tool `{toolName}` is not in the current registry. Available tools are: {availableTools}. I did not retry the missing tool.";
    }
}
