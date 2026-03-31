using RAM.Models;
using RAM.Services;

namespace RAM.Tools;

public sealed class DotnetTestTool
{
    private readonly CommandExecutionService _commandExecutionService;

    public DotnetTestTool(CommandExecutionService commandExecutionService)
    {
        _commandExecutionService = commandExecutionService;
    }

    public CommandExecutionResult Run(string workspaceRoot, string workingDirectory, string projectPath, string filter, int timeoutSeconds, ExecutionGateDecision gateDecision)
    {
        var arguments = BuildArguments(projectPath, filter);
        return _commandExecutionService.Execute(
            workspaceRoot,
            "dotnet",
            arguments,
            workingDirectory,
            timeoutSeconds,
            gateDecision);
    }

    private static string BuildArguments(string projectPath, string filter)
    {
        var parts = new List<string>
        {
            "test",
            "--nologo"
        };

        if (!string.IsNullOrWhiteSpace(projectPath))
            parts.Add($"\"{projectPath}\"");

        if (!string.IsNullOrWhiteSpace(filter))
        {
            parts.Add("--filter");
            parts.Add($"\"{filter}\"");
        }

        return string.Join(" ", parts);
    }
}
