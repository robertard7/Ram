using RAM.Models;
using RAM.Services;

namespace RAM.Tools;

public sealed class DotnetBuildTool
{
    private readonly CommandExecutionService _commandExecutionService;

    public DotnetBuildTool(CommandExecutionService commandExecutionService)
    {
        _commandExecutionService = commandExecutionService;
    }

    public CommandExecutionResult Run(string workspaceRoot, string workingDirectory, string projectPath, string configuration, int timeoutSeconds, ExecutionGateDecision gateDecision)
    {
        var arguments = BuildArguments(projectPath, configuration);
        return _commandExecutionService.Execute(
            workspaceRoot,
            "dotnet",
            arguments,
            workingDirectory,
            timeoutSeconds,
            gateDecision);
    }

    private static string BuildArguments(string projectPath, string configuration)
    {
        var parts = new List<string>
        {
            "build",
            "--nologo"
        };

        if (!string.IsNullOrWhiteSpace(projectPath))
            parts.Add($"\"{projectPath}\"");

        if (!string.IsNullOrWhiteSpace(configuration))
        {
            parts.Add("-c");
            parts.Add(configuration);
        }

        return string.Join(" ", parts);
    }
}
