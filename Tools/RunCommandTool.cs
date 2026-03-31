using RAM.Models;
using RAM.Services;

namespace RAM.Tools;

public sealed class RunCommandTool
{
    private readonly CommandExecutionService _commandExecutionService;

    public RunCommandTool(CommandExecutionService commandExecutionService)
    {
        _commandExecutionService = commandExecutionService;
    }

    public CommandExecutionResult Run(string workspaceRoot, string command, string arguments, string workingDirectory, int timeoutSeconds, ExecutionGateDecision gateDecision)
    {
        return _commandExecutionService.Execute(workspaceRoot, command, arguments, workingDirectory, timeoutSeconds, gateDecision);
    }
}
