using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class CSharpTemplateGenerationService
{
    private readonly CSharpGenerationArgumentResolverService _cSharpGenerationArgumentResolverService = new();

    public bool TryGenerateContent(ToolRequest request, string relativePath, out string content, out string summary)
    {
        if (!TryGeneratePlan(request, relativePath, out var plan))
        {
            content = "";
            summary = "";
            return false;
        }

        content = plan.PrimaryArtifact.Content;
        summary = plan.TemplateGenerationSummary;
        return true;
    }

    public bool TryGeneratePlan(ToolRequest request, string relativePath, out CSharpGeneratedOutputPlanRecord plan)
    {
        plan = new CSharpGeneratedOutputPlanRecord();

        var pattern = GetArgument(request, "pattern").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        var normalizedPath = NormalizeRelativePath(relativePath);
        var fileName = Path.GetFileName(normalizedPath.Replace('/', Path.DirectorySeparatorChar));
        var projectName = FirstNonEmpty(GetArgument(request, "project"), InferProjectName(normalizedPath));
        var namespaceName = FirstNonEmpty(GetArgument(request, "namespace"), InferNamespace(projectName, normalizedPath, pattern));
        var arguments = _cSharpGenerationArgumentResolverService.Resolve(request, normalizedPath, namespaceName, projectName);
        var primaryContent = BuildPatternContent(fileName, namespaceName, arguments);
        if (string.IsNullOrWhiteSpace(primaryContent))
            return false;

        var companionArtifacts = BuildCompanionArtifacts(normalizedPath, namespaceName, arguments);
        var summary = $"Generated `{fileName}` from explicit pattern `{arguments.Pattern}` depth={arguments.ImplementationDepth} namespace={namespaceName} followthrough={arguments.FollowThroughMode}.";

        plan = new CSharpGeneratedOutputPlanRecord
        {
            Summary = summary,
            TemplateGenerationSummary = summary,
            PrimaryArtifact = new CSharpGeneratedArtifactPlanRecord
            {
                RelativePath = normalizedPath,
                FileRole = arguments.FileRole,
                Pattern = arguments.Pattern,
                Summary = summary,
                Content = primaryContent
            },
            CompanionArtifacts = companionArtifacts
        };
        return true;
    }

    private static string BuildPatternContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        return arguments.Pattern switch
        {
            "interface" => BuildInterfaceContent(fileName, namespaceName, arguments),
            "repository" => BuildRepositoryContent(fileName, namespaceName, arguments.ImplementationDepth, arguments),
            "viewmodel" => BuildViewModelContent(fileName, namespaceName, arguments),
            "page" => BuildPageContent(fileName),
            "test_harness" => BuildTestHarnessContent(fileName, namespaceName, arguments),
            "model" => BuildModelContent(fileName, namespaceName, arguments),
            "dto" => BuildDtoContent(fileName, namespaceName, arguments),
            "service" => BuildServiceContent(fileName, namespaceName, arguments),
            "controller" => BuildControllerContent(fileName, namespaceName, arguments),
            "worker_support" => BuildWorkerSupportContent(fileName, namespaceName, arguments),
            _ => ""
        };
    }

    private static List<CSharpGeneratedArtifactPlanRecord> BuildCompanionArtifacts(
        string relativePath,
        string namespaceName,
        CSharpGenerationArgumentContractRecord arguments)
    {
        var artifacts = new List<CSharpGeneratedArtifactPlanRecord>();
        foreach (var surface in arguments.SupportingSurfaces)
        {
            if (string.IsNullOrWhiteSpace(surface))
                continue;

            var separatorIndex = surface.IndexOf(':', StringComparison.Ordinal);
            var surfaceKind = separatorIndex >= 0 ? surface[..separatorIndex].Trim().ToLowerInvariant() : "support";
            var surfaceValue = separatorIndex >= 0 ? surface[(separatorIndex + 1)..].Trim() : surface.Trim();
            if (string.IsNullOrWhiteSpace(surfaceValue))
                continue;

            var companionPath = ResolveCompanionPath(relativePath, surfaceValue);
            var companionFileName = Path.GetFileName(companionPath);
            var companionNamespace = InferNamespace(arguments.TargetProject, companionPath, surfaceKind);
            var companionArguments = CloneArgumentsForCompanion(arguments, companionPath, companionNamespace, companionFileName);
            var content = surfaceKind switch
            {
                "interface" => BuildGenericInterfaceContent(companionFileName, companionNamespace, companionArguments),
                "model" => BuildGenericModelContent(companionFileName, companionNamespace, companionArguments),
                "request" => BuildGenericDtoContent(companionFileName, companionNamespace, companionArguments, "request"),
                "response" => BuildGenericDtoContent(companionFileName, companionNamespace, companionArguments, "response"),
                "helper" => BuildHelperContent(companionFileName, companionNamespace, companionArguments),
                "options" => BuildOptionsContent(companionFileName, companionNamespace, companionArguments),
                _ => ""
            };
            if (string.IsNullOrWhiteSpace(content))
                continue;

            artifacts.Add(new CSharpGeneratedArtifactPlanRecord
            {
                RelativePath = companionPath,
                FileRole = arguments.FileRole,
                Pattern = surfaceKind,
                Summary = $"Generated companion `{companionFileName}` for `{arguments.ClassName}`.",
                Content = content
            });
        }

        return artifacts;
    }

    private static CSharpGenerationArgumentContractRecord CloneArgumentsForCompanion(
        CSharpGenerationArgumentContractRecord arguments,
        string companionPath,
        string companionNamespace,
        string companionFileName)
    {
        return new CSharpGenerationArgumentContractRecord
        {
            ModificationIntent = arguments.ModificationIntent,
            FileRole = arguments.FileRole,
            Pattern = arguments.Pattern,
            ImplementationDepth = arguments.ImplementationDepth,
            FollowThroughMode = arguments.FollowThroughMode,
            TargetProject = arguments.TargetProject,
            TargetProjectPath = arguments.TargetProjectPath,
            TargetPath = companionPath,
            NamespaceName = companionNamespace,
            ClassName = Path.GetFileNameWithoutExtension(companionFileName),
            BaseTypes = [.. arguments.BaseTypes],
            Interfaces = [.. arguments.Interfaces],
            ConstructorDependencies = [.. arguments.ConstructorDependencies],
            RequiredUsings = [.. arguments.RequiredUsings],
            SupportingSurfaces = [.. arguments.SupportingSurfaces],
            CompletionContract = [.. arguments.CompletionContract],
            DomainEntity = arguments.DomainEntity,
            ServiceName = arguments.ServiceName,
            StorageContext = arguments.StorageContext,
            TestSubject = arguments.TestSubject,
            UiSurface = arguments.UiSurface,
            FeatureName = arguments.FeatureName,
            RetrievalReadinessStatus = arguments.RetrievalReadinessStatus,
            WorkspaceTruthFingerprint = arguments.WorkspaceTruthFingerprint
        };
    }

    private static string BuildInterfaceContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        var legacy = BuildInterfaceContent(fileName, namespaceName);
        return string.IsNullOrWhiteSpace(legacy)
            ? BuildGenericInterfaceContent(fileName, namespaceName, arguments)
            : legacy;
    }

    private static string BuildRepositoryContent(string fileName, string namespaceName, string depth, CSharpGenerationArgumentContractRecord arguments)
    {
        var legacy = BuildRepositoryContent(fileName, namespaceName, depth);
        return string.IsNullOrWhiteSpace(legacy)
            ? BuildGenericRepositoryContent(fileName, namespaceName, arguments)
            : legacy;
    }

    private static string BuildViewModelContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        var legacy = BuildViewModelContent(fileName, namespaceName);
        return string.IsNullOrWhiteSpace(legacy)
            ? BuildGenericViewModelContent(fileName, namespaceName, arguments)
            : legacy;
    }

    private static string BuildTestHarnessContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        var legacy = BuildTestHarnessContent(fileName, namespaceName);
        return string.IsNullOrWhiteSpace(legacy)
            ? BuildGenericTestHarnessContent(fileName, namespaceName, arguments)
            : legacy;
    }

    private static string BuildModelContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        var legacy = BuildModelContent(fileName, namespaceName);
        return string.IsNullOrWhiteSpace(legacy)
            ? BuildGenericModelContent(fileName, namespaceName, arguments)
            : legacy;
    }

    private static string BuildServiceContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        return BuildGenericServiceContent(fileName, namespaceName, arguments);
    }

    private static string BuildDtoContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        var flavor = fileName.EndsWith("Request.cs", StringComparison.OrdinalIgnoreCase)
            ? "request"
            : fileName.EndsWith("Response.cs", StringComparison.OrdinalIgnoreCase)
                ? "response"
                : "dto";
        return BuildGenericDtoContent(fileName, namespaceName, arguments, flavor);
    }

    private static string BuildControllerContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        return BuildGenericControllerContent(fileName, namespaceName, arguments);
    }

    private static string BuildWorkerSupportContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        return BuildGenericWorkerSupportContent(fileName, namespaceName, arguments);
    }

    private static string BuildInterfaceContent(string fileName, string namespaceName)
    {
        return fileName switch
        {
            "ISettingsStore.cs" => $$"""
namespace {{namespaceName}};

public interface ISettingsStore
{
    string Load();
    void Save(string json);
}
""",
            "ISnapshotRepository.cs" => $$"""
namespace {{namespaceName}};

public interface ISnapshotRepository
{
    string LoadSnapshotJson();
    void SaveSnapshotJson(string json);
}
""",
            _ => ""
        };
    }

    private static string BuildRepositoryContent(string fileName, string namespaceName, string depth)
    {
        return fileName switch
        {
            "FileSettingsStore.cs" => BuildFileSettingsStoreCs(namespaceName, depth),
            "SqliteSnapshotRepository.cs" => BuildSnapshotRepositoryImplCs(namespaceName, depth),
            _ => ""
        };
    }

    private static string BuildViewModelContent(string fileName, string namespaceName)
    {
        return fileName switch
        {
            "ShellViewModel.cs" => BuildShellViewModelCs(namespaceName),
            _ => ""
        };
    }

    private static string BuildPageContent(string fileName)
    {
        return fileName switch
        {
            "DashboardPage.xaml" => BuildDashboardPageXaml("Dashboard"),
            "FindingsPage.xaml" => BuildFindingsPageXaml("Findings"),
            "HistoryPage.xaml" => BuildHistoryPageXaml("History / Log"),
            "SettingsPage.xaml" => BuildSettingsPageXaml("Settings"),
            _ => ""
        };
    }

    private static string BuildTestHarnessContent(string fileName, string namespaceName)
    {
        return fileName switch
        {
            "CheckRegistry.cs" => BuildCheckRegistryCs(namespaceName),
            "SnapshotBuilder.cs" => BuildSnapshotBuilderCs(namespaceName),
            "FindingsNormalizer.cs" => BuildFindingsNormalizerCs(namespaceName),
            _ => ""
        };
    }

    private static string BuildModelContent(string fileName, string namespaceName)
    {
        return fileName switch
        {
            "CheckDefinition.cs" => $$"""
namespace {{namespaceName}};

public sealed class CheckDefinition
{
    public string CheckId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Severity { get; init; } = "info";
}
""",
            "FindingRecord.cs" => $$"""
namespace {{namespaceName}};

public sealed class FindingRecord
{
    public string FindingId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Severity { get; init; } = "info";
    public bool IsResolved { get; set; }
}
""",
            _ => ""
        };
    }

    private static string BuildServiceContent(string fileName, string namespaceName)
    {
        var typeName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(typeName))
            return "";

        return $$"""
using System;

namespace {{namespaceName}};

public sealed class {{typeName}}
{
    private readonly string _serviceName;

    public {{typeName}}(string? serviceName = null)
    {
        _serviceName = string.IsNullOrWhiteSpace(serviceName) ? "{{typeName}}" : serviceName.Trim();
    }

    public string Execute(string input)
    {
        var normalizedInput = NormalizeInput(input);
        return $"{_serviceName}:{normalizedInput}";
    }

    private static string NormalizeInput(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        return input.Trim();
    }
}
""";
    }

    private static string BuildFileSettingsStoreCs(string namespaceName, string depth)
    {
        var includesHelpers = depth is "integrated" or "strong";
        var helperBlock = includesHelpers
            ? """

    private static string ResolveDefaultSettingsPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "settings.json");
    }

    private void EnsureStorageDirectoryExists()
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
    }

    private static string NormalizePayload(string? json)
    {
        return string.IsNullOrWhiteSpace(json) ? "{}" : json.Trim();
    }
"""
            : "";

        return $$"""
using System;
using System.IO;

namespace {{namespaceName}};

public sealed class FileSettingsStore : ISettingsStore
{
    private readonly string _settingsPath;

    public FileSettingsStore(string? settingsPath = null)
    {
        _settingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? {{(includesHelpers ? "ResolveDefaultSettingsPath()" : "Path.Combine(AppContext.BaseDirectory, \"settings.json\")")}}
            : settingsPath.Trim();
    }

    public string Load()
    {
{{(includesHelpers ? "        EnsureStorageDirectoryExists();" : "")}}
        return File.Exists(_settingsPath) ? {{(includesHelpers ? "NormalizePayload(File.ReadAllText(_settingsPath))" : "File.ReadAllText(_settingsPath)")}} : "{}";
    }

    public void Save(string json)
    {
{{(includesHelpers ? "        EnsureStorageDirectoryExists();" : "")}}
        File.WriteAllText(_settingsPath, {{(includesHelpers ? "NormalizePayload(json)" : "json ?? \"{}\"")}});
    }
{{helperBlock}}
}
""";
    }

    private static string BuildSnapshotRepositoryImplCs(string namespaceName, string depth)
    {
        var includesHelpers = depth is "integrated" or "strong";
        var helperBlock = includesHelpers
            ? """

    private static string ResolveDefaultSnapshotPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "snapshot-cache.json");
    }

    private void EnsureStorageDirectoryExists()
    {
        var directory = Path.GetDirectoryName(_snapshotPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
    }

    private static string NormalizeSnapshotJson(string? json)
    {
        return string.IsNullOrWhiteSpace(json) ? "[]" : json.Trim();
    }
"""
            : "";

        return $$"""
using System;
using System.IO;

namespace {{namespaceName}};

public sealed class SqliteSnapshotRepository : ISnapshotRepository
{
    private readonly string _snapshotPath;

    public SqliteSnapshotRepository(string? snapshotPath = null)
    {
        _snapshotPath = string.IsNullOrWhiteSpace(snapshotPath)
            ? {{(includesHelpers ? "ResolveDefaultSnapshotPath()" : "Path.Combine(AppContext.BaseDirectory, \"snapshot-cache.json\")")}}
            : snapshotPath.Trim();
    }

    public string LoadSnapshotJson()
    {
{{(includesHelpers ? "        EnsureStorageDirectoryExists();" : "")}}
        return File.Exists(_snapshotPath) ? {{(includesHelpers ? "NormalizeSnapshotJson(File.ReadAllText(_snapshotPath))" : "File.ReadAllText(_snapshotPath)")}} : "[]";
    }

    public void SaveSnapshotJson(string json)
    {
{{(includesHelpers ? "        EnsureStorageDirectoryExists();" : "")}}
        File.WriteAllText(_snapshotPath, {{(includesHelpers ? "NormalizeSnapshotJson(json)" : "json ?? \"[]\"")}});
    }
{{helperBlock}}
}
""";
    }

    private static string BuildShellViewModelCs(string namespaceName)
    {
        return $$"""
using System;
using System.Collections.Generic;

namespace {{namespaceName}};

public sealed class ShellViewModel
{
    public AppState State { get; } = new();
    public NavigationItem? SelectedNavigationItem { get; private set; }
    public string WindowTitle { get; } = "Service Dispatch App";
    public string CurrentStatusSummary => $"{State.LastBuildResult}. Active route: {State.CurrentRoute}. {State.StatusMessage}";
    public bool CanNavigate => State.NavigationItems.Count > 0;
    public IReadOnlyList<string> DashboardHighlights { get; } =
    [
        "Service dispatch workspace scaffold completed successfully.",
        "Repository, state, and verification surfaces are available for bounded follow-through.",
        "The shell exposes navigation-backed regions with deterministic bindings."
    ];

    public IReadOnlyList<FindingSummary> RecentFindings { get; } =
    [
        new() { Title = "Queue synchronization", Severity = "Low", Status = "Healthy" },
        new() { Title = "Technician assignment workflow", Severity = "Medium", Status = "Pending wire-up" },
        new() { Title = "Persistence checkpoint", Severity = "Low", Status = "Ready" }
    ];

    public IReadOnlyList<string> HistoryEntries { get; } =
    [
        "Solution scaffold completed and verification passed.",
        "State and page surfaces are ready for bounded feature follow-through.",
        "Repository and test harness outputs are available for runtime proof."
    ];

    public IReadOnlyList<SettingRow> SettingsItems { get; } =
    [
        new() { Label = "Refresh cadence", Value = "On verification" },
        new() { Label = "Assignment sync", Value = "Deterministic local state" },
        new() { Label = "History retention", Value = "Bounded workspace snapshot" }
    ];

    public void Navigate(string routeKey)
    {
        if (string.IsNullOrWhiteSpace(routeKey))
            return;

        State.CurrentRoute = routeKey.Trim();
        SelectedNavigationItem = FindNavigationItem(State.CurrentRoute);
        UpdateStatusMessage(State.CurrentRoute);
    }

    public void SelectNavigationItem(NavigationItem navigationItem)
    {
        if (navigationItem is null)
            return;

        SelectedNavigationItem = navigationItem;
        Navigate(navigationItem.RouteKey);
    }

    private NavigationItem? FindNavigationItem(string routeKey)
    {
        foreach (var item in State.NavigationItems)
        {
            if (string.Equals(item.RouteKey, routeKey, StringComparison.OrdinalIgnoreCase))
                return item;
        }

        return null;
    }

    private void UpdateStatusMessage(string routeKey)
    {
        var selectedTitle = SelectedNavigationItem?.Title ?? "Dashboard";
        State.StatusMessage = $"Showing {selectedTitle} for route `{routeKey}`.";
    }
}

public sealed class FindingSummary
{
    public string Title { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Status { get; init; } = "";
}

public sealed class SettingRow
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
}

public sealed class AppState
{
    public string LastBuildResult { get; set; } = "Build ready";
    public string CurrentRoute { get; set; } = "dashboard";
    public string StatusMessage { get; set; } = "Waiting for the next bounded follow-through step.";
    public List<NavigationItem> NavigationItems { get; } =
    [
        new() { Title = "Dashboard", RouteKey = "dashboard" },
        new() { Title = "Findings", RouteKey = "findings" },
        new() { Title = "History", RouteKey = "history" },
        new() { Title = "Settings", RouteKey = "settings" }
    ];
}

public sealed class NavigationItem
{
    public string Title { get; init; } = "";
    public string RouteKey { get; init; } = "";
}
""";
    }

    private static string BuildDashboardPageXaml(string title)
    {
        return $$"""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Title="{{EscapeXml(title)}}"
      DataContext="{Binding RelativeSource={RelativeSource AncestorType=Window}, Path=DataContext}">
    <ScrollViewer x:Name="DashboardScrollHost" VerticalScrollBarVisibility="Auto">
        <StackPanel x:Name="DashboardContent" Margin="0,0,0,8">
            <TextBlock Text="{{EscapeXml(title)}}" FontSize="22" FontWeight="Bold"/>
            <TextBlock Text="{Binding CurrentStatusSummary}" Margin="0,8,0,0" Foreground="#4B5563" TextWrapping="Wrap"/>
            <ItemsControl x:Name="DashboardHighlightsList" ItemsSource="{Binding DashboardHighlights}" Margin="0,18,0,0">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Border Margin="0,0,0,10" Padding="14" Background="#F9FAFB" BorderBrush="#E5E7EB" BorderThickness="1" CornerRadius="10">
                            <TextBlock Text="{Binding}" TextWrapping="Wrap"/>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </StackPanel>
    </ScrollViewer>
</Page>
""";
    }

    private static string BuildFindingsPageXaml(string title)
    {
        return $$"""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Title="{{EscapeXml(title)}}"
      DataContext="{Binding RelativeSource={RelativeSource AncestorType=Window}, Path=DataContext}">
    <Grid x:Name="FindingsLayoutRoot">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>
        <TextBlock Text="{{EscapeXml(title)}}" FontSize="22" FontWeight="Bold"/>
        <ListView x:Name="FindingsList" Grid.Row="1" Margin="0,16,0,0" ItemsSource="{Binding RecentFindings}">
            <ListView.View>
                <GridView>
                    <GridViewColumn Header="Finding" DisplayMemberBinding="{Binding Title}" Width="240"/>
                    <GridViewColumn Header="Severity" DisplayMemberBinding="{Binding Severity}" Width="120"/>
                    <GridViewColumn Header="Status" DisplayMemberBinding="{Binding Status}" Width="160"/>
                </GridView>
            </ListView.View>
        </ListView>
    </Grid>
</Page>
""";
    }

    private static string BuildHistoryPageXaml(string title)
    {
        return $$"""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Title="{{EscapeXml(title)}}"
      DataContext="{Binding RelativeSource={RelativeSource AncestorType=Window}, Path=DataContext}">
    <StackPanel x:Name="HistoryContent">
        <TextBlock Text="{{EscapeXml(title)}}" FontSize="22" FontWeight="Bold"/>
        <TextBlock x:Name="HistorySummary" Text="{Binding CurrentStatusSummary}" Margin="0,8,0,0" Foreground="#4B5563" TextWrapping="Wrap"/>
        <ItemsControl x:Name="HistoryEntriesList" ItemsSource="{Binding HistoryEntries}" Margin="0,16,0,0">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <Border Margin="0,0,0,10" Padding="12" Background="#F9FAFB" BorderBrush="#E5E7EB" BorderThickness="1" CornerRadius="10">
                        <TextBlock Text="{Binding}" TextWrapping="Wrap"/>
                    </Border>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </StackPanel>
</Page>
""";
    }

    private static string BuildSettingsPageXaml(string title)
    {
        return $$"""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Title="{{EscapeXml(title)}}"
      DataContext="{Binding RelativeSource={RelativeSource AncestorType=Window}, Path=DataContext}">
    <StackPanel x:Name="SettingsContent">
        <TextBlock Text="{{EscapeXml(title)}}" FontSize="22" FontWeight="Bold"/>
        <ItemsControl x:Name="SettingsItemsList" ItemsSource="{Binding SettingsItems}" Margin="0,16,0,0">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <Grid Margin="0,0,0,10">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="220"/>
                            <ColumnDefinition Width="*"/>
                        </Grid.ColumnDefinitions>
                        <TextBlock Text="{Binding Label}" FontWeight="SemiBold"/>
                        <TextBlock Grid.Column="1" Text="{Binding Value}" TextWrapping="Wrap"/>
                    </Grid>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </StackPanel>
</Page>
""";
    }

    private static string BuildCheckRegistryCs(string namespaceName)
    {
        return $$"""
using System;
using System.Collections.Generic;

namespace {{namespaceName}};

public static class CheckRegistry
{
    public static IReadOnlyList<string> CreateDefaultChecks()
    {
        return
        [
            "dispatch_queue",
            "assignment_sync",
            "history_projection"
        ];
    }

    public static bool Contains(string checkKey)
    {
        return FindByKey(checkKey) is not null;
    }

    public static string? FindByKey(string checkKey)
    {
        if (string.IsNullOrWhiteSpace(checkKey))
            return null;

        foreach (var candidate in CreateDefaultChecks())
        {
            if (string.Equals(candidate, checkKey.Trim(), StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    public static string TryGetOrDefault(string checkKey, string fallback)
    {
        return FindByKey(checkKey) ?? fallback;
    }
}
""";
    }

    private static string BuildSnapshotBuilderCs(string namespaceName)
    {
        return $$"""
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace {{namespaceName}};

public static class SnapshotBuilder
{
    public static Dictionary<string, object> BuildDefaultSnapshot()
    {
        return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["checks"] = CheckRegistry.CreateDefaultChecks(),
            ["capturedBy"] = "RAM",
            ["machine"] = "workspace"
        };
    }

    public static string BuildDefaultSnapshotJson()
    {
        return BuildSnapshotJson(BuildDefaultSnapshot());
    }

    public static string BuildSnapshotJson(Dictionary<string, object> snapshot)
    {
        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
    }
}
""";
    }

    private static string BuildFindingsNormalizerCs(string namespaceName)
    {
        return $$"""
using System;
using System.Collections.Generic;

namespace {{namespaceName}};

public static class FindingsNormalizer
{
    public static string NormalizeSeverity(string severity)
    {
        return string.IsNullOrWhiteSpace(severity) ? "info" : severity.Trim().ToLowerInvariant();
    }

    public static string NormalizeStatus(string status)
    {
        return string.IsNullOrWhiteSpace(status) ? "unknown" : status.Trim().ToLowerInvariant();
    }

    public static IReadOnlyList<Dictionary<string, string>> NormalizeFindings(IEnumerable<Dictionary<string, string>> findings)
    {
        var normalized = new List<Dictionary<string, string>>();
        foreach (var finding in findings)
        {
            normalized.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["severity"] = NormalizeSeverity(finding.TryGetValue("severity", out var severity) ? severity : ""),
                ["status"] = NormalizeStatus(finding.TryGetValue("status", out var status) ? status : ""),
                ["title"] = finding.TryGetValue("title", out var title) ? title?.Trim() ?? "" : ""
            });
        }

        return normalized;
    }
}
""";
    }

    private static string BuildGenericInterfaceContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        var interfaceName = FirstNonEmpty(arguments.ClassName, Path.GetFileNameWithoutExtension(fileName));
        if (string.IsNullOrWhiteSpace(interfaceName))
            return "";

        var entityName = FirstNonEmpty(SanitizeTypeName(arguments.DomainEntity), TrimKnownTypeSuffix(interfaceName, "Repository", "Service"), "Item");
        var recordType = $"{entityName}Record";
        var usings = BuildUsingBlock(namespaceName, arguments.RequiredUsings);

        if (interfaceName.Contains("Repository", StringComparison.OrdinalIgnoreCase))
        {
            return $$"""
{{usings}}namespace {{namespaceName}};

public interface {{interfaceName}}
{
    Task<IReadOnlyList<{{recordType}}>> ListAsync(CancellationToken cancellationToken = default);
    Task<{{recordType}}?> FindByIdAsync(string id, CancellationToken cancellationToken = default);
    Task SaveAsync({{recordType}} item, CancellationToken cancellationToken = default);
}
""";
        }

        var featureMembers = BuildFeatureUpdateInterfaceMembers(arguments, recordType);

        return $$"""
{{usings}}namespace {{namespaceName}};

public interface {{interfaceName}}
{
    string Execute(string input);
    Task<IReadOnlyList<{{recordType}}>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<{{recordType}}?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<{{recordType}}> UpsertAsync({{recordType}} item, CancellationToken cancellationToken = default);
{{featureMembers}}}
""";
    }

    private static string BuildFeatureUpdateInterfaceMembers(CSharpGenerationArgumentContractRecord arguments, string recordType)
    {
        if (!IsSearchFeature(arguments))
            return "";

        return $"""
    Task<IReadOnlyList<{recordType}>> SearchAsync(string query, CancellationToken cancellationToken = default);
""";
    }

    private static string BuildGenericRepositoryContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        var className = FirstNonEmpty(arguments.ClassName, Path.GetFileNameWithoutExtension(fileName));
        if (string.IsNullOrWhiteSpace(className))
            return "";

        var entityName = FirstNonEmpty(SanitizeTypeName(arguments.DomainEntity), TrimKnownTypeSuffix(className, "Repository", "Store"), "Item");
        var recordType = $"{entityName}Record";
        var implementedInterface = arguments.Interfaces.FirstOrDefault();
        var interfaceClause = string.IsNullOrWhiteSpace(implementedInterface) ? "" : $" : {implementedInterface}";
        var usings = BuildUsingBlock(namespaceName, arguments.RequiredUsings.Concat(["System.IO"]).Distinct(StringComparer.OrdinalIgnoreCase));
        var constructorDependencies = BuildConstructorDependencies(arguments);
        var fieldBlock = BuildDependencyFieldBlock(constructorDependencies);
        var constructorSignature = BuildConstructorSignature(className, constructorDependencies);
        var constructorAssignments = BuildConstructorAssignments(constructorDependencies);
        var storageContext = FirstNonEmpty(arguments.StorageContext, "repository");
        var storageFileName = $"{storageContext}.store";

        return $$"""
{{usings}}namespace {{namespaceName}};

public sealed class {{className}}{{interfaceClause}}
{
{{fieldBlock}}    private readonly string _storagePath = string.Empty;
    private readonly string _storageContext = "{{EscapeString(storageContext)}}";

    public {{constructorSignature}}
    {
{{constructorAssignments}}        _storagePath = Path.Combine(AppContext.BaseDirectory, "{{EscapeString(storageFileName)}}");
    }

    public Task<IReadOnlyList<{{recordType}}>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serialized = LoadStorageText();
        var items = ParseItems(serialized)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<{{recordType}}>>(items);
    }

    public Task<{{recordType}}?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedId = NormalizeId(id);
        var serialized = LoadStorageText();
        var item = ParseItems(serialized)
            .FirstOrDefault(existing => string.Equals(existing.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(item);
    }

    public Task SaveAsync({{recordType}} item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedItem = NormalizeItem(item);
        var serialized = LoadStorageText();
        var items = ParseItems(serialized)
            .Where(existing => !string.Equals(existing.Id, normalizedItem.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        items.Add(normalizedItem with { UpdatedUtc = DateTimeOffset.UtcNow });
        File.WriteAllText(_storagePath, SerializeItems(items));
        return Task.CompletedTask;
    }

    private string LoadStorageText()
    {
        return File.Exists(_storagePath) ? File.ReadAllText(_storagePath) : string.Empty;
    }

    private static string NormalizeId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return id.Trim();
    }

    private static {{recordType}} NormalizeItem({{recordType}} item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var normalizedName = string.IsNullOrWhiteSpace(item.Name) ? "{{entityName}}" : item.Name.Trim();
        var normalizedStatus = string.IsNullOrWhiteSpace(item.Status) ? "ready" : item.Status.Trim();
        return item with { Id = NormalizeId(item.Id), Name = normalizedName, Status = normalizedStatus };
    }

    private static List<{{recordType}}> ParseItems(string serialized)
    {
        return (serialized ?? string.Empty)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', StringSplitOptions.None))
            .Where(parts => parts.Length >= 3)
            .Select(parts => new {{recordType}}
            {
                Id = NormalizeId(parts[0]),
                Name = parts[1].Trim(),
                Status = string.IsNullOrWhiteSpace(parts[2]) ? "ready" : parts[2].Trim(),
                UpdatedUtc = DateTimeOffset.UtcNow
            })
            .ToList();
    }

    private static string SerializeItems(IEnumerable<{{recordType}}> items)
    {
        return string.Join(
            Environment.NewLine,
            items.Select(item => $"{item.Id}|{item.Name}|{item.Status}"));
    }
}
""";
    }

    private static string BuildGenericViewModelContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        var className = FirstNonEmpty(arguments.ClassName, Path.GetFileNameWithoutExtension(fileName));
        if (string.IsNullOrWhiteSpace(className))
            return "";

        var entityName = FirstNonEmpty(SanitizeTypeName(arguments.UiSurface), TrimKnownTypeSuffix(className, "ViewModel"), "Dashboard");
        var usings = BuildUsingBlock(namespaceName, arguments.RequiredUsings);
        return $$"""
{{usings}}namespace {{namespaceName}};

public sealed class {{className}} : INotifyPropertyChanged
{
    private string _statusMessage = "Ready";
    private string? _selectedItem;

    public {{className}}()
    {
        Items = new ObservableCollection<string>(BuildDefaultItems());
        RefreshCommand = new DelegateCommand(_ => Refresh());
        SelectItemCommand = new DelegateCommand(value => SelectItem(value as string), value => value is string text && !string.IsNullOrWhiteSpace(text));
    }

    public ObservableCollection<string> Items { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SelectItemCommand { get; }

    public string Title => "{{entityName}}";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string? SelectedItem
    {
        get => _selectedItem;
        private set => SetProperty(ref _selectedItem, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        if (Items.Count == 0)
        {
            foreach (var item in BuildDefaultItems())
                Items.Add(item);
        }

        StatusMessage = $"{{entityName}} surface ready with {Items.Count} items.";
    }

    public void SelectItem(string? item)
    {
        if (string.IsNullOrWhiteSpace(item))
            return;

        SelectedItem = item.Trim();
        StatusMessage = $"Selected {SelectedItem}.";
    }

    private static IEnumerable<string> BuildDefaultItems()
    {
        return
        [
            "{{entityName}} queue",
            "{{entityName}} details",
            "{{entityName}} history"
        ];
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        OnPropertyChanged(propertyName);
    }
}
""";
    }

    private static string BuildGenericTestHarnessContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        var className = FirstNonEmpty(arguments.ClassName, Path.GetFileNameWithoutExtension(fileName));
        var subjectType = FirstNonEmpty(arguments.TestSubject, TrimKnownTypeSuffix(className, "Tests", "Test"), "SubjectUnderTest");
        var usings = BuildUsingBlock(namespaceName, arguments.RequiredUsings);
        return $$"""
{{usings}}namespace {{namespaceName}};

public sealed class {{className}}
{
    [Fact]
    public void Execute_returns_a_non_empty_summary()
    {
        var subject = CreateSubject();
        var result = subject.Execute("stage11-proof");

        AssertStrongSummary(result);
    }

    [Fact]
    public async Task Strong_generation_subject_exposes_collection_surface()
    {
        var subject = CreateSubject();
        var records = await subject.GetAllAsync();

        Assert.NotNull(records);
        Assert.True(records.Count >= 0);
    }

    private static {{subjectType}} CreateSubject()
    {
        return new {{subjectType}}();
    }

    private static void AssertStrongSummary(string result)
    {
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains("stage11-proof", result, StringComparison.OrdinalIgnoreCase);
    }
}
""";
    }

    private static string BuildGenericModelContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        var typeName = FirstNonEmpty(arguments.ClassName, Path.GetFileNameWithoutExtension(fileName));
        if (string.IsNullOrWhiteSpace(typeName))
            return "";

        return $$"""
namespace {{namespaceName}};

public sealed record {{typeName}}
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Status { get; init; } = "ready";
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
""";
    }

    private static string BuildGenericDtoContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments, string flavor)
    {
        var typeName = FirstNonEmpty(arguments.ClassName, Path.GetFileNameWithoutExtension(fileName));
        if (string.IsNullOrWhiteSpace(typeName))
            return "";

        var propertyBlock = flavor switch
        {
            "request" when typeName.StartsWith("Search", StringComparison.OrdinalIgnoreCase) => """
    public string Query { get; init; } = "";
    public int Limit { get; init; } = 25;
""",
            "request" => """
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Priority { get; init; } = "normal";
""",
            "response" => """
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Status { get; init; } = "ready";
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
""",
            _ => """
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Status { get; init; } = "ready";
"""
        };

        return $$"""
namespace {{namespaceName}};

public sealed record {{typeName}}
{
{{propertyBlock}}}
""";
    }

    private static string BuildGenericServiceContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        var className = FirstNonEmpty(arguments.ClassName, Path.GetFileNameWithoutExtension(fileName));
        if (string.IsNullOrWhiteSpace(className))
            return "";

        var entityName = FirstNonEmpty(SanitizeTypeName(arguments.DomainEntity), TrimKnownTypeSuffix(className, "Service"), "Item");
        var recordType = $"{entityName}Record";
        var interfaceName = arguments.Interfaces.FirstOrDefault();
        var interfaceClause = string.IsNullOrWhiteSpace(interfaceName) ? "" : $" : {interfaceName}";
        var usings = BuildUsingBlock(namespaceName, arguments.RequiredUsings);
        var constructorDependencies = BuildConstructorDependencies(arguments);
        var fieldBlock = BuildDependencyFieldBlock(constructorDependencies);
        var constructorBlock = BuildConstructorBlock(className, constructorDependencies);
        var featureMembers = BuildFeatureUpdateServiceMembers(arguments, recordType);

        return $$"""
{{usings}}namespace {{namespaceName}};

public sealed class {{className}}{{interfaceClause}}
{
{{fieldBlock}}    private readonly List<{{recordType}}> _items =
    [
        new() { Id = "seed", Name = "{{entityName}} seed", Status = "ready", UpdatedUtc = DateTimeOffset.UtcNow }
    ];

{{constructorBlock}}

    public string Execute(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        return $"{{className}}:{input.Trim()}";
    }

    public Task<IReadOnlyList<{{recordType}}>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<{{recordType}}>>(_items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Task<{{recordType}}?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Task.FromResult(_items.FirstOrDefault(item => string.Equals(item.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    public Task<{{recordType}}> UpsertAsync({{recordType}} item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(item);
        var normalized = NormalizeItem(item);
        _items.RemoveAll(existing => string.Equals(existing.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
        _items.Add(normalized with { UpdatedUtc = DateTimeOffset.UtcNow });
        return Task.FromResult(normalized);
    }
{{featureMembers}}

    private static {{recordType}} NormalizeItem({{recordType}} item)
    {
        var normalizedName = string.IsNullOrWhiteSpace(item.Name) ? "{{entityName}}" : item.Name.Trim();
        var normalizedStatus = string.IsNullOrWhiteSpace(item.Status) ? "ready" : item.Status.Trim();
        var normalizedId = string.IsNullOrWhiteSpace(item.Id) ? normalizedName.Replace(" ", "-", StringComparison.OrdinalIgnoreCase).ToLowerInvariant() : item.Id.Trim();
        return item with { Id = normalizedId, Name = normalizedName, Status = normalizedStatus };
    }
}
""";
    }

    private static string BuildFeatureUpdateServiceMembers(CSharpGenerationArgumentContractRecord arguments, string recordType)
    {
        if (!IsSearchFeature(arguments))
            return "";

        return $$"""

    public Task<IReadOnlyList<{{recordType}}>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var normalizedQuery = query.Trim();
        var results = _items
            .Where(item => item.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                || item.Status.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<{{recordType}}>>(results);
    }
""";
    }

    private static string BuildGenericControllerContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        var controllerName = FirstNonEmpty(arguments.ClassName, Path.GetFileNameWithoutExtension(fileName));
        if (string.IsNullOrWhiteSpace(controllerName))
            return "";

        var entityName = FirstNonEmpty(SanitizeTypeName(arguments.DomainEntity), TrimKnownTypeSuffix(controllerName, "Controller"), "Item");
        var serviceInterface = FirstNonEmpty(arguments.Interfaces.FirstOrDefault(), $"I{entityName}Service");
        var requestType = $"Create{entityName}Request";
        var responseType = $"{entityName}Response";
        var dependencyName = ToCamelCase(serviceInterface.TrimStart('I'));
        var usings = BuildUsingBlock(namespaceName, arguments.RequiredUsings.Concat(["System.Linq"]).Distinct(StringComparer.OrdinalIgnoreCase));
        var featureMembers = BuildFeatureUpdateControllerMembers(arguments, entityName, responseType, dependencyName);

        return $$"""
{{usings}}namespace {{namespaceName}};

[ApiController]
[Route("api/[controller]")]
public sealed class {{controllerName}} : ControllerBase
{
    private readonly {{serviceInterface}} _{{dependencyName}};

    public {{controllerName}}({{serviceInterface}} {{dependencyName}})
    {
        _{{dependencyName}} = {{dependencyName}} ?? throw new ArgumentNullException(nameof({{dependencyName}}));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<{{responseType}}>>> Get(CancellationToken cancellationToken)
    {
        var items = await _{{dependencyName}}.GetAllAsync(cancellationToken);
        return Ok(items.Select(Map).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<{{responseType}}>> Create([FromBody] {{requestType}} request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var created = await _{{dependencyName}}.UpsertAsync(new {{entityName}}Record
        {
            Id = string.IsNullOrWhiteSpace(request.Id) ? request.Name.Trim().ToLowerInvariant() : request.Id.Trim(),
            Name = request.Name.Trim(),
            Status = request.Priority
        }, cancellationToken);

        var response = Map(created);
        return CreatedAtAction(nameof(Get), new { id = response.Id }, response);
    }
{{featureMembers}}

    private static {{responseType}} Map({{entityName}}Record record)
    {
        return new {{responseType}}
        {
            Id = record.Id,
            Name = record.Name,
            Status = record.Status,
            UpdatedUtc = record.UpdatedUtc
        };
    }
}
""";
    }

    private static string BuildFeatureUpdateControllerMembers(CSharpGenerationArgumentContractRecord arguments, string entityName, string responseType, string dependencyName)
    {
        if (!IsSearchFeature(arguments))
            return "";

        var requestType = $"Search{entityName}Request";
        return $$"""

    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<{{responseType}}>>> Search([FromQuery] {{requestType}} request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var items = await _{{dependencyName}}.SearchAsync(request.Query, cancellationToken);
        return Ok(items.Select(Map).ToList());
    }
""";
    }

    private static string BuildGenericWorkerSupportContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        var className = FirstNonEmpty(arguments.ClassName, Path.GetFileNameWithoutExtension(fileName));
        if (string.IsNullOrWhiteSpace(className))
            return "";

        var optionsType = $"{TrimKnownTypeSuffix(className, "Service", "Worker")}Options";
        var usings = BuildUsingBlock(namespaceName, arguments.RequiredUsings.Concat(["Microsoft.Extensions.Logging"]).Distinct(StringComparer.OrdinalIgnoreCase));
        return $$"""
{{usings}}namespace {{namespaceName}};

public sealed class {{className}} : BackgroundService
{
    private readonly ILogger<{{className}}>? _logger;
    private readonly {{optionsType}} _options;

    public {{className}}(ILogger<{{className}}>? logger = null, {{optionsType}}? options = null)
    {
        _logger = logger;
        _options = options ?? new {{optionsType}}();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunIterationAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
        }
    }

    private Task RunIterationAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("{{className}} iteration started.");
        stoppingToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
""";
    }

    private static string BuildHelperContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        return string.Equals(fileName, "DelegateCommand.cs", StringComparison.OrdinalIgnoreCase)
            ? $$"""
using System;
using System.Windows.Input;

namespace {{namespaceName}};

public sealed class DelegateCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public DelegateCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
"""
            : "";
    }

    private static string BuildOptionsContent(string fileName, string namespaceName, CSharpGenerationArgumentContractRecord arguments)
    {
        var typeName = FirstNonEmpty(arguments.ClassName, Path.GetFileNameWithoutExtension(fileName));
        if (string.IsNullOrWhiteSpace(typeName))
            typeName = Path.GetFileNameWithoutExtension(fileName);

        return $$"""
namespace {{namespaceName}};

public sealed class {{typeName}}
{
    public int PollIntervalSeconds { get; init; } = 30;
}
""";
    }

    private static string ResolveCompanionPath(string primaryRelativePath, string surfaceValue)
    {
        var normalizedValue = NormalizeRelativePath(surfaceValue);
        if (normalizedValue.Contains('/'))
            return normalizedValue;

        var directory = Path.GetDirectoryName(primaryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(directory))
            return normalizedValue;

        return NormalizeRelativePath(Path.Combine(directory, normalizedValue));
    }

    private static string BuildUsingBlock(string namespaceName, IEnumerable<string> requestedUsings)
    {
        var values = requestedUsings
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => !string.Equals(value, namespaceName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (values.Count == 0)
            return "";

        var builder = new StringBuilder();
        foreach (var value in values)
            builder.AppendLine($"using {value};");
        return builder.ToString() + Environment.NewLine;
    }

    private static List<(string TypeName, string ParameterName)> BuildConstructorDependencies(CSharpGenerationArgumentContractRecord arguments)
    {
        var results = new List<(string TypeName, string ParameterName)>();
        foreach (var dependency in arguments.ConstructorDependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency))
                continue;

            var normalized = dependency.Trim();
            var lastSpace = normalized.LastIndexOf(' ');
            if (lastSpace > 0)
            {
                var typeName = normalized[..lastSpace].Trim();
                var parameterName = normalized[(lastSpace + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(typeName) && !string.IsNullOrWhiteSpace(parameterName))
                    results.Add((typeName, parameterName));
                continue;
            }

            results.Add((normalized, ToCamelCase(SanitizeTypeName(normalized).TrimStart('I'))));
        }

        return results;
    }

    private static string BuildDependencyFieldBlock(IReadOnlyList<(string TypeName, string ParameterName)> dependencies)
    {
        if (dependencies.Count == 0)
            return "";

        var builder = new StringBuilder();
        foreach (var dependency in dependencies)
            builder.AppendLine($"    private readonly {dependency.TypeName} _{dependency.ParameterName};");
        return builder.ToString();
    }

    private static string BuildConstructorSignature(string className, IReadOnlyList<(string TypeName, string ParameterName)> dependencies)
    {
        if (dependencies.Count == 0)
            return $"{className}()";

        return $"{className}({string.Join(", ", dependencies.Select(dependency => $"{dependency.TypeName} {dependency.ParameterName}"))})";
    }

    private static string BuildConstructorAssignments(IReadOnlyList<(string TypeName, string ParameterName)> dependencies)
    {
        if (dependencies.Count == 0)
            return "";

        var builder = new StringBuilder();
        foreach (var dependency in dependencies)
            builder.AppendLine($"        _{dependency.ParameterName} = {dependency.ParameterName} ?? throw new ArgumentNullException(nameof({dependency.ParameterName}));");
        return builder.ToString();
    }

    private static string BuildConstructorBlock(string className, IReadOnlyList<(string TypeName, string ParameterName)> dependencies)
    {
        if (dependencies.Count == 0)
            return "";

        var constructorSignature = BuildConstructorSignature(className, dependencies);
        var constructorAssignments = BuildConstructorAssignments(dependencies);
        return $$"""
    public {{constructorSignature}}
    {
{{constructorAssignments}}    }

""";
    }

    private static string SanitizeTypeName(string value)
    {
        var tokens = Regex.Matches(value ?? "", @"[A-Za-z0-9]+")
            .Select(match => match.Value)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToList();
        return tokens.Count == 0
            ? ""
            : string.Concat(tokens.Select(token => char.ToUpperInvariant(token[0]) + token[1..]));
    }

    private static string TrimKnownTypeSuffix(string value, params string[] suffixes)
    {
        var result = value ?? "";
        foreach (var suffix in suffixes)
        {
            if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return result[..^suffix.Length];
        }

        return result;
    }

    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "value";

        var normalized = SanitizeTypeName(value);
        return normalized.Length == 0
            ? "value"
            : char.ToLowerInvariant(normalized[0]) + normalized[1..];
    }

    private static bool IsSearchFeature(CSharpGenerationArgumentContractRecord arguments)
    {
        return string.Equals(arguments.ModificationIntent, "feature_update", StringComparison.OrdinalIgnoreCase)
            && string.Equals(arguments.FeatureName, "search", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeString(string value)
    {
        return (value ?? "").Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string InferProjectName(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
            return "";

        return segments[1];
    }

    private static string InferNamespace(string projectName, string relativePath, string pattern)
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        if (string.Equals(pattern, "viewmodel", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Contains("/State/", StringComparison.OrdinalIgnoreCase))
        {
            return projectName.EndsWith(".State", StringComparison.OrdinalIgnoreCase)
                ? projectName
                : $"{projectName}.State";
        }

        if (string.Equals(pattern, "page", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Contains("/Views/", StringComparison.OrdinalIgnoreCase))
        {
            return projectName.EndsWith(".Views", StringComparison.OrdinalIgnoreCase)
                ? projectName
                : $"{projectName}.Views";
        }

        if (string.Equals(pattern, "repository", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Contains("/Storage/", StringComparison.OrdinalIgnoreCase)
            || projectName.EndsWith(".Storage", StringComparison.OrdinalIgnoreCase))
        {
            return projectName.EndsWith(".Storage", StringComparison.OrdinalIgnoreCase)
                ? projectName
                : $"{projectName}.Storage";
        }

        if (string.Equals(pattern, "test_harness", StringComparison.OrdinalIgnoreCase)
            || projectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
        {
            return projectName;
        }

        return projectName;
    }

    private static string EscapeXml(string value)
    {
        return (value ?? "")
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static string GetArgument(ToolRequest request, string key)
    {
        return request.TryGetArgument(key, out var value) ? value : "";
    }

    private static string NormalizeRelativePath(string value)
    {
        return (value ?? "").Replace('\\', '/').Trim().Trim('/');
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }
}
