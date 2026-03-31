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
        _modelRoleConfigurationService.Normalize(_settings);

        EndpointTextBox.Text = _settings.Endpoint;
    }

    private void ApplySavedModelSettingsToUi()
    {
        IntakeModelComboBox.Text = _settings.IntakeModel;
        CoderModelComboBox.Text = _settings.CoderModel;
        EmbedderModelComboBox.Text = _settings.EmbedderModel;
        EmbedderBackendComboBox.ItemsSource = _modelRoleConfigurationService.GetEmbedderBackends();
        EmbedderBackendComboBox.Text = _settings.EmbedderBackend;
        QdrantEndpointTextBox.Text = _settings.QdrantEndpoint;
        QdrantCollectionTextBox.Text = _settings.QdrantCollection;
    }

    private void ApplySavedWorkspaceToUi()
    {
        if (string.IsNullOrWhiteSpace(_settings.WorkspaceRoot))
        {
            WorkspaceTextBlock.Text = "Workspace: (not set)";
            LoadIntentFromWorkspace();
            LoadActiveArtifactFromWorkspace();
            RefreshTaskboardUi();
            return;
        }

        try
        {
            _workspaceService.SetWorkspace(_settings.WorkspaceRoot);
            WorkspaceTextBlock.Text = $"Workspace: {_workspaceService.WorkspaceRoot}";
            AddMessage("system", $"Workspace restored: {_workspaceService.WorkspaceRoot}");
            AppendOutput($"Workspace restored: {_workspaceService.WorkspaceRoot}");
            PrepareActiveWorkspaceSurface("restore workspace");
        }
        catch
        {
            WorkspaceTextBlock.Text = "Workspace: (not set)";
            AppendOutput("Saved workspace could not be restored.");
        }

        LoadIntentFromWorkspace();
        LoadActiveArtifactFromWorkspace();
        RefreshTaskboardUi();
    }

    private void SaveSettings()
    {
        ResolveCurrentAppSettingsSnapshot(persist: true);
    }

    private AppSettings ResolveCurrentAppSettingsSnapshot(bool persist)
    {
        var snapshot = CloneSettings(_settings);
        snapshot.Endpoint = EndpointTextBox.Text.Trim();
        snapshot.IntakeModel = GetSelectedIntakeModel();
        snapshot.CoderModel = GetSelectedCoderModel();
        snapshot.EmbedderModel = GetSelectedEmbedderModel();
        snapshot.Model = snapshot.CoderModel;
        snapshot.EmbedderBackend = GetSelectedEmbedderBackend();
        snapshot.QdrantEndpoint = QdrantEndpointTextBox.Text.Trim();
        snapshot.QdrantCollection = QdrantCollectionTextBox.Text.Trim();
        snapshot.WorkspaceRoot = _workspaceService.HasWorkspace()
            ? _workspaceService.WorkspaceRoot
            : snapshot.WorkspaceRoot;

        _modelRoleConfigurationService.Normalize(snapshot);

        if (persist)
        {
            _settings = snapshot;
            _settingsService.Save(_settings);
        }

        return snapshot;
    }

    private static AppSettings CloneSettings(AppSettings source)
    {
        return new AppSettings
        {
            Endpoint = source.Endpoint,
            Model = source.Model,
            IntakeModel = source.IntakeModel,
            CoderModel = source.CoderModel,
            EmbedderModel = source.EmbedderModel,
            EmbedderBackend = source.EmbedderBackend,
            QdrantEndpoint = source.QdrantEndpoint,
            QdrantCollection = source.QdrantCollection,
            WorkspaceRoot = source.WorkspaceRoot,
            EnableAdvisoryAgents = source.EnableAdvisoryAgents,
            EnableSummaryAgent = source.EnableSummaryAgent,
            EnableSuggestionAgent = source.EnableSuggestionAgent,
            EnableBuildProfileAgent = source.EnableBuildProfileAgent,
            EnablePhraseFamilyAgent = source.EnablePhraseFamilyAgent,
            EnableTemplateSelectorAgent = source.EnableTemplateSelectorAgent,
            EnableForensicsAgent = source.EnableForensicsAgent,
            SummaryAgentModel = source.SummaryAgentModel,
            SuggestionAgentModel = source.SuggestionAgentModel,
            BuildProfileAgentModel = source.BuildProfileAgentModel,
            PhraseFamilyAgentModel = source.PhraseFamilyAgentModel,
            TemplateSelectorAgentModel = source.TemplateSelectorAgentModel,
            ForensicsAgentModel = source.ForensicsAgentModel,
            AgentTimeoutSeconds = source.AgentTimeoutSeconds,
            AutoActivateValidatedTaskboardWhenNoActivePlan = source.AutoActivateValidatedTaskboardWhenNoActivePlan,
            ConfirmBeforeReplacingActivePlan = source.ConfirmBeforeReplacingActivePlan,
            ShowArchivedTaskboards = source.ShowArchivedTaskboards,
            TaskboardActionMessageDedupeWindowSeconds = source.TaskboardActionMessageDedupeWindowSeconds
        };
    }
}
