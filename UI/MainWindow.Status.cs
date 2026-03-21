namespace RAM;

public partial class MainWindow
{
    private void AppendOutput(string text)
    {
        if (!string.IsNullOrWhiteSpace(OutputTextBox.Text))
        {
            OutputTextBox.AppendText(Environment.NewLine + Environment.NewLine);
        }

        OutputTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}");
        OutputTextBox.ScrollToEnd();
    }

    private void SetBusy(bool isBusy)
    {
        SendButton.IsEnabled = !isBusy;
        TestConnectionButton.IsEnabled = !isBusy;
    }

    private async void TestConnectionButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);

            var ok = await _ollamaClient.TestConnectionAsync(EndpointTextBox.Text.Trim());
            StatusTextBlock.Text = ok ? "Connected" : "Not Connected";

            AppendOutput(ok
                ? $"Connection OK: {EndpointTextBox.Text.Trim()}"
                : $"Connection failed: {EndpointTextBox.Text.Trim()}");

            if (ok)
            {
                await LoadModelsAsync();
            }

            SaveSettings();
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