using RAM.Models;
using RAM.Services;

namespace RAM;

public partial class MainWindow
{
    private readonly SettingsService _settingsService = new();
    private AppSettings _settings = new();

    private void LoadSettings()
    {
        _settings = _settingsService.Load();

        EndpointTextBox.Text = _settings.Endpoint;
    }

    private void ApplySavedModelToUi()
    {
        ModelComboBox.Text = _settings.Model;
    }

    private void ApplySavedWorkspaceToUi()
    {
        if (string.IsNullOrWhiteSpace(_settings.WorkspaceRoot))
        {
            WorkspaceTextBlock.Text = "Workspace: (not set)";
            LoadIntentFromWorkspace();
            return;
        }

        try
        {
            _workspaceService.SetWorkspace(_settings.WorkspaceRoot);
            WorkspaceTextBlock.Text = $"Workspace: {_workspaceService.WorkspaceRoot}";
            AddMessage("system", $"Workspace restored: {_workspaceService.WorkspaceRoot}");
            AppendOutput($"Workspace restored: {_workspaceService.WorkspaceRoot}");
        }
        catch
        {
            WorkspaceTextBlock.Text = "Workspace: (not set)";
            AppendOutput("Saved workspace could not be restored.");
        }

        LoadIntentFromWorkspace();
    }

    private void SaveSettings()
    {
        _settings.Endpoint = EndpointTextBox.Text.Trim();
        _settings.Model = GetSelectedModel();
        _settings.WorkspaceRoot = _workspaceService.HasWorkspace()
            ? _workspaceService.WorkspaceRoot
            : "";

        _settingsService.Save(_settings);
    }
}