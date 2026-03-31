using RAM.Models;

namespace RAM.Services;

public sealed class ExecutionTraceAssertionService
{
    public bool HasExecutionAttempt(string workspaceRoot, string sourceName)
    {
        return Snapshot(workspaceRoot, sourceName)
            .Any(item => string.Equals(item.EventKind, "execution_attempted", StringComparison.OrdinalIgnoreCase));
    }

    public string BuildSummary(string workspaceRoot, string sourceName)
    {
        var events = Snapshot(workspaceRoot, sourceName);
        if (events.Count == 0)
            return "No execution trace events were recorded for the selected workspace/source.";

        var lines = new List<string>();
        foreach (var item in events)
        {
            lines.Add(
                $"{item.EventKind}: source={DisplayValue(item.SourceType)}:{DisplayValue(item.SourceName)} "
                + $"tool={DisplayValue(item.ToolName)} build_family={DisplayValue(item.BuildFamily)} "
                + $"message={DisplayValue(item.Message)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static List<ExecutionTraceEventRecord> Snapshot(string workspaceRoot, string sourceName)
    {
        return ExecutionTraceService.Snapshot()
            .Where(item =>
                string.Equals(item.WorkspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(sourceName)
                    || string.Equals(item.SourceName, sourceName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
