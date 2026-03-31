using System.Collections.ObjectModel;
using System.Windows;
using RAM.Models;
using RAM.Services;
using RAM.Tools;

namespace RAM;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ChatMessage> _messages = [];
    private readonly OllamaClient _ollamaClient = new();
    private readonly AgentCallPolicyService _agentCallPolicyService = new();
    private readonly BuilderRequestClassifier _builderRequestClassifier = new();
    private readonly ExecutionFeedbackService _executionFeedbackService = new();
    private readonly IntentDraftService _intentDraftService = new();
    private readonly ModelOutputValidationService _modelOutputValidationService = new();
    private readonly ResponseModeSelectionService _responseModeSelectionService = new();
    private readonly IAgentRuntimeClient _agentRuntimeClient;
    private readonly IAgentTraceWriter _agentTraceWriter;
    private readonly SummaryAgentService _summaryAgentService;
    private readonly SuggestionAgentService _suggestionAgentService;
    private readonly BuildProfileAgentService _buildProfileAgentService;
    private readonly PhraseFamilyAgentService _phraseFamilyAgentService;
    private readonly TemplateSelectorAgentService _templateSelectorAgentService;
    private readonly ForensicsAgentService _forensicsAgentService;
    private readonly UserInputResolutionService _userInputResolutionService = new();
    private readonly ToolArgumentRecoveryService _toolArgumentRecoveryService = new();
    private readonly ToolChainControllerService _toolChainControllerService = new();
    private readonly ToolChainSummaryService _toolChainSummaryService = new();
    private readonly ToolExecutionService _toolExecutionService;
    private readonly ToolRegistryService _toolRegistryService = new();
    private readonly ToolRequestParser _toolRequestParser = new();
    private readonly SaveOutputTool _saveOutputTool = new();
    private readonly ToolService _toolService = new();
    private readonly WorkspaceService _workspaceService = new();
    private readonly WorkspacePreparationService _workspacePreparationService = new();
    private readonly RamDbService _ramDbService = new();
    private readonly ModelRoleConfigurationService _modelRoleConfigurationService = new();

    private IntentRecord _currentIntent = new();
    private ArtifactRecord? _activeArtifact;
    private string _activeTargetPath = "";

    public MainWindow()
    {
        _agentRuntimeClient = new OllamaAgentRuntimeClient(_ollamaClient);
        _agentTraceWriter = new RamAgentTraceWriter(_ramDbService);
        _summaryAgentService = new SummaryAgentService(_agentRuntimeClient, _agentTraceWriter, _agentCallPolicyService);
        _suggestionAgentService = new SuggestionAgentService(_agentRuntimeClient, _agentTraceWriter, _agentCallPolicyService);
        _buildProfileAgentService = new BuildProfileAgentService(_agentRuntimeClient, _agentTraceWriter, _agentCallPolicyService);
        _phraseFamilyAgentService = new PhraseFamilyAgentService(_agentRuntimeClient, _agentTraceWriter, _agentCallPolicyService);
        _templateSelectorAgentService = new TemplateSelectorAgentService(_agentRuntimeClient, _agentTraceWriter, _agentCallPolicyService);
        _forensicsAgentService = new ForensicsAgentService(_agentRuntimeClient, _agentTraceWriter, _agentCallPolicyService);
        _taskboardOperatorSummaryService = new TaskboardOperatorSummaryService(
            (endpoint, model, prompt, cancellationToken) => _ollamaClient.GenerateAsync(endpoint, model, prompt, cancellationToken));
        _taskboardAutoRunService = new TaskboardAutoRunService(
            new BuilderWorkItemDecompositionService(
                new BuildProfileResolutionService(_buildProfileAgentService),
                _phraseFamilyAgentService,
                _templateSelectorAgentService),
            forensicsAgentService: _forensicsAgentService);
        _toolExecutionService = new ToolExecutionService(
            _toolRegistryService,
            _toolService,
            _saveOutputTool,
            _workspaceService,
            _ramDbService,
            _settingsService,
            _ollamaClient);

        InitializeComponent();
        InitializeTaskboardUi();

        LoadSettings();
        ApplySavedModelSettingsToUi();

        ChatListBox.ItemsSource = _messages;

        AddMessage("system", "RAM is ready.");
        AppendOutput("RAM started.");

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        ApplySavedWorkspaceToUi();
        await LoadModelsAsync();
    }
}
