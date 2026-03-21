namespace RAM.Services;

public sealed class QdrantMemoryService
{
    public bool IsEnabled => false;

    public Task IndexSummaryAsync(string workspaceRoot, string sourceType, string sourceId, string summaryText)
    {
        return Task.CompletedTask;
    }
}