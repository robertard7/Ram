using RAM.Models;
using RAM.Services;

namespace RAM.Tools;

public sealed class AddProjectToSolutionTool
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet"
    };

    private readonly CommandExecutionService _commandExecutionService;

    public AddProjectToSolutionTool(CommandExecutionService commandExecutionService)
    {
        _commandExecutionService = commandExecutionService;
    }

    public CommandExecutionResult Run(
        string workspaceRoot,
        string solutionPath,
        string projectPath,
        string workingDirectory,
        int timeoutSeconds,
        ExecutionGateDecision gateDecision)
    {
        var arguments = $"sln \"{solutionPath}\" add \"{projectPath}\"";
        return _commandExecutionService.ExecuteTrustedToolCommand(
            workspaceRoot,
            "dotnet",
            arguments,
            string.IsNullOrWhiteSpace(workingDirectory) ? workspaceRoot : workingDirectory,
            timeoutSeconds,
            AllowedCommands,
            "add_project_to_solution",
            gateDecision);
    }
}
