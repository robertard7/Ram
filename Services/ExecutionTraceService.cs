using RAM.Models;

namespace RAM.Services;

public static class ExecutionTraceService
{
    private static readonly object Sync = new();
    private static readonly List<ExecutionTraceEventRecord> Events = [];

    public static void Reset()
    {
        lock (Sync)
        {
            Events.Clear();
        }
    }

    public static void Record(ExecutionTraceEventRecord record)
    {
        if (record is null)
            return;

        lock (Sync)
        {
            Events.Add(record);
        }
    }

    public static IReadOnlyList<ExecutionTraceEventRecord> Snapshot()
    {
        lock (Sync)
        {
            return Events
                .OrderBy(item => item.CreatedUtc, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
