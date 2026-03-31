using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class RamAgentTraceWriter : IAgentTraceWriter
{
    private static readonly object TraceLock = new();
    private static readonly List<(string WorkspaceRoot, AgentTraceRecord Trace)> Traces = [];
    private readonly RamDbService _ramDbService;

    public RamAgentTraceWriter(RamDbService ramDbService)
    {
        _ramDbService = ramDbService;
    }

    public void WriteTrace(string workspaceRoot, AgentTraceRecord trace)
    {
        lock (TraceLock)
        {
            Traces.Add((workspaceRoot ?? "", trace));
            while (Traces.Count > 200)
                Traces.RemoveAt(0);
        }

        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return;

        _ramDbService.SaveArtifact(workspaceRoot, new ArtifactRecord
        {
            ArtifactType = "agent_trace",
            Title = $"Agent trace: {trace.AgentRole}",
            RelativePath = $".ram/agent-traces/{trace.AgentRole.ToLowerInvariant()}/{trace.RequestId}-{trace.TraceId}.json",
            Content = JsonSerializer.Serialize(trace, new JsonSerializerOptions { WriteIndented = true }),
            Summary = $"{trace.AgentRole} {trace.ResultCategory} accepted={trace.Accepted} fallback={trace.FallbackUsed}"
        });
    }

    public IReadOnlyList<AgentTraceRecord> Snapshot(string? workspaceRoot = null)
    {
        lock (TraceLock)
        {
            return Traces
                .Where(item => string.IsNullOrWhiteSpace(workspaceRoot)
                    || string.Equals(item.WorkspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Trace)
                .ToList();
        }
    }
}
