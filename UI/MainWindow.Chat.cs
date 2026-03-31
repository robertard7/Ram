using System.Text;
using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private void AddMessage(string role, string content)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AddMessage(role, content));
            return;
        }

        _messages.Add(new ChatMessage
        {
            Role = role,
            Content = content,
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        });

        if (_messages.Count > 0)
        {
            ChatListBox.ScrollIntoView(_messages[^1]);
        }
    }

    private async void SendButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var prompt = PromptTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        SaveSettings();

        AddMessage("user", prompt);
        PromptTextBox.Clear();

        try
        {
            SetBusy(true);

            if (TrySaveIntentFromPrompt(prompt))
                return;

            if (await TryHandleTaskboardImportAsync(prompt))
                return;

            if (TryHandleSlashTool(prompt))
                return;

            if (await TryHandleTaskboardPromptAsync(prompt))
                return;

            var requestKind = _builderRequestClassifier.Classify(prompt);
            var workspaceRoot = _workspaceService.HasWorkspace() ? _workspaceService.WorkspaceRoot : "";
            var resolvedIntent = _userInputResolutionService.Resolve(
                prompt,
                requestKind,
                GetActiveTargetRelativePath(),
                workspaceRoot);
            var responseMode = _responseModeSelectionService.Select(prompt, requestKind, resolvedIntent);

            if (TryHandleExecutionFeedbackRequest(prompt, requestKind))
                return;

            if (await TryHandleDeterministicToolRequestAsync(prompt, resolvedIntent, responseMode))
                return;

            if (TryHandleBuilderRequest(prompt, requestKind))
                return;

            AppendOutput($"Response mode selected: {FormatResponseMode(responseMode.Mode)}. {responseMode.Reason}");
            AppendOutput("Sending prompt to model...");

            var firstPassResponse = await _ollamaClient.GenerateAsync(
                EndpointTextBox.Text.Trim(),
                GetSelectedModel(),
                BuildToolAwarePrompt(prompt, responseMode.Mode));

            var validation = _modelOutputValidationService.Validate(responseMode.Mode, firstPassResponse);
            if (!validation.IsValid)
            {
                HandleRejectedModelOutput(prompt, responseMode, validation, firstPassResponse);
                AddMessage("assistant", BuildRejectedModelFallbackResponse(responseMode.Mode, validation.RejectionReason));
                return;
            }

            var toolRequest = validation.ParsedToolRequest ?? _toolRequestParser.Parse(firstPassResponse);
            if (toolRequest is null)
            {
                if (_toolRequestParser.LooksLikeToolRequest(firstPassResponse))
                {
                    AppendOutput("Malformed tool request ignored. Using local fallback response.");
                    AddMessage("assistant", BuildMalformedToolRequestResponse());
                    return;
                }

                AddMessage("assistant", firstPassResponse);
                AppendOutput("Model response received.");
                return;
            }

            if ((responseMode.Mode is ResponseMode.ToolRequired or ResponseMode.ChainRequired)
                && !_toolRegistryService.HasTool(toolRequest.ToolName))
            {
                var rejection = new ModelOutputValidationResult
                {
                    IsValid = false,
                    RejectionReason = "invalid_model_step"
                };
                HandleRejectedModelOutput(prompt, responseMode, rejection, firstPassResponse);
                AddMessage("assistant", BuildRejectedModelFallbackResponse(responseMode.Mode, rejection.RejectionReason));
                return;
            }

            if (!_toolRegistryService.HasTool(toolRequest.ToolName))
            {
                var unknownToolResult = new ToolResult
                {
                    ToolName = toolRequest.ToolName,
                    Success = false,
                    OutcomeType = "validation_failure",
                    Summary = $"Unknown tool: {toolRequest.ToolName}",
                    Output = "",
                    ErrorMessage = $"Unknown tool: {toolRequest.ToolName}"
                };

                var recoveryResponse = await RecoverUnknownToolAsync(prompt, toolRequest, unknownToolResult, requestKind);
                AddMessage("assistant", recoveryResponse);
                AppendOutput("Unknown tool recovery response received.");
                return;
            }

            var chainRun = await ExecuteControlledToolChainAsync(
                toolRequest,
                "AI tool request",
                prompt,
                allowModelSummary: true,
                responseMode.Mode);
            AddMessage("assistant", chainRun.Summary);
            AppendOutput("Controlled tool chain response received.");
        }
        catch (Exception ex)
        {
            AddMessage("error", ex.Message);
            AppendOutput("ERROR:" + Environment.NewLine + ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool TrySaveIntentFromPrompt(string prompt)
    {
        const string prefix = "intent:";

        if (!prompt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var intentText = prompt[prefix.Length..].Trim();
        SaveIntentToWorkspace(intentText);

        AddMessage("system", $"Intent saved: {_currentIntent.Title}");
        AppendOutput($"Intent saved to workspace: {_currentIntent.Title}");

        return true;
    }

    private string BuildToolAwarePrompt(string userPrompt, ResponseMode responseMode)
    {
        var sb = new StringBuilder();
        AppendPromptContext(sb);

        sb.AppendLine(_toolRegistryService.BuildPromptToolBlock());
        sb.AppendLine();
        sb.AppendLine("Tool request rules:");
        sb.AppendLine("- You may request only an exact tool name from the registry above.");
        sb.AppendLine("- Never invent, rename, or combine tool names.");
        sb.AppendLine("- Prefer a specific tool before run_command.");
        sb.AppendLine("- If the user asks to build, create, add, implement, or modify a capability, do not emit a tool request. Respond as the builder for this app.");
        sb.AppendLine("- Do not invent file contents, folder contents, memory rows, or artifact rows.");
        sb.AppendLine("- If a tool is needed, emit only this exact format with no markdown fences:");
        sb.AppendLine("TOOL_REQUEST");
        sb.AppendLine("name=<tool_name>");
        sb.AppendLine("reason=<short reason>");
        sb.AppendLine("<argument>=<value>");

        if (responseMode is ResponseMode.ToolRequired or ResponseMode.ChainRequired)
        {
            sb.AppendLine("- A tool or controlled chain is required for this request.");
            sb.AppendLine("- Do not answer in prose, do not narrate steps, and do not emit code or fake tool logs.");
            sb.AppendLine("- Emit exactly one valid TOOL_REQUEST and nothing else.");
        }
        else
        {
            sb.AppendLine("- Use a tool request only when tool data is needed.");
            sb.AppendLine("- Otherwise answer normally.");
        }
        sb.AppendLine();
        sb.AppendLine("User request:");
        sb.AppendLine(userPrompt);

        return sb.ToString();
    }

    private string BuildToolResultPrompt(string userPrompt, ToolRequest request, ToolResult result)
    {
        var sb = new StringBuilder();
        AppendPromptContext(sb);
        sb.AppendLine("A tool has already been executed for this user request.");
        sb.AppendLine("Use the result below to answer the user.");
        sb.AppendLine("Do not request another tool.");
        sb.AppendLine();
        sb.AppendLine("Tool request:");
        sb.AppendLine($"name={request.ToolName}");

        if (!string.IsNullOrWhiteSpace(request.Reason))
        {
            sb.AppendLine($"reason={request.Reason}");
        }

        foreach (var argument in request.Arguments.Take(6))
        {
            sb.AppendLine($"{argument.Key}={TrimToolArgumentForPrompt(argument.Value)}");
        }

        sb.AppendLine();
        sb.AppendLine("Tool result:");
        sb.AppendLine($"name={result.ToolName}");
        sb.AppendLine($"success={result.Success}");
        sb.AppendLine($"outcome={TrimToolArgumentForPrompt(result.OutcomeType)}");

        if (!string.IsNullOrWhiteSpace(result.Summary))
        {
            sb.AppendLine($"summary={TrimToolArgumentForPrompt(result.Summary)}");
        }

        if (result.Success)
        {
            sb.AppendLine("output:");
            sb.AppendLine(TrimToolOutputForPrompt(result.Output));
        }
        else
        {
            sb.AppendLine($"error={result.ErrorMessage}");
        }

        if (!string.IsNullOrWhiteSpace(result.StructuredDataJson))
        {
            sb.AppendLine("structured_data:");
            sb.AppendLine(TrimToolOutputForPrompt(result.StructuredDataJson));
        }

        sb.AppendLine();
        sb.AppendLine("User request:");
        sb.AppendLine(userPrompt);

        return sb.ToString();
    }

    private void AppendPromptContext(StringBuilder sb)
    {
        var hasIntent = !string.IsNullOrWhiteSpace(_currentIntent.Objective);
        var hasArtifact = _activeArtifact is not null
            && (!string.IsNullOrWhiteSpace(_activeArtifact.RelativePath)
                || !string.IsNullOrWhiteSpace(_activeArtifact.Content))
            && !_artifactClassificationService.IsTaskboardArtifact(_activeArtifact);
        var activeTarget = GetActiveTargetRelativePath();

        if (hasIntent)
        {
            sb.AppendLine("Current saved intent:");
            sb.AppendLine($"Title: {_currentIntent.Title}");
            sb.AppendLine($"Objective: {_currentIntent.Objective}");

            if (!string.IsNullOrWhiteSpace(_currentIntent.Notes))
            {
                sb.AppendLine($"Notes: {_currentIntent.Notes}");
            }

            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(activeTarget))
        {
            sb.AppendLine("Current active target file:");
            sb.AppendLine(activeTarget);
            sb.AppendLine();
        }

        if (hasArtifact)
        {
            sb.AppendLine("Current active artifact:");
            sb.AppendLine($"Title: {_activeArtifact!.Title}");
            sb.AppendLine($"Type: {_activeArtifact.ArtifactType}");
            sb.AppendLine($"Path: {_activeArtifact.RelativePath}");

            if (!string.IsNullOrWhiteSpace(_activeArtifact.Summary))
            {
                sb.AppendLine($"Summary: {_activeArtifact.Summary}");
            }

            if (!string.IsNullOrWhiteSpace(_activeArtifact.Content))
            {
                sb.AppendLine("Content:");
                sb.AppendLine(TrimArtifactContent(_activeArtifact.Content));
            }

            sb.AppendLine();
        }
    }

    private static string TrimArtifactContent(string content)
    {
        const int maxChars = 4000;

        if (string.IsNullOrWhiteSpace(content))
            return "";

        if (content.Length <= maxChars)
            return content;

        return content[..maxChars] + Environment.NewLine + Environment.NewLine + "[ARTIFACT CONTENT TRUNCATED]";
    }

    private static string TrimToolOutputForPrompt(string output)
    {
        const int maxChars = 8000;

        if (string.IsNullOrWhiteSpace(output))
            return "";

        if (output.Length <= maxChars)
            return output;

        return output[..maxChars] + Environment.NewLine + Environment.NewLine + "[TOOL OUTPUT TRUNCATED]";
    }

    private static string TrimToolArgumentForPrompt(string value)
    {
        const int maxChars = 300;

        if (string.IsNullOrWhiteSpace(value))
            return "";

        if (value.Length <= maxChars)
            return value;

        return value[..maxChars] + "...";
    }

    private void HandleRejectedModelOutput(
        string prompt,
        ResponseModeSelectionResult responseMode,
        ModelOutputValidationResult validation,
        string rejectedOutput)
    {
        AppendOutput($"Model output rejected: {validation.RejectionReason}.");
        AppendOutput(responseMode.Mode is ResponseMode.ToolRequired or ResponseMode.ChainRequired
            ? "Falling back to deterministic tool-or-chain refusal."
            : "Falling back to deterministic local response.");

        if (_workspaceService.HasWorkspace())
        {
            var artifact = new ArtifactRecord
            {
                ArtifactType = "model_output_rejection",
                Title = "Rejected model output",
                RelativePath = $".ram/model-output-rejections/{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json",
                Content = BuildRejectedModelOutputPayload(prompt, responseMode, validation, rejectedOutput),
                Summary = $"response_mode={FormatResponseMode(responseMode.Mode)} reason={validation.RejectionReason}"
            };

            _ramDbService.SaveArtifact(_workspaceService.WorkspaceRoot, artifact);
            AppendPendingDatabaseMessages();
        }
    }

    private string BuildRejectedModelFallbackResponse(ResponseMode responseMode, string rejectionReason)
    {
        if (responseMode == ResponseMode.ToolRequired)
        {
            var lines = new List<string>
            {
                "RAM rejected free-form model output because this request requires a tool-backed action.",
                $"Reason: {rejectionReason}.",
                "No tool ran because RAM did not receive a valid tool request."
            };

            var activeTarget = GetActiveTargetRelativePath();
            if (!string.IsNullOrWhiteSpace(activeTarget))
                lines.Add($"Current active file: {activeTarget}.");

            lines.Add("Use a clearer relative path, filename pattern, or explicit build target and try again.");
            return string.Join(Environment.NewLine, lines);
        }

        if (responseMode == ResponseMode.ChainRequired)
        {
            return "RAM rejected free-form model output because this request requires a controlled chain."
                + Environment.NewLine
                + $"Reason: {rejectionReason}."
                + Environment.NewLine
                + "No chain advanced because RAM did not receive a valid allowed next step."
                + Environment.NewLine
                + "Use a clearer repair/build target or a more specific operational prompt and try again.";
        }

        return "RAM rejected invalid model output and used a deterministic fallback instead.";
    }

    private static string BuildRejectedModelOutputPayload(
        string prompt,
        ResponseModeSelectionResult responseMode,
        ModelOutputValidationResult validation,
        string rejectedOutput)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            created_utc = DateTime.UtcNow.ToString("O"),
            prompt,
            response_mode = FormatResponseMode(responseMode.Mode),
            response_mode_reason = responseMode.Reason,
            rejection_reason = validation.RejectionReason,
            rejected_output = rejectedOutput
        }, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
}
