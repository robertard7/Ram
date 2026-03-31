using RAM.Models;
using RAM.Services;

namespace RAM.Tools;

public sealed class CMakeBuildTool
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmake"
    };

    private readonly CommandExecutionService _commandExecutionService;

    public CMakeBuildTool(CommandExecutionService commandExecutionService)
    {
        _commandExecutionService = commandExecutionService;
    }

    public CommandExecutionResult Run(
        string workspaceRoot,
        string buildDirectory,
        string target,
        string configuration,
        int timeoutSeconds,
        ExecutionGateDecision gateDecision)
    {
        var policy = _commandExecutionService.GetSafetyPolicy("cmake_build");
        var arguments = BuildArguments(buildDirectory, target, configuration, policy.MaxParallelJobs);
        return _commandExecutionService.ExecuteTrustedToolCommand(
            workspaceRoot,
            "cmake",
            arguments,
            workspaceRoot,
            timeoutSeconds,
            AllowedCommands,
            "cmake_build",
            gateDecision);
    }

    private static string BuildArguments(string buildDirectory, string target, string configuration, int maxParallelJobs)
    {
        var parts = new List<string>
        {
            "--build",
            QuoteValue(string.IsNullOrWhiteSpace(buildDirectory) ? "build" : buildDirectory)
        };

        if (!string.IsNullOrWhiteSpace(configuration))
        {
            parts.Add("--config");
            parts.Add(QuoteValue(configuration));
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            parts.Add("--target");
            parts.Add(QuoteValue(target));
        }

        if (maxParallelJobs > 0)
        {
            parts.Add("--parallel");
            parts.Add(maxParallelJobs.ToString());
        }

        return string.Join(" ", parts);
    }

    private static string QuoteValue(string value)
    {
        return $"\"{value}\"";
    }
}
