using RAM.Models;

namespace RAM.Services;

public sealed class ExecutionSafetyPolicyService
{
    public ExecutionSafetyPolicy GetPolicy(string commandFamily)
    {
        var normalizedFamily = NormalizeCommandFamily(commandFamily);

        return normalizedFamily switch
        {
            "create_dotnet_solution" => CreatePolicy(normalizedFamily, 60, 12000, 400, 1, allowScriptExecution: false, allowRecursiveBuildTools: false),
            "create_dotnet_project" => CreatePolicy(normalizedFamily, 90, 16000, 500, 1, allowScriptExecution: false, allowRecursiveBuildTools: false),
            "add_project_to_solution" => CreatePolicy(normalizedFamily, 60, 12000, 400, 1, allowScriptExecution: false, allowRecursiveBuildTools: false),
            "add_dotnet_project_reference" => CreatePolicy(normalizedFamily, 60, 12000, 400, 1, allowScriptExecution: false, allowRecursiveBuildTools: false),
            "cmake_configure" => CreatePolicy(normalizedFamily, 20, 12000, 320, 1, allowScriptExecution: false, allowRecursiveBuildTools: false),
            "cmake_build" => CreatePolicy(normalizedFamily, 15, 12000, 300, 2, allowScriptExecution: false, allowRecursiveBuildTools: true),
            "ctest_run" => CreatePolicy(normalizedFamily, 20, 12000, 320, 1, allowScriptExecution: false, allowRecursiveBuildTools: false),
            "make_build" => CreatePolicy(normalizedFamily, 15, 12000, 300, 2, allowScriptExecution: false, allowRecursiveBuildTools: true),
            "ninja_build" => CreatePolicy(normalizedFamily, 15, 12000, 300, 2, allowScriptExecution: false, allowRecursiveBuildTools: true),
            "run_build_script" => CreatePolicy(normalizedFamily, 10, 6000, 180, 1, allowScriptExecution: true, allowRecursiveBuildTools: false),
            "dotnet" => CreatePolicy(normalizedFamily, 120, 24000, 800, 2, allowScriptExecution: false, allowRecursiveBuildTools: true),
            _ => CreatePolicy("generic", 60, 12000, 400, 1, allowScriptExecution: false, allowRecursiveBuildTools: false)
        };
    }

    public string Describe(ExecutionSafetyPolicy policy)
    {
        if (policy is null)
            return "";

        var parallelism = policy.MaxParallelJobs > 0
            ? policy.MaxParallelJobs.ToString()
            : "n/a";

        return $"{policy.CommandFamily}, timeout={policy.MaxRuntimeSeconds}s, parallelism={parallelism}, output cap={policy.MaxOutputLines} lines/{policy.MaxOutputBytes} chars.";
    }

    public bool IsSafetyOutcome(string outcomeType)
    {
        return string.Equals(outcomeType, "timed_out", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcomeType, "output_limit_exceeded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcomeType, "safety_blocked", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcomeType, "safety_blocked_scope", StringComparison.OrdinalIgnoreCase);
    }

    private static ExecutionSafetyPolicy CreatePolicy(
        string commandFamily,
        int maxRuntimeSeconds,
        int maxOutputBytes,
        int maxOutputLines,
        int maxParallelJobs,
        bool allowScriptExecution,
        bool allowRecursiveBuildTools)
    {
        return new ExecutionSafetyPolicy
        {
            CommandFamily = commandFamily,
            MaxRuntimeSeconds = maxRuntimeSeconds,
            MaxOutputBytes = maxOutputBytes,
            MaxOutputLines = maxOutputLines,
            MaxParallelJobs = maxParallelJobs,
            AllowScriptExecution = allowScriptExecution,
            AllowRecursiveBuildTools = allowRecursiveBuildTools,
            KillProcessTreeOnTimeout = true,
            MaxRepeatedLineCount = 30,
            MaxLinesPerSecond = 120
        };
    }

    private static string NormalizeCommandFamily(string commandFamily)
    {
        return (commandFamily ?? "").Trim().ToLowerInvariant();
    }
}
