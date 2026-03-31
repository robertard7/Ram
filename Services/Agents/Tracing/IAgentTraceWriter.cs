namespace RAM.Services;

public interface IAgentTraceWriter
{
    void WriteTrace(string workspaceRoot, AgentTraceRecord trace);
    IReadOnlyList<AgentTraceRecord> Snapshot(string? workspaceRoot = null);
}
