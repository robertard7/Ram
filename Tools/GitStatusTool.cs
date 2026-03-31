using RAM.Models;
using RAM.Services;

namespace RAM.Tools;

public sealed class GitStatusTool
{
    private readonly CommandExecutionService _commandExecutionService;

    public GitStatusTool(CommandExecutionService commandExecutionService)
    {
        _commandExecutionService = commandExecutionService;
    }

    public CommandExecutionResult Run(string workspaceRoot, string workingDirectory, int timeoutSeconds, ExecutionGateDecision gateDecision)
    {
        return _commandExecutionService.Execute(
            workspaceRoot,
            "git",
            "status --short --branch",
            workingDirectory,
            timeoutSeconds,
            gateDecision);
    }
}
