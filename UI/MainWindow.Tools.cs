namespace RAM;

public partial class MainWindow
{
    private void ListFolderButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            var path = _toolService.PickFolderWithDialog();
            if (string.IsNullOrWhiteSpace(path))
                return;

            var result = _toolService.ListFolder(path);
            AddMessage("tool", $"ListFolder: {path}");
            AppendOutput(result);
        }
        catch (Exception ex)
        {
            AddMessage("error", ex.Message);
            AppendOutput("ERROR:" + Environment.NewLine + ex.Message);
        }
    }

    private void ReadFileButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            var path = _toolService.PickTextFileWithDialog();
            if (string.IsNullOrWhiteSpace(path))
                return;

            var result = _toolService.ReadTextFile(path);
            AddMessage("tool", $"ReadFile: {path}");
            AppendOutput(result);
        }
        catch (Exception ex)
        {
            AddMessage("error", ex.Message);
            AppendOutput("ERROR:" + Environment.NewLine + ex.Message);
        }
    }

    private void EchoButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            var input = PromptTextBox.Text;
            var result = _toolService.Echo(input);
            AddMessage("tool", "Echo");
            AppendOutput(result);
        }
        catch (Exception ex)
        {
            AddMessage("error", ex.Message);
            AppendOutput("ERROR:" + Environment.NewLine + ex.Message);
        }
    }

    private void ClearOutputButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        OutputTextBox.Clear();
    }

	private void ShowMemoryButton_Click(object sender, System.Windows.RoutedEventArgs e)
	{
		ShowRecentMemorySummaries();
	}

    private bool TryHandleSlashTool(string prompt, out string result)
    {
        result = "";

        if (!prompt.StartsWith("/tool ", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = prompt.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            result = "Invalid tool command.";
            return true;
        }

        var toolName = parts[1].Trim().ToLowerInvariant();
        var arg = parts.Length >= 3 ? parts[2].Trim() : "";

        result = toolName switch
        {
            "echo" => _toolService.Echo(arg),
            "listfiles" or "listfolder" => _toolService.ListFolder(arg),
            "readfile" => _toolService.ReadTextFile(arg),
            _ => $"Unknown tool: {toolName}"
        };

        return true;
    }
}