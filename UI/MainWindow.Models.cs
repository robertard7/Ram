using System.Windows.Controls;
using RAM.Models;

namespace RAM;

public partial class MainWindow
{
    private async Task LoadModelsAsync()
    {
        try
        {
            var models = await _ollamaClient.GetModelsAsync(EndpointTextBox.Text.Trim());
            ApplyAvailableModelsToUi(models);
        }
        catch (Exception ex)
        {
            ApplyAvailableModelsToUi([]);
            AppendOutput("Model list load failed:" + Environment.NewLine + ex.Message);
        }
    }

    private void ApplyAvailableModelsToUi(IReadOnlyList<OllamaModelInfo> models)
    {
        _modelRoleConfigurationService.Normalize(_settings);
        var state = _modelRoleConfigurationService.BuildState(_settings, models);

        IntakeModelComboBox.ItemsSource = state.AvailableModels;
        CoderModelComboBox.ItemsSource = state.AvailableModels;
        EmbedderModelComboBox.ItemsSource = state.AvailableModels;
        EmbedderBackendComboBox.ItemsSource = state.AvailableEmbedderBackends;

        ApplyModelSelection(IntakeModelComboBox, state.AvailableModels, state.IntakeModel);
        ApplyModelSelection(CoderModelComboBox, state.AvailableModels, state.CoderModel);
        ApplyModelSelection(EmbedderModelComboBox, state.AvailableModels, state.EmbedderModel);
        ApplyBackendSelection(state.EmbedderBackend);

        QdrantEndpointTextBox.Text = state.QdrantEndpoint;
        QdrantCollectionTextBox.Text = state.QdrantCollection;
    }

    private static void ApplyModelSelection(ComboBox comboBox, IReadOnlyList<OllamaModelInfo> models, string selectedModel)
    {
        if (!string.IsNullOrWhiteSpace(selectedModel))
        {
            var match = models.FirstOrDefault(current =>
                string.Equals(current.Name, selectedModel, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                comboBox.SelectedItem = match;
                comboBox.Text = match.Name;
                return;
            }

            comboBox.Text = selectedModel;
            return;
        }

        if (models.Count > 0)
        {
            comboBox.SelectedIndex = 0;
            comboBox.Text = models[0].Name;
        }
    }

    private void ApplyBackendSelection(string backend)
    {
        var backends = _modelRoleConfigurationService.GetEmbedderBackends();
        var match = backends.FirstOrDefault(current =>
            string.Equals(current, backend, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(match))
        {
            EmbedderBackendComboBox.SelectedItem = match;
            EmbedderBackendComboBox.Text = match;
            return;
        }

        EmbedderBackendComboBox.Text = backend;
    }

    private string GetSelectedModel()
    {
        return GetSelectedCoderModel();
    }

    private string GetSelectedCoderModel()
    {
        return GetSelectedModelName(CoderModelComboBox);
    }

    private string GetSelectedIntakeModel()
    {
        return GetSelectedModelName(IntakeModelComboBox);
    }

    private string GetSelectedEmbedderModel()
    {
        return GetSelectedModelName(EmbedderModelComboBox);
    }

    private string GetSelectedEmbedderBackend()
    {
        if (EmbedderBackendComboBox.SelectedItem is string selected
            && !string.IsNullOrWhiteSpace(selected))
        {
            return selected;
        }

        return EmbedderBackendComboBox.Text.Trim();
    }

    private static string GetSelectedModelName(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is OllamaModelInfo selected &&
            !string.IsNullOrWhiteSpace(selected.Name))
        {
            return selected.Name;
        }

        return comboBox.Text.Trim();
    }
}
