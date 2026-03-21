using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private async Task LoadModelsAsync()
    {
        try
        {
            var models = await _ollamaClient.GetModelsAsync(EndpointTextBox.Text.Trim());

            ModelComboBox.ItemsSource = models;

            if (!string.IsNullOrWhiteSpace(_settings.Model))
            {
                var match = models.FirstOrDefault(x =>
                    string.Equals(x.Name, _settings.Model, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    ModelComboBox.SelectedItem = match;
                    ModelComboBox.Text = match.Name;
                }
                else
                {
                    ModelComboBox.Text = _settings.Model;
                }
            }
            else if (models.Count > 0)
            {
                ModelComboBox.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            AppendOutput("Model list load failed:" + Environment.NewLine + ex.Message);
        }
    }

    private string GetSelectedModel()
    {
        if (ModelComboBox.SelectedItem is OllamaModelInfo selected &&
            !string.IsNullOrWhiteSpace(selected.Name))
        {
            return selected.Name;
        }

        return ModelComboBox.Text.Trim();
    }
}