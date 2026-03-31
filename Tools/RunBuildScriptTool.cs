using System.IO;
using RAM.Models;
using RAM.Services;

namespace RAM.Tools;

public sealed class RunBuildScriptTool
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash",
        "cmd",
        "powershell"
    };

    private readonly CommandExecutionService _commandExecutionService;

    public RunBuildScriptTool(CommandExecutionService commandExecutionService)
    {
        _commandExecutionService = commandExecutionService;
    }

    public CommandExecutionResult Run(
        string workspaceRoot,
        string scriptPath,
        string scriptArguments,
        int timeoutSeconds,
        ExecutionGateDecision gateDecision)
    {
        var normalizedScriptPath = NormalizePath(scriptPath);
        if (!IsAllowedScriptName(Path.GetFileName(normalizedScriptPath)))
            throw new InvalidOperationException("run_build_script failed: only detected repo-local build/configure scripts are allowed.");

        var workingDirectory = NormalizePath(Path.GetDirectoryName(normalizedScriptPath) ?? ".");
        var scriptFileName = Path.GetFileName(normalizedScriptPath);
        var extension = Path.GetExtension(scriptFileName).ToLowerInvariant();

        var (command, arguments) = extension switch
        {
            ".sh" => ("bash", BuildShellArguments(scriptFileName, scriptArguments)),
            ".bat" or ".cmd" => ("cmd", BuildCmdArguments(scriptFileName, scriptArguments)),
            ".ps1" => ("powershell", BuildPowerShellArguments(scriptFileName, scriptArguments)),
            _ => throw new InvalidOperationException("run_build_script failed: unsupported script extension. Supported: .sh, .bat, .cmd, .ps1.")
        };

        return _commandExecutionService.ExecuteTrustedToolCommand(
            workspaceRoot,
            command,
            arguments,
            workingDirectory,
            timeoutSeconds,
            AllowedCommands,
            "run_build_script",
            gateDecision);
    }

    private static string BuildShellArguments(string scriptFileName, string scriptArguments)
    {
        var parts = new List<string> { QuoteValue(scriptFileName) };
        if (!string.IsNullOrWhiteSpace(scriptArguments))
            parts.Add(scriptArguments.Trim());

        return string.Join(" ", parts);
    }

    private static string BuildCmdArguments(string scriptFileName, string scriptArguments)
    {
        var parts = new List<string> { "/c", QuoteValue(scriptFileName) };
        if (!string.IsNullOrWhiteSpace(scriptArguments))
            parts.Add(scriptArguments.Trim());

        return string.Join(" ", parts);
    }

    private static string BuildPowerShellArguments(string scriptFileName, string scriptArguments)
    {
        var parts = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            QuoteValue(scriptFileName)
        };

        if (!string.IsNullOrWhiteSpace(scriptArguments))
            parts.Add(scriptArguments.Trim());

        return string.Join(" ", parts);
    }

    private static string NormalizePath(string path)
    {
        return (path ?? "").Replace('\\', '/');
    }

    private static bool IsAllowedScriptName(string fileName)
    {
        return string.Equals(fileName, "build.sh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "build.bat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "build.cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "build.ps1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "configure.sh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "configure.bat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "configure.cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "configure.ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteValue(string value)
    {
        return $"\"{value}\"";
    }
}
