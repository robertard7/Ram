using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private void ListFolderButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_workspaceService.HasWorkspace())
        {
            AppendOutput("No workspace set. Cannot list folders.");
            return;
        }

        try
        {
            var path = _toolService.PickFolderWithDialog(_workspaceService.WorkspaceRoot);
            if (string.IsNullOrWhiteSpace(path))
                return;

            var request = new ToolRequest
            {
                ToolName = "list_folder",
                Reason = "User clicked List Folder."
            };

            request.Arguments["path"] = path;
            ExecuteToolRequest(request, "Manual tool request");
        }
        catch (Exception ex)
        {
            AddMessage("error", ex.Message);
            AppendOutput("ERROR:" + Environment.NewLine + ex.Message);
        }
    }

    private void ReadFileButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_workspaceService.HasWorkspace())
        {
            AppendOutput("No workspace set. Cannot read files.");
            return;
        }

        try
        {
            var path = _toolService.PickTextFileWithDialog(_workspaceService.WorkspaceRoot);
            if (string.IsNullOrWhiteSpace(path))
                return;

            var request = new ToolRequest
            {
                ToolName = "read_file",
                Reason = "User clicked Read File."
            };

            request.Arguments["path"] = path;
            ExecuteToolRequest(request, "Manual tool request");
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

    private bool TryHandleSlashTool(string prompt)
    {
        if (!prompt.StartsWith("/tool ", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = prompt.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            AppendOutput("Invalid tool command.");
            return true;
        }

        var toolName = parts[1].Trim().ToLowerInvariant();
        var arg = parts.Length >= 3 ? parts[2].Trim() : "";

        switch (toolName)
        {
            case "echo":
                AddMessage("tool", "Slash tool request: echo");
                AppendOutput(_toolService.Echo(arg));
                return true;

            case "listfiles":
            case "listfolder":
                ExecuteToolRequest(new ToolRequest
                {
                    ToolName = "list_folder",
                    Reason = "User issued a slash tool command.",
                    Arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["path"] = arg
                    }
                }, "Slash tool request");
                return true;

            case "readfile":
                ExecuteToolRequest(new ToolRequest
                {
                    ToolName = "read_file",
                    Reason = "User issued a slash tool command.",
                    Arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["path"] = arg
                    }
                }, "Slash tool request");
                return true;

            case "showmemory":
                ExecuteToolRequest(new ToolRequest
                {
                    ToolName = "show_memory",
                    Reason = "User issued a slash tool command."
                }, "Slash tool request");
                return true;

            case "showartifacts":
                ExecuteToolRequest(new ToolRequest
                {
                    ToolName = "show_artifacts",
                    Reason = "User issued a slash tool command."
                }, "Slash tool request");
                return true;

            case "runcommand":
                toolName = "run_command";
                break;

            case "gitstatus":
                toolName = "git_status";
                break;

            case "gitdiff":
                toolName = "git_diff";
                break;

            case "dotnetbuild":
                toolName = "dotnet_build";
                break;

            case "dotnettest":
                toolName = "dotnet_test";
                break;

            default:
                break;
        }

        if (TryExecuteGenericSlashTool(toolName, arg))
            return true;

        AddMessage("error", $"Unknown tool: {toolName}");
        AppendOutput($"Unknown tool: {toolName}");
        return true;
    }
}
