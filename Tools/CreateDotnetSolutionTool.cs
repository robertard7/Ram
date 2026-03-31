using RAM.Models;
using RAM.Services;

namespace RAM.Tools;

public sealed class CreateDotnetSolutionTool
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet"
    };

    private readonly CommandExecutionService _commandExecutionService;

    public CreateDotnetSolutionTool(CommandExecutionService commandExecutionService)
    {
        _commandExecutionService = commandExecutionService;
    }

    public CommandExecutionResult Run(
        string workspaceRoot,
        string solutionName,
        string workingDirectory,
        int timeoutSeconds,
        ExecutionGateDecision gateDecision)
    {
        var arguments = $"new sln --name \"{solutionName}\"";
        return _commandExecutionService.ExecuteTrustedToolCommand(
            workspaceRoot,
            "dotnet",
            arguments,
            string.IsNullOrWhiteSpace(workingDirectory) ? workspaceRoot : workingDirectory,
            timeoutSeconds,
            AllowedCommands,
            "create_dotnet_solution",
            gateDecision);
    }
}
