using RAM.Models;
using RAM.Services;

namespace RAM.Tools;

public sealed class GitDiffTool
{
    private readonly CommandExecutionService _commandExecutionService;

    public GitDiffTool(CommandExecutionService commandExecutionService)
    {
        _commandExecutionService = commandExecutionService;
    }

    public CommandExecutionResult Run(string workspaceRoot, string workingDirectory, string path, int timeoutSeconds, ExecutionGateDecision gateDecision)
    {
        var arguments = string.IsNullOrWhiteSpace(path)
            ? "diff --"
            : $"diff -- \"{path}\"";

        return _commandExecutionService.Execute(
            workspaceRoot,
            "git",
            arguments,
            workingDirectory,
            timeoutSeconds,
            gateDecision);
    }
}
