using System.Text;
using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private void AddMessage(string role, string content)
    {
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

            if (TryHandleSlashTool(prompt, out var toolResult))
            {
                AddMessage("tool", toolResult);
                AppendOutput(toolResult);
                return;
            }

            AppendOutput("Sending prompt to model...");

            var response = await _ollamaClient.GenerateAsync(
                EndpointTextBox.Text.Trim(),
                GetSelectedModel(),
                BuildPromptWithIntent(prompt));

            AddMessage("assistant", response);
            AppendOutput("Model response received.");
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

    private string BuildPromptWithIntent(string userPrompt)
    {
        if (string.IsNullOrWhiteSpace(_currentIntent.Objective))
            return userPrompt;

        var sb = new StringBuilder();

        sb.AppendLine("Current saved intent:");
        sb.AppendLine($"Title: {_currentIntent.Title}");
        sb.AppendLine($"Objective: {_currentIntent.Objective}");

        if (!string.IsNullOrWhiteSpace(_currentIntent.Notes))
        {
            sb.AppendLine($"Notes: {_currentIntent.Notes}");
        }

        sb.AppendLine();
        sb.AppendLine("User request:");
        sb.AppendLine(userPrompt);

        return sb.ToString();
    }
}