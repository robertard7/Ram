using RAM.Services;

namespace RAM;

public partial class MainWindow
{
    private void AppendOutput(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendOutput(text));
            return;
        }

        if (!string.IsNullOrWhiteSpace(OutputTextBox.Text))
        {
            OutputTextBox.AppendText(Environment.NewLine + Environment.NewLine);
        }

        OutputTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}");
        OutputTextBox.ScrollToEnd();
    }

    private void SetBusy(bool isBusy)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetBusy(isBusy));
            return;
        }

        SendButton.IsEnabled = !isBusy;
        TestConnectionButton.IsEnabled = !isBusy;
    }

    private async void TestConnectionButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
            SaveSettings();

            var ok = await _ollamaClient.TestConnectionAsync(EndpointTextBox.Text.Trim());
            var retrievalService = new RamRetrievalService(_ramDbService, _settingsService, _ollamaClient);
            var retrievalStatus = await retrievalService.TestBackendAsync(_settings);
            var qdrantStatusText = !string.Equals(_settings.EmbedderBackend, "qdrant", StringComparison.OrdinalIgnoreCase)
                ? "Disabled"
                : string.IsNullOrWhiteSpace(_settings.QdrantEndpoint) || string.IsNullOrWhiteSpace(_settings.QdrantCollection)
                    ? "Not Configured"
                    : retrievalStatus.ConnectionOk
                        ? retrievalStatus.CollectionReady ? "Ready" : "Connected"
                        : "Not Connected";
            StatusTextBlock.Text = $"Coder: {(ok ? "Connected" : "Not Connected")} | Qdrant: {qdrantStatusText}";

            AppendOutput(ok
                ? $"Connection OK: {EndpointTextBox.Text.Trim()}"
                : $"Connection failed: {EndpointTextBox.Text.Trim()}");
            AppendOutput($"Qdrant status: {retrievalStatus.StatusSummary}");

            if (ok)
            {
                await LoadModelsAsync();
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Error";
            AppendOutput("Connection test error:" + Environment.NewLine + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }
}
