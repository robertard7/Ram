using RAM.Models;
using RAM.Services;

namespace RAM.Tools;

public sealed class CMakeConfigureTool
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmake"
    };

    private readonly CommandExecutionService _commandExecutionService;

    public CMakeConfigureTool(CommandExecutionService commandExecutionService)
    {
        _commandExecutionService = commandExecutionService;
    }

    public CommandExecutionResult Run(
        string workspaceRoot,
        string sourceDirectory,
        string buildDirectory,
        string generator,
        string configuration,
        int timeoutSeconds,
        ExecutionGateDecision gateDecision)
    {
        var arguments = BuildArguments(sourceDirectory, buildDirectory, generator, configuration);
        return _commandExecutionService.ExecuteTrustedToolCommand(
            workspaceRoot,
            "cmake",
            arguments,
            workspaceRoot,
            timeoutSeconds,
            AllowedCommands,
            "cmake_configure",
            gateDecision);
    }

    private static string BuildArguments(string sourceDirectory, string buildDirectory, string generator, string configuration)
    {
        var parts = new List<string>
        {
            "-S",
            QuoteValue(string.IsNullOrWhiteSpace(sourceDirectory) ? "." : sourceDirectory),
            "-B",
            QuoteValue(string.IsNullOrWhiteSpace(buildDirectory) ? "build" : buildDirectory)
        };

        if (!string.IsNullOrWhiteSpace(generator))
        {
            parts.Add("-G");
            parts.Add(QuoteValue(generator));
        }

        if (!string.IsNullOrWhiteSpace(configuration))
        {
            parts.Add($"-DCMAKE_BUILD_TYPE={configuration}");
        }

        return string.Join(" ", parts);
    }

    private static string QuoteValue(string value)
    {
        return $"\"{value}\"";
    }
}
