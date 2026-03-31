namespace RAM;

public partial class MainWindow
{
    private UiExecutionContextSnapshot CaptureUiExecutionContextSnapshot(string source)
    {
        if (Dispatcher.CheckAccess())
            return BuildUiExecutionContextSnapshot();

        var callingThreadId = Environment.CurrentManagedThreadId;
        var dispatcherThreadId = Dispatcher.Thread.ManagedThreadId;

        try
        {
            return Dispatcher.Invoke(BuildUiExecutionContextSnapshot);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{source} failed to capture UI execution context. calling_thread={callingThreadId} dispatcher_thread={dispatcherThreadId}.",
                ex);
        }
    }

    private UiExecutionContextSnapshot BuildUiExecutionContextSnapshot()
    {
        var settings = ResolveCurrentAppSettingsSnapshot(persist: false);
        return new UiExecutionContextSnapshot
        {
            Endpoint = settings.Endpoint,
            SelectedModel = settings.CoderModel,
            IntakeModel = settings.IntakeModel,
            CoderModel = settings.CoderModel,
            EmbedderModel = settings.EmbedderModel,
            EmbedderBackend = settings.EmbedderBackend,
            QdrantEndpoint = settings.QdrantEndpoint,
            QdrantCollection = settings.QdrantCollection,
            ActiveTargetRelativePath = GetActiveTargetRelativePath(),
            CapturedOnThreadId = Environment.CurrentManagedThreadId,
            CapturedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private sealed class UiExecutionContextSnapshot
    {
        public string Endpoint { get; init; } = "";
        public string SelectedModel { get; init; } = "";
        public string IntakeModel { get; init; } = "";
        public string CoderModel { get; init; } = "";
        public string EmbedderModel { get; init; } = "";
        public string EmbedderBackend { get; init; } = "";
        public string QdrantEndpoint { get; init; } = "";
        public string QdrantCollection { get; init; } = "";
        public string ActiveTargetRelativePath { get; init; } = "";
        public int CapturedOnThreadId { get; init; }
        public string CapturedUtc { get; init; } = "";
    }
}
