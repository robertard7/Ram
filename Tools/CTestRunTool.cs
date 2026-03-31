using RAM.Models;
using RAM.Services;

namespace RAM.Tools;

public sealed class CTestRunTool
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ctest"
    };

    private readonly CommandExecutionService _commandExecutionService;

    public CTestRunTool(CommandExecutionService commandExecutionService)
    {
        _commandExecutionService = commandExecutionService;
    }

    public CommandExecutionResult Run(
        string workspaceRoot,
        string directory,
        string configuration,
        int timeoutSeconds,
        ExecutionGateDecision gateDecision)
    {
        var arguments = BuildArguments(directory, configuration);
        return _commandExecutionService.ExecuteTrustedToolCommand(
            workspaceRoot,
            "ctest",
            arguments,
            workspaceRoot,
            timeoutSeconds,
            AllowedCommands,
            "ctest_run",
            gateDecision);
    }

    private static string BuildArguments(string directory, string configuration)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(directory))
        {
            parts.Add("--test-dir");
            parts.Add(QuoteValue(directory));
        }

        if (!string.IsNullOrWhiteSpace(configuration))
        {
            parts.Add("--build-config");
            parts.Add(QuoteValue(configuration));
        }

        parts.Add("--output-on-failure");
        return string.Join(" ", parts);
    }

    private static string QuoteValue(string value)
    {
        return $"\"{value}\"";
    }
}
