using RAM.Models;
using RAM.Services;

namespace RAM.Tools;

public sealed class AddDotnetProjectReferenceTool
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet"
    };

    private readonly CommandExecutionService _commandExecutionService;

    public AddDotnetProjectReferenceTool(CommandExecutionService commandExecutionService)
    {
        _commandExecutionService = commandExecutionService;
    }

    public CommandExecutionResult Run(
        string workspaceRoot,
        string projectPath,
        string referencePath,
        string workingDirectory,
        int timeoutSeconds,
        ExecutionGateDecision gateDecision)
    {
        var arguments = $"add \"{projectPath}\" reference \"{referencePath}\"";
        return _commandExecutionService.ExecuteTrustedToolCommand(
            workspaceRoot,
            "dotnet",
            arguments,
            string.IsNullOrWhiteSpace(workingDirectory) ? workspaceRoot : workingDirectory,
            timeoutSeconds,
            AllowedCommands,
            "add_dotnet_project_reference",
            gateDecision);
    }
}
