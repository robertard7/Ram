using RAM.Models;
using RAM.Services;
using System.Text;

namespace RAM.Tools;

public sealed class CreateDotnetProjectTool
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet"
    };

    private readonly CommandExecutionService _commandExecutionService;

    public CreateDotnetProjectTool(CommandExecutionService commandExecutionService)
    {
        _commandExecutionService = commandExecutionService;
    }

    public CommandExecutionResult Run(
        string workspaceRoot,
        string template,
        string projectName,
        string outputPath,
        string targetFramework,
        string templateSwitches,
        string workingDirectory,
        int timeoutSeconds,
        ExecutionGateDecision gateDecision)
    {
        var argumentsBuilder = new StringBuilder($"new {template} --name \"{projectName}\" --output \"{outputPath}\"");
        if (!string.IsNullOrWhiteSpace(targetFramework))
            argumentsBuilder.Append($" --framework \"{targetFramework}\"");
        if (!string.IsNullOrWhiteSpace(templateSwitches))
            argumentsBuilder.Append(' ').Append(templateSwitches.Trim());

        var arguments = argumentsBuilder.ToString();
        return _commandExecutionService.ExecuteTrustedToolCommand(
            workspaceRoot,
            "dotnet",
            arguments,
            string.IsNullOrWhiteSpace(workingDirectory) ? workspaceRoot : workingDirectory,
            timeoutSeconds,
            AllowedCommands,
            "create_dotnet_project",
            gateDecision);
    }
}
