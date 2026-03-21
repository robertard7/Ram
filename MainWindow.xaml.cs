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
    private readonly ToolService _toolService = new();
    private readonly WorkspaceService _workspaceService = new();
    private readonly RamDbService _ramDbService = new();

    private IntentRecord _currentIntent = new();

    public MainWindow()
    {
        InitializeComponent();

        LoadSettings();
        ApplySavedModelToUi();

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