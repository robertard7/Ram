using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RAM.Models;

namespace RAM.Services;

public sealed class CommandExecutionService
{
    private static int ExecutionAttemptCount;
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet",
        "git",
        "echo",
        "dir",
        "ls"
    };

    private readonly ExecutionSafetyPolicyService _executionSafetyPolicyService = new();

    public ExecutionSafetyPolicy GetSafetyPolicy(string commandFamily)
    {
        return _executionSafetyPolicyService.GetPolicy(commandFamily);
    }

    public static void ResetExecutionAttemptCount()
    {
        Interlocked.Exchange(ref ExecutionAttemptCount, 0);
    }

    public static int GetExecutionAttemptCount()
    {
        return Volatile.Read(ref ExecutionAttemptCount);
    }

    public CommandExecutionResult Execute(
        string workspaceRoot,
        string command,
        string arguments,
        string workingDirectory,
        int timeoutSeconds,
        ExecutionGateDecision gateDecision)
    {
        Interlocked.Increment(ref ExecutionAttemptCount);

        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace not found: {workspaceRoot}");

        var normalizedCommand = NormalizeCommand(command);
        var gatePolicy = string.Equals(normalizedCommand, "dotnet", StringComparison.OrdinalIgnoreCase)
            ? _executionSafetyPolicyService.GetPolicy("dotnet")
            : _executionSafetyPolicyService.GetPolicy("generic");
        if (!IsGateApproved(gateDecision))
            return CreateExecutionGateBlockedResult(workspaceRoot, normalizedCommand, arguments, workingDirectory, gatePolicy, gateDecision);

        if (!AllowedCommands.Contains(normalizedCommand))
            throw new InvalidOperationException($"Unsupported command: {normalizedCommand}. Allowed commands: dotnet, git, echo, dir, ls.");

        var resolvedWorkingDirectory = ResolveWorkingDirectory(workspaceRoot, workingDirectory);
        var normalizedArguments = arguments?.Trim() ?? "";

        return normalizedCommand switch
        {
            "echo" => ExecuteEcho(resolvedWorkingDirectory, normalizedArguments, timeoutSeconds, gateDecision),
            "dir" or "ls" => ExecuteDirectoryListing(workspaceRoot, resolvedWorkingDirectory, normalizedArguments, timeoutSeconds, normalizedCommand, gateDecision),
            "git" => ExecuteExternalProcess(
                workspaceRoot,
                "git",
                ValidateGitArguments(normalizedArguments),
                resolvedWorkingDirectory,
                timeoutSeconds,
                _executionSafetyPolicyService.GetPolicy("generic"),
                gateDecision),
            "dotnet" => ExecuteExternalProcess(
                workspaceRoot,
                "dotnet",
                ValidateDotnetArguments(normalizedArguments),
                resolvedWorkingDirectory,
                timeoutSeconds,
                _executionSafetyPolicyService.GetPolicy("dotnet"),
                gateDecision),
            _ => throw new InvalidOperationException($"Unsupported command: {normalizedCommand}")
        };
    }

    public CommandExecutionResult ExecuteTrustedToolCommand(
        string workspaceRoot,
        string command,
        string arguments,
        string workingDirectory,
        int timeoutSeconds,
        IReadOnlyCollection<string> allowedCommands,
        string commandFamily,
        ExecutionGateDecision gateDecision)
    {
        Interlocked.Increment(ref ExecutionAttemptCount);

        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace not found: {workspaceRoot}");

        var normalizedCommand = NormalizeCommand(command);
        if (allowedCommands is null || !allowedCommands.Contains(normalizedCommand, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported command: {normalizedCommand}. Allowed commands: {string.Join(", ", allowedCommands ?? Array.Empty<string>())}.");
        }

        var policy = _executionSafetyPolicyService.GetPolicy(commandFamily);
        if (!IsGateApproved(gateDecision))
            return CreateExecutionGateBlockedResult(workspaceRoot, normalizedCommand, arguments, workingDirectory, policy, gateDecision);

        if (IsScriptCommand(normalizedCommand) && !policy.AllowScriptExecution)
        {
            return CreateSafetyBlockedResult(
                workspaceRoot,
                normalizedCommand,
                arguments,
                workingDirectory,
                policy,
                $"Execution safety: blocked {commandFamily} because script execution is not allowed for this tool family.",
                gateDecision);
        }

        var resolvedWorkingDirectory = ResolveWorkingDirectory(workspaceRoot, workingDirectory);
        return ExecuteExternalProcess(workspaceRoot, normalizedCommand, arguments?.Trim() ?? "", resolvedWorkingDirectory, timeoutSeconds, policy, gateDecision);
    }

    public string FormatDetailedResult(CommandExecutionResult result)
    {
        var lines = new List<string>
        {
            $"Command: {result.DisplayCommand}",
            $"Working directory: {result.WorkingDirectory}",
            $"Exit code: {result.ExitCode}"
        };

        if (!string.IsNullOrWhiteSpace(result.ExecutionSourceSummary))
            lines.Add($"Execution source: {result.ExecutionSourceSummary}");

        if (!string.IsNullOrWhiteSpace(result.GateDecisionSummary))
            lines.Add($"Execution gate: {result.GateDecisionSummary}");

        if (!string.IsNullOrWhiteSpace(result.SafetyProfileSummary))
            lines.Add($"Execution safety profile: {result.SafetyProfileSummary}");

        if (!string.IsNullOrWhiteSpace(result.SafetyMessage))
            lines.Add(result.SafetyMessage);
        else if (result.TimedOut)
            lines.Add($"Timed out after {result.TimeoutSeconds} second(s).");

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            lines.Add("Stdout:");
            lines.Add(result.StandardOutput);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            lines.Add("Stderr:");
            lines.Add(result.StandardError);
        }

        if (result.OutputWasTruncated)
            lines.Add("[OUTPUT TRUNCATED]");

        return string.Join(Environment.NewLine, lines);
    }

    public string FormatCompactResult(string label, CommandExecutionResult result)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(result.ExecutionSourceSummary))
            lines.Add($"Execution source: {result.ExecutionSourceSummary}");

        if (!string.IsNullOrWhiteSpace(result.GateDecisionSummary))
            lines.Add($"Execution gate: {result.GateDecisionSummary}");

        if (!string.IsNullOrWhiteSpace(result.SafetyProfileSummary))
            lines.Add($"Execution safety profile: {result.SafetyProfileSummary}");

        lines.Add($"{label}: exit code {result.ExitCode}");

        if (!string.IsNullOrWhiteSpace(result.SafetyMessage))
            lines.Add(result.SafetyMessage);
        else if (result.TimedOut)
            lines.Add($"Timed out after {result.TimeoutSeconds} second(s).");

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            lines.Add(result.StandardOutput);

        if (!string.IsNullOrWhiteSpace(result.StandardError))
            lines.Add($"stderr:{Environment.NewLine}{result.StandardError}");

        if (result.OutputWasTruncated)
            lines.Add("[OUTPUT TRUNCATED]");

        return string.Join(Environment.NewLine, lines);
    }

    private static CommandExecutionResult ExecuteEcho(string workingDirectory, string arguments, int timeoutSeconds, ExecutionGateDecision gateDecision)
    {
        return new CommandExecutionResult
        {
            DisplayCommand = string.IsNullOrWhiteSpace(arguments) ? "echo" : $"echo {arguments}",
            WorkingDirectory = workingDirectory,
            ExitCode = 0,
            TimeoutSeconds = timeoutSeconds,
            StandardOutput = arguments,
            ExecutionAttempted = false,
            ExecutionSourceSummary = BuildExecutionSourceSummary(gateDecision),
            GateDecisionId = gateDecision?.DecisionId ?? "",
            GateDecisionSummary = gateDecision?.Summary ?? ""
        };
    }

    private static CommandExecutionResult ExecuteDirectoryListing(
        string workspaceRoot,
        string workingDirectory,
        string arguments,
        int timeoutSeconds,
        string commandName,
        ExecutionGateDecision gateDecision)
    {
        var targetDirectory = ResolveDirectoryListingTarget(workspaceRoot, workingDirectory, arguments);
        var directories = Directory.GetDirectories(targetDirectory)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var files = Directory.GetFiles(targetDirectory)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var output = new StringBuilder();
        output.AppendLine($"Directory: {targetDirectory}");
        output.AppendLine();
        output.AppendLine("[Directories]");

        foreach (var directory in directories)
            output.AppendLine($"  {Path.GetFileName(directory)}");

        output.AppendLine();
        output.AppendLine("[Files]");

        foreach (var file in files)
        {
            var info = new FileInfo(file);
            output.AppendLine($"  {info.Name}  ({info.Length} bytes)");
        }

        var boundedOutput = BoundText(output.ToString(), 12000, out var truncated);

        return new CommandExecutionResult
        {
            DisplayCommand = string.IsNullOrWhiteSpace(arguments)
                ? commandName
                : $"{commandName} {arguments}",
            WorkingDirectory = targetDirectory,
            ExitCode = 0,
            TimeoutSeconds = timeoutSeconds,
            StandardOutput = boundedOutput,
            OutputWasTruncated = truncated,
            ExecutionAttempted = false,
            ExecutionSourceSummary = BuildExecutionSourceSummary(gateDecision),
            GateDecisionId = gateDecision?.DecisionId ?? "",
            GateDecisionSummary = gateDecision?.Summary ?? ""
        };
    }

    private CommandExecutionResult ExecuteExternalProcess(
        string workspaceRoot,
        string fileName,
        string arguments,
        string workingDirectory,
        int requestedTimeoutSeconds,
        ExecutionSafetyPolicy policy,
        ExecutionGateDecision gateDecision)
    {
        RecordExecutionTrace(
            "execution_attempted",
            workspaceRoot,
            gateDecision,
            fileName,
            $"Launching {fileName} from {workingDirectory}.");

        var effectiveTimeoutSeconds = Math.Clamp(requestedTimeoutSeconds, 1, policy.MaxRuntimeSeconds);
        using var process = new Process
        {
            StartInfo = BuildProcessStartInfo(fileName, arguments, workingDirectory)
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var sync = new object();
        var stdoutClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capturedChars = 0;
        var totalChars = 0;
        var totalLines = 0;
        var repeatedLineCount = 0;
        var lastLine = "";
        var windowStart = Stopwatch.GetTimestamp();
        var windowLineCount = 0;
        var outputWasTruncated = false;
        var timedOut = false;
        var killedProcessTree = false;
        var safetyOutcomeType = "";
        var safetyMessage = "";

        process.OutputDataReceived += (_, eventArgs) => HandleLine(eventArgs.Data, stdout, stdoutClosed, false);
        process.ErrorDataReceived += (_, eventArgs) => HandleLine(eventArgs.Data, stderr, stderrClosed, true);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(effectiveTimeoutSeconds * 1000))
        {
            timedOut = true;
            if (TrySetSafetyOutcome("timed_out", $"Execution safety: timed out {policy.CommandFamily} after {effectiveTimeoutSeconds}s; killed process tree."))
                killedProcessTree = KillProcessTree(process, policy);
        }

        WaitForProcessExit(process);
        WaitForStreamCompletion(stdoutClosed.Task, stderrClosed.Task);

        var exitCode = process.HasExited ? process.ExitCode : -1;
        if (timedOut || !string.IsNullOrWhiteSpace(safetyOutcomeType))
            exitCode = -1;

        var boundedStdout = BoundText(stdout.ToString(), policy.MaxOutputBytes, out var stdoutTruncated);
        var boundedStderr = BoundText(stderr.ToString(), policy.MaxOutputBytes, out var stderrTruncated);

        return new CommandExecutionResult
        {
            DisplayCommand = string.IsNullOrWhiteSpace(arguments)
                ? fileName
                : $"{fileName} {arguments}",
            WorkingDirectory = workingDirectory,
            ExitCode = exitCode,
            TimeoutSeconds = effectiveTimeoutSeconds,
            TimedOut = timedOut,
            StandardOutput = boundedStdout,
            StandardError = boundedStderr,
            OutputWasTruncated = outputWasTruncated || stdoutTruncated || stderrTruncated,
            KilledProcessTree = killedProcessTree,
            SafetyOutcomeType = safetyOutcomeType,
            SafetyMessage = safetyMessage,
            SafetyProfileSummary = _executionSafetyPolicyService.Describe(policy),
            ExecutionAttempted = true,
            ExecutionSourceSummary = BuildExecutionSourceSummary(gateDecision),
            GateDecisionId = gateDecision?.DecisionId ?? "",
            GateDecisionSummary = gateDecision?.Summary ?? ""
        };

        void HandleLine(string? line, StringBuilder builder, TaskCompletionSource<bool> completion, bool isError)
        {
            if (line is null)
            {
                completion.TrySetResult(true);
                return;
            }

            var shouldKill = false;
            lock (sync)
            {
                totalLines++;
                totalChars += line.Length + Environment.NewLine.Length;
                UpdateRunawayWindow();
                UpdateRepeatedLineState(line);
                AppendCapturedLine(builder, line, ref capturedChars, policy.MaxOutputBytes, ref outputWasTruncated);

                if (totalLines > policy.MaxOutputLines || totalChars > policy.MaxOutputBytes)
                {
                    shouldKill = TrySetSafetyOutcome(
                        "output_limit_exceeded",
                        $"Execution aborted for safety: output limit exceeded for {policy.CommandFamily}.");
                }
                else if (!string.IsNullOrWhiteSpace(line)
                    && repeatedLineCount >= policy.MaxRepeatedLineCount)
                {
                    shouldKill = TrySetSafetyOutcome(
                        "output_limit_exceeded",
                        $"Execution aborted for safety: repeated output flood detected for {policy.CommandFamily}.");
                }
                else if (windowLineCount >= policy.MaxLinesPerSecond)
                {
                    shouldKill = TrySetSafetyOutcome(
                        "output_limit_exceeded",
                        $"Execution aborted for safety: rapid output growth detected for {policy.CommandFamily}.");
                }
            }

            if (shouldKill)
                killedProcessTree = KillProcessTree(process, policy) || killedProcessTree;

            void UpdateRunawayWindow()
            {
                if (Stopwatch.GetElapsedTime(windowStart).TotalSeconds >= 1)
                {
                    windowStart = Stopwatch.GetTimestamp();
                    windowLineCount = 1;
                    return;
                }

                windowLineCount++;
            }

            void UpdateRepeatedLineState(string currentLine)
            {
                var normalizedLine = currentLine.Trim();
                if (string.IsNullOrWhiteSpace(normalizedLine))
                {
                    repeatedLineCount = 0;
                    lastLine = "";
                    return;
                }

                if (string.Equals(lastLine, normalizedLine, StringComparison.Ordinal))
                {
                    repeatedLineCount++;
                    return;
                }

                lastLine = normalizedLine;
                repeatedLineCount = 1;
            }
        }

        bool TrySetSafetyOutcome(string outcomeType, string message)
        {
            if (!string.IsNullOrWhiteSpace(safetyOutcomeType))
                return false;

            safetyOutcomeType = outcomeType;
            safetyMessage = message;
            return true;
        }
    }

    private static ProcessStartInfo BuildProcessStartInfo(string fileName, string arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var token in TokenizeArguments(arguments))
            info.ArgumentList.Add(token);

        return info;
    }

    private static string ResolveWorkingDirectory(string workspaceRoot, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return Path.GetFullPath(workspaceRoot);

        var candidate = Path.IsPathRooted(workingDirectory)
            ? Path.GetFullPath(workingDirectory)
            : Path.GetFullPath(Path.Combine(workspaceRoot, workingDirectory));

        EnsureInsideWorkspace(workspaceRoot, candidate);

        if (!Directory.Exists(candidate))
            throw new DirectoryNotFoundException($"Working directory not found: {candidate}");

        return candidate;
    }

    private static string ResolveDirectoryListingTarget(string workspaceRoot, string workingDirectory, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return workingDirectory;

        var tokens = TokenizeArguments(arguments);
        if (tokens.Count != 1)
            throw new InvalidOperationException("dir/ls accepts at most one target path argument.");

        var target = Path.IsPathRooted(tokens[0])
            ? Path.GetFullPath(tokens[0])
            : Path.GetFullPath(Path.Combine(workingDirectory, tokens[0]));

        EnsureInsideWorkspace(workspaceRoot, target);

        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException($"Directory not found: {target}");

        return target;
    }

    private static string ValidateGitArguments(string arguments)
    {
        var tokens = TokenizeArguments(arguments);
        if (tokens.Count == 0)
            throw new InvalidOperationException("git command requires arguments.");

        var first = tokens[0];
        var allowed = first is "status" or "diff" or "branch" or "log" or "rev-parse";
        if (!allowed)
            throw new InvalidOperationException("run_command only allows git status, diff, branch, log, and rev-parse.");

        return arguments;
    }

    private static string ValidateDotnetArguments(string arguments)
    {
        var tokens = TokenizeArguments(arguments);
        if (tokens.Count == 0)
            throw new InvalidOperationException("dotnet command requires arguments.");

        var first = tokens[0];
        var allowed = first is "build" or "test" or "restore" or "clean" or "--info" or "--version";
        if (!allowed)
            throw new InvalidOperationException("run_command only allows dotnet build, test, restore, clean, --info, and --version.");

        return arguments;
    }

    private static void EnsureInsideWorkspace(string workspaceRoot, string path)
    {
        var fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        var fullPath = Path.GetFullPath(path);
        var workspacePrefix = fullWorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var isWorkspaceRoot = string.Equals(fullPath, fullWorkspaceRoot, StringComparison.OrdinalIgnoreCase);
        var isInsideWorkspace = fullPath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase);

        if (!isWorkspaceRoot && !isInsideWorkspace)
            throw new InvalidOperationException("Command working directory must stay inside the active workspace.");
    }

    private static bool KillProcessTree(Process process, ExecutionSafetyPolicy policy)
    {
        try
        {
            process.Kill(policy.KillProcessTreeOnTimeout);
        }
        catch
        {
            // Fall back to taskkill below.
        }

        if (process.WaitForExit(3000))
            return true;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            using var taskKill = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            taskKill.StartInfo.ArgumentList.Add("/PID");
            taskKill.StartInfo.ArgumentList.Add(process.Id.ToString());
            taskKill.StartInfo.ArgumentList.Add("/T");
            taskKill.StartInfo.ArgumentList.Add("/F");
            taskKill.Start();
            taskKill.WaitForExit(5000);
        }
        catch
        {
            // Ignore fallback failures.
        }

        return process.WaitForExit(3000);
    }

    private static void WaitForProcessExit(Process process)
    {
        try
        {
            process.WaitForExit(5000);
        }
        catch
        {
            // Ignore wait failures after termination attempts.
        }
    }

    private static void WaitForStreamCompletion(Task stdoutTask, Task stderrTask)
    {
        try
        {
            Task.WaitAll([stdoutTask, stderrTask], 5000);
        }
        catch
        {
            // Ignore stream completion races after process shutdown.
        }
    }

    private static void AppendCapturedLine(
        StringBuilder builder,
        string line,
        ref int capturedChars,
        int maxCapturedChars,
        ref bool truncated)
    {
        if (capturedChars >= maxCapturedChars)
        {
            truncated = true;
            return;
        }

        var remaining = maxCapturedChars - capturedChars;
        var text = line + Environment.NewLine;

        if (text.Length > remaining)
        {
            builder.Append(text[..remaining]);
            capturedChars += remaining;
            truncated = true;
            return;
        }

        builder.Append(text);
        capturedChars += text.Length;
    }

    private static bool IsScriptCommand(string command)
    {
        return string.Equals(command, "bash", StringComparison.OrdinalIgnoreCase)
            || string.Equals(command, "cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(command, "powershell", StringComparison.OrdinalIgnoreCase);
    }

    private CommandExecutionResult CreateSafetyBlockedResult(
        string workspaceRoot,
        string fileName,
        string arguments,
        string workingDirectory,
        ExecutionSafetyPolicy policy,
        string message,
        ExecutionGateDecision gateDecision)
    {
        RecordExecutionTrace("execution_blocked", workspaceRoot, gateDecision, fileName, message);
        return new CommandExecutionResult
        {
            DisplayCommand = string.IsNullOrWhiteSpace(arguments)
                ? fileName
                : $"{fileName} {arguments}",
            WorkingDirectory = workingDirectory,
            ExitCode = -1,
            TimeoutSeconds = policy.MaxRuntimeSeconds,
            TimedOut = false,
            StandardOutput = "",
            StandardError = "",
            OutputWasTruncated = false,
            KilledProcessTree = false,
            SafetyOutcomeType = "safety_blocked",
            SafetyMessage = message,
            SafetyProfileSummary = _executionSafetyPolicyService.Describe(policy),
            ExecutionAttempted = false,
            ExecutionSourceSummary = BuildExecutionSourceSummary(gateDecision),
            GateDecisionId = gateDecision?.DecisionId ?? "",
            GateDecisionSummary = gateDecision?.Summary ?? ""
        };
    }

    private CommandExecutionResult CreateExecutionGateBlockedResult(
        string workspaceRoot,
        string fileName,
        string arguments,
        string workingDirectory,
        ExecutionSafetyPolicy policy,
        ExecutionGateDecision gateDecision)
    {
        RecordExecutionTrace(
            "execution_blocked",
            workspaceRoot,
            gateDecision,
            fileName,
            gateDecision?.BlockedReason ?? "Execution gate blocked this command before launch.");
        return new CommandExecutionResult
        {
            DisplayCommand = string.IsNullOrWhiteSpace(arguments)
                ? fileName
                : $"{fileName} {arguments}",
            WorkingDirectory = workingDirectory,
            ExitCode = -1,
            TimeoutSeconds = policy.MaxRuntimeSeconds,
            TimedOut = false,
            StandardOutput = "",
            StandardError = "",
            OutputWasTruncated = false,
            KilledProcessTree = false,
            SafetyOutcomeType = "execution_gate_blocked",
            SafetyMessage = gateDecision?.BlockedReason ?? "Execution gate blocked this command before launch.",
            SafetyProfileSummary = _executionSafetyPolicyService.Describe(policy),
            ExecutionAttempted = false,
            ExecutionSourceSummary = BuildExecutionSourceSummary(gateDecision),
            GateDecisionId = gateDecision?.DecisionId ?? "",
            GateDecisionSummary = gateDecision?.Summary ?? ""
        };
    }

    private static bool IsGateApproved(ExecutionGateDecision gateDecision)
    {
        return gateDecision is not null && gateDecision.IsAllowed;
    }

    private static string BuildExecutionSourceSummary(ExecutionGateDecision? gateDecision)
    {
        if (gateDecision is null)
            return "unknown";

        var source = gateDecision.SourceType switch
        {
            ExecutionSourceType.ManualUserRequest => "manual_user_request",
            ExecutionSourceType.AutoValidation => "auto_validation",
            ExecutionSourceType.Verification => "verification",
            ExecutionSourceType.BuildTool => "build_tool",
            _ => "unknown"
        };

        if (string.IsNullOrWhiteSpace(gateDecision.SourceName))
            return source;

        return $"{source}:{gateDecision.SourceName}";
    }

    private static void RecordExecutionTrace(
        string eventKind,
        string workspaceRoot,
        ExecutionGateDecision? gateDecision,
        string toolName,
        string message)
    {
        ExecutionTraceService.Record(new ExecutionTraceEventRecord
        {
            EventKind = eventKind,
            WorkspaceRoot = workspaceRoot,
            SourceType = gateDecision?.SourceType switch
            {
                ExecutionSourceType.ManualUserRequest => "manual_user_request",
                ExecutionSourceType.AutoValidation => "auto_validation",
                ExecutionSourceType.Verification => "verification",
                ExecutionSourceType.BuildTool => "build_tool",
                _ => "unknown"
            },
            SourceName = gateDecision?.SourceName ?? "",
            ToolName = toolName,
            CommandFamily = gateDecision?.CommandFamily ?? "",
            BuildFamily = gateDecision?.BuildFamily ?? "",
            GateDecisionId = gateDecision?.DecisionId ?? "",
            GateDecisionSummary = gateDecision?.Summary ?? "",
            Message = message
        });
    }

    private static string NormalizeCommand(string command)
    {
        return (command ?? "").Trim().ToLowerInvariant();
    }

    private static string BoundText(string text, int maxChars, out bool truncated)
    {
        text ??= "";
        if (text.Length <= maxChars)
        {
            truncated = false;
            return text;
        }

        truncated = true;
        return text[..maxChars];
    }

    private static List<string> TokenizeArguments(string arguments)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments))
            return tokens;

        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in arguments)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }
}

public sealed class CommandExecutionResult
{
    public string DisplayCommand { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public int ExitCode { get; set; }
    public int TimeoutSeconds { get; set; }
    public bool TimedOut { get; set; }
    public string StandardOutput { get; set; } = "";
    public string StandardError { get; set; } = "";
    public bool OutputWasTruncated { get; set; }
    public bool KilledProcessTree { get; set; }
    public string SafetyOutcomeType { get; set; } = "";
    public string SafetyMessage { get; set; } = "";
    public string SafetyProfileSummary { get; set; } = "";
    public bool ExecutionAttempted { get; set; }
    public string ExecutionSourceSummary { get; set; } = "";
    public string GateDecisionId { get; set; } = "";
    public string GateDecisionSummary { get; set; } = "";
}
