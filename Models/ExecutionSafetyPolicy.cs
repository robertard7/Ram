namespace RAM.Models;

public sealed class ExecutionSafetyPolicy
{
    public string CommandFamily { get; set; } = "generic";
    public int MaxRuntimeSeconds { get; set; }
    public int MaxOutputBytes { get; set; }
    public int MaxOutputLines { get; set; }
    public int MaxParallelJobs { get; set; }
    public bool AllowScriptExecution { get; set; }
    public bool AllowRecursiveBuildTools { get; set; }
    public bool KillProcessTreeOnTimeout { get; set; }
    public int MaxRepeatedLineCount { get; set; }
    public int MaxLinesPerSecond { get; set; }
}
