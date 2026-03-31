using RAM.Models;
using RAM.Services;

namespace RAM.Tools;

public sealed class NinjaBuildTool
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ninja"
    };

    private readonly CommandExecutionService _commandExecutionService;

    public NinjaBuildTool(CommandExecutionService commandExecutionService)
    {
        _commandExecutionService = commandExecutionService;
    }

    public CommandExecutionResult Run(
        string workspaceRoot,
        string workingDirectory,
        string target,
        int timeoutSeconds,
        ExecutionGateDecision gateDecision)
    {
        var policy = _commandExecutionService.GetSafetyPolicy("ninja_build");
        var arguments = BuildArguments(target, policy.MaxParallelJobs);
        return _commandExecutionService.ExecuteTrustedToolCommand(
            workspaceRoot,
            "ninja",
            arguments,
            string.IsNullOrWhiteSpace(workingDirectory) ? workspaceRoot : workingDirectory,
            timeoutSeconds,
            AllowedCommands,
            "ninja_build",
            gateDecision);
    }

    private static string BuildArguments(string target, int maxParallelJobs)
    {
        var parts = new List<string>();

        if (maxParallelJobs > 0)
            parts.Add($"-j{maxParallelJobs}");

        if (!string.IsNullOrWhiteSpace(target))
            parts.Add(QuoteValue(target));

        return string.Join(" ", parts);
    }

    private static string QuoteValue(string value)
    {
        return $"\"{value}\"";
    }
}
