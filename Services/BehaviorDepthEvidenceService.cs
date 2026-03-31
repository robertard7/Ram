using System.IO;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class BehaviorDepthEvidenceService
{
    public BehaviorDepthEvidenceRecord Build(
        string workspaceRoot,
        string fullPath,
        string toolName,
        CSharpGenerationGuardrailEvaluationRecord evaluation)
    {
        if (evaluation is null)
            throw new ArgumentNullException(nameof(evaluation));

        var contract = evaluation.Contract ?? new CSharpGenerationPromptContractRecord();
        var relativePath = NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, fullPath));
        var fileName = Path.GetFileName(relativePath);
        var typeTokens = contract.RequiredTypeNames.Count > 0
            ? contract.RequiredTypeNames
            : [Path.GetFileNameWithoutExtension(fileName)];
        var memberTokens = contract.RequiredMemberNames.Count > 0
            ? contract.RequiredMemberNames
            : [];
        var callerPaths = FindCallerReferencePaths(workspaceRoot, relativePath, typeTokens, memberTokens);
        var diPaths = FindEvidencePaths(workspaceRoot, relativePath, typeTokens, ["AddSingleton(", "AddScoped(", "AddTransient(", "register_di_service"]);
        var bindingPaths = FindEvidencePaths(workspaceRoot, relativePath, memberTokens.Count == 0 ? typeTokens : memberTokens, ["{Binding", "NavigationItems", "DashboardHighlights", "RecentFindings", "HistoryEntries", "SettingsItems"]);
        var repositoryPaths = contract.Profile == CSharpGenerationProfile.RepositoryImplementation
            ? FindRepositoryConsumerPaths(workspaceRoot, relativePath, contract, typeTokens)
            : FindEvidencePaths(workspaceRoot, relativePath, typeTokens, ["Repository", "Store", "Service"]);
        var testPaths = FindEvidencePaths(workspaceRoot, relativePath, typeTokens, ["Fact]", "Theory]", "Assert.", "Tests"]);
        var shallowFlags = BuildShallowPatternFlags(contract, evaluation, callerPaths, diPaths, bindingPaths, repositoryPaths, testPaths);
        var featureFamily = DetermineFeatureFamily(contract, relativePath);
        var integrationGapKind = DetermineIntegrationGapKind(contract, relativePath, shallowFlags);
        var nextFollowThroughHint = DetermineNextFollowThroughHint(contract, relativePath, integrationGapKind);
        var candidateConsumerSurfaceHints = BuildCandidateConsumerSurfaceHints(contract, relativePath, integrationGapKind);

        return new BehaviorDepthEvidenceRecord
        {
            EvidenceId = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            ToolName = toolName,
            TargetPath = relativePath,
            Profile = FormatProfile(contract.Profile),
            BehaviorDepthTier = FirstNonEmpty(evaluation.BehaviorDepthTier, evaluation.OutputQuality),
            OutputQuality = evaluation.OutputQuality ?? "",
            CompletionStrength = evaluation.CompletionStrength ?? "",
            StrongerBehaviorProofStillMissing = evaluation.StrongerBehaviorProofStillMissing,
            ChangedFiles = [relativePath],
            CallerReferencePaths = callerPaths,
            CalleeReferenceTokens = typeTokens.Concat(memberTokens).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DiOrRegistrationEvidenceFound = diPaths.Count > 0,
            DiOrRegistrationEvidencePaths = diPaths,
            CommandViewModelOrBindingEvidenceFound = bindingPaths.Count > 0,
            CommandViewModelOrBindingEvidencePaths = bindingPaths,
            RepositoryOrServiceLinkageFound = repositoryPaths.Count > 0,
            RepositoryOrServiceLinkagePaths = repositoryPaths,
            TestLinkageFound = testPaths.Count > 0,
            TestLinkagePaths = testPaths,
            ShallowPatternFlags = shallowFlags,
            CompletionRecommendation = BuildCompletionRecommendation(evaluation, shallowFlags),
            FollowUpRecommendation = BuildFollowUpRecommendation(contract, shallowFlags),
            SourceNamespace = contract.NamespaceName ?? "",
            TemplateKind = contract.TemplateKind ?? "",
            RequestedRole = contract.DeclaredRole ?? "",
            RequestedPattern = contract.DeclaredPattern ?? "",
            RequestedProject = contract.DeclaredProject ?? "",
            RequestedImplementationDepth = contract.ImplementationDepth ?? "",
            RequestedFollowThroughRequirements = [.. contract.FollowThroughRequirements],
            ValidationTarget = contract.ValidationTarget ?? "",
            CompanionArtifactHints = [.. contract.CompanionArtifactHints],
            FeatureFamily = featureFamily,
            IntegrationGapKind = integrationGapKind,
            NextFollowThroughHint = nextFollowThroughHint,
            CandidateConsumerSurfaceHints = candidateConsumerSurfaceHints,
            EvidenceSummarySignals = BuildSummarySignals(evaluation, callerPaths, diPaths, bindingPaths, repositoryPaths, testPaths)
        };
    }

    private static List<string> FindCallerReferencePaths(
        string workspaceRoot,
        string relativePath,
        IReadOnlyList<string> typeTokens,
        IReadOnlyList<string> memberTokens)
    {
        var tokens = typeTokens.Concat(memberTokens).Where(token => !string.IsNullOrWhiteSpace(token)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return ScanWorkspaceForEvidencePaths(workspaceRoot, relativePath, path =>
        {
            var text = SafeReadAllText(path);
            return tokens.Any(token => text.Contains(token, StringComparison.Ordinal));
        });
    }

    private static List<string> FindEvidencePaths(
        string workspaceRoot,
        string relativePath,
        IReadOnlyList<string> primaryTokens,
        IReadOnlyList<string> contextTokens)
    {
        return ScanWorkspaceForEvidencePaths(workspaceRoot, relativePath, path =>
        {
            var text = SafeReadAllText(path);
            var hasPrimary = primaryTokens.Count == 0 || primaryTokens.Any(token => !string.IsNullOrWhiteSpace(token) && text.Contains(token, StringComparison.Ordinal));
            var hasContext = contextTokens.Count == 0 || contextTokens.Any(token => !string.IsNullOrWhiteSpace(token) && text.Contains(token, StringComparison.Ordinal));
            return hasPrimary && hasContext;
        });
    }

    private static List<string> FindRepositoryConsumerPaths(
        string workspaceRoot,
        string relativePath,
        CSharpGenerationPromptContractRecord contract,
        IReadOnlyList<string> typeTokens)
    {
        var targetFileName = Path.GetFileName(relativePath);
        var contractFileName = targetFileName switch
        {
            "FileSettingsStore.cs" => "ISettingsStore.cs",
            "SqliteSnapshotRepository.cs" => "ISnapshotRepository.cs",
            _ => ""
        };
        var excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeRelativePath(relativePath)
        };
        if (!string.IsNullOrWhiteSpace(contractFileName))
        {
            var contractDirectory = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "";
            excludedPaths.Add(NormalizeRelativePath(string.IsNullOrWhiteSpace(contractDirectory)
                ? contractFileName
                : $"{contractDirectory}/{contractFileName}"));
        }

        return ScanWorkspaceForEvidencePaths(workspaceRoot, relativePath, path =>
        {
            var candidateRelativePath = NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, path));
            if (excludedPaths.Contains(candidateRelativePath))
                return false;

            var text = SafeReadAllText(path);
            return typeTokens.Any(token => ContainsIdentifier(text, token));
        });
    }

    private static List<string> ScanWorkspaceForEvidencePaths(
        string workspaceRoot,
        string relativePath,
        Func<string, bool> predicate)
    {
        var results = new List<string>();
        foreach (var path in EnumerateRelevantWorkspaceFiles(workspaceRoot))
        {
            var candidateRelativePath = NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, path));
            if (string.Equals(candidateRelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!predicate(path))
                continue;

            results.Add(candidateRelativePath);
            if (results.Count >= 8)
                break;
        }

        return results;
    }

    private static IEnumerable<string> EnumerateRelevantWorkspaceFiles(string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root))
            yield break;

        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(root, file));
            if (relativePath.StartsWith(".ram/", StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains("/.git/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var extension = Path.GetExtension(file);
            if (!string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    private static List<string> BuildShallowPatternFlags(
        CSharpGenerationPromptContractRecord contract,
        CSharpGenerationGuardrailEvaluationRecord evaluation,
        IReadOnlyList<string> callerPaths,
        IReadOnlyList<string> diPaths,
        IReadOnlyList<string> bindingPaths,
        IReadOnlyList<string> repositoryPaths,
        IReadOnlyList<string> testPaths)
    {
        var flags = new List<string>();
        if (evaluation.StrongerBehaviorProofStillMissing)
            flags.Add("stronger_behavior_proof_missing");

        if (contract.Profile is CSharpGenerationProfile.RepositoryImplementation
            && repositoryPaths.Count == 0)
        {
            flags.Add("repository_without_consumer");
        }

        if (contract.Profile is CSharpGenerationProfile.WpfViewmodelImplementation or CSharpGenerationProfile.WpfXamlLayoutImplementation or CSharpGenerationProfile.WpfShellIntegration
            && bindingPaths.Count == 0)
        {
            flags.Add("ui_surface_without_binding_evidence");
        }

        if (contract.Profile is CSharpGenerationProfile.TestRegistryImplementation
            or CSharpGenerationProfile.SnapshotBuilderImplementation
            or CSharpGenerationProfile.FindingsNormalizerImplementation
            or CSharpGenerationProfile.TestHelperImplementation
            or CSharpGenerationProfile.BuilderImplementation
            or CSharpGenerationProfile.NormalizerImplementation)
        {
            if (callerPaths.Count == 0)
                flags.Add("dead_helper_without_caller_path");
            if (testPaths.Count == 0)
                flags.Add("helper_without_test_linkage");
        }

        if (contract.Profile == CSharpGenerationProfile.RuntimeWiring && diPaths.Count == 0)
            flags.Add("runtime_wiring_without_registration_evidence");

        if (contract.FollowThroughRequirements.Contains("registration_required", StringComparer.OrdinalIgnoreCase)
            && diPaths.Count == 0)
        {
            flags.Add("explicit_registration_followthrough_missing");
        }

        if (contract.FollowThroughRequirements.Contains("consumer_required", StringComparer.OrdinalIgnoreCase)
            && repositoryPaths.Count == 0)
        {
            flags.Add("explicit_consumer_followthrough_missing");
        }

        if ((contract.FollowThroughRequirements.Contains("binding_required", StringComparer.OrdinalIgnoreCase)
                || contract.FollowThroughRequirements.Contains("use_site_required", StringComparer.OrdinalIgnoreCase))
            && bindingPaths.Count == 0)
        {
            flags.Add("explicit_binding_followthrough_missing");
        }

        return flags;
    }

    private static string BuildCompletionRecommendation(
        CSharpGenerationGuardrailEvaluationRecord evaluation,
        IReadOnlyList<string> shallowFlags)
    {
        if (string.Equals(evaluation.BehaviorDepthTier, "integrated_behavior_impl", StringComparison.OrdinalIgnoreCase)
            && !evaluation.StrongerBehaviorProofStillMissing
            && shallowFlags.Count == 0)
        {
            return "verified_integrated_behavior_ready";
        }

        if (shallowFlags.Count > 0)
            return "followup_required_for_behavior_depth";

        return "accepted_behavior_without_closure";
    }

    private static string BuildFollowUpRecommendation(
        CSharpGenerationPromptContractRecord contract,
        IReadOnlyList<string> shallowFlags)
    {
        if (shallowFlags.Count == 0)
            return "no_additional_followup_required";

        var gapKind = DetermineIntegrationGapKind(contract, contract.TargetPath ?? "", shallowFlags);
        var nextHint = DetermineNextFollowThroughHint(contract, contract.TargetPath ?? "", gapKind);
        if (!string.IsNullOrWhiteSpace(nextHint))
            return nextHint;

        return contract.Profile switch
        {
            CSharpGenerationProfile.RepositoryImplementation => "wire service into consumer or add missing registration",
            CSharpGenerationProfile.WpfViewmodelImplementation => "wire command or state surface into the consuming UI",
            CSharpGenerationProfile.WpfXamlLayoutImplementation => "bind the layout to the active viewmodel or navigation surface",
            CSharpGenerationProfile.WpfShellIntegration => "wire shell navigation or state into the active shell consumer",
            CSharpGenerationProfile.TestRegistryImplementation or
            CSharpGenerationProfile.SnapshotBuilderImplementation or
            CSharpGenerationProfile.FindingsNormalizerImplementation or
            CSharpGenerationProfile.TestHelperImplementation or
            CSharpGenerationProfile.BuilderImplementation or
            CSharpGenerationProfile.NormalizerImplementation => "add a bounded caller path or meaningful behavior-path test",
            _ => "continue with one bounded consumer or integration step"
        };
    }

    private static List<string> BuildSummarySignals(
        CSharpGenerationGuardrailEvaluationRecord evaluation,
        IReadOnlyList<string> callerPaths,
        IReadOnlyList<string> diPaths,
        IReadOnlyList<string> bindingPaths,
        IReadOnlyList<string> repositoryPaths,
        IReadOnlyList<string> testPaths)
    {
        var signals = new List<string>();
        signals.AddRange(evaluation.PostWriteObservedSignals);
        if (callerPaths.Count > 0)
            signals.Add($"caller_refs={callerPaths.Count}");
        if (diPaths.Count > 0)
            signals.Add($"registration_refs={diPaths.Count}");
        if (bindingPaths.Count > 0)
            signals.Add($"binding_refs={bindingPaths.Count}");
        if (repositoryPaths.Count > 0)
            signals.Add($"repository_refs={repositoryPaths.Count}");
        if (testPaths.Count > 0)
            signals.Add($"test_refs={testPaths.Count}");
        return signals
            .Where(signal => !string.IsNullOrWhiteSpace(signal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string DetermineFeatureFamily(CSharpGenerationPromptContractRecord contract, string relativePath)
    {
        if (IsSettingsSurface(relativePath, contract))
            return "storage_bootstrap";

        if (IsSnapshotSurface(relativePath, contract))
            return "repository_scaffold";

        if (IsShellStateSurface(relativePath, contract))
            return "app_state_wiring";

        if (IsUiSurface(relativePath, contract))
            return "ui_wiring";

        if (IsTestHelperSurface(contract))
            return "findings_pipeline";

        return "";
    }

    private static string DetermineIntegrationGapKind(
        CSharpGenerationPromptContractRecord contract,
        string relativePath,
        IReadOnlyList<string> shallowFlags)
    {
        if (shallowFlags.Count == 0)
            return "integration_satisfied";

        if (shallowFlags.Contains("runtime_wiring_without_registration_evidence", StringComparer.OrdinalIgnoreCase))
            return "missing_service_registration";

        if (shallowFlags.Contains("explicit_registration_followthrough_missing", StringComparer.OrdinalIgnoreCase))
            return "missing_service_registration";

        if (shallowFlags.Contains("repository_without_consumer", StringComparer.OrdinalIgnoreCase))
        {
            return IsSettingsSurface(relativePath, contract)
                ? "missing_repository_store_consumer"
                : "missing_repository_consumer";
        }

        if (shallowFlags.Contains("explicit_consumer_followthrough_missing", StringComparer.OrdinalIgnoreCase))
        {
            return IsSettingsSurface(relativePath, contract)
                ? "missing_repository_store_consumer"
                : "missing_repository_consumer";
        }

        if (shallowFlags.Contains("ui_surface_without_binding_evidence", StringComparer.OrdinalIgnoreCase))
        {
            if (contract.Profile == CSharpGenerationProfile.WpfShellIntegration || IsShellStateSurface(relativePath, contract))
                return "missing_navigation_use_site";

            if (contract.Profile == CSharpGenerationProfile.WpfViewmodelImplementation || IsViewmodelSurface(relativePath))
                return "missing_viewmodel_consumer";

            return "missing_binding_surface";
        }

        if (shallowFlags.Contains("explicit_binding_followthrough_missing", StringComparer.OrdinalIgnoreCase))
        {
            if (contract.Profile == CSharpGenerationProfile.WpfShellIntegration || IsShellStateSurface(relativePath, contract))
                return "missing_navigation_use_site";

            if (contract.Profile == CSharpGenerationProfile.WpfViewmodelImplementation || IsViewmodelSurface(relativePath))
                return "missing_viewmodel_consumer";

            return "missing_binding_surface";
        }

        if (shallowFlags.Contains("dead_helper_without_caller_path", StringComparer.OrdinalIgnoreCase))
            return "missing_helper_caller_path";

        if (shallowFlags.Contains("helper_without_test_linkage", StringComparer.OrdinalIgnoreCase))
            return "missing_behavior_path_test";

        if (shallowFlags.Contains("stronger_behavior_proof_missing", StringComparer.OrdinalIgnoreCase))
        {
            return contract.Profile switch
            {
                CSharpGenerationProfile.RepositoryImplementation => IsSettingsSurface(relativePath, contract)
                    ? "missing_repository_store_consumer"
                    : "missing_repository_consumer",
                CSharpGenerationProfile.RuntimeWiring => "missing_service_registration",
                CSharpGenerationProfile.WpfViewmodelImplementation => "missing_viewmodel_consumer",
                CSharpGenerationProfile.WpfXamlLayoutImplementation => "missing_binding_surface",
                CSharpGenerationProfile.WpfShellIntegration => "missing_navigation_use_site",
                _ => "required_adjacent_integration_not_attempted"
            };
        }

        return "required_adjacent_integration_not_attempted";
    }

    private static string DetermineNextFollowThroughHint(
        CSharpGenerationPromptContractRecord contract,
        string relativePath,
        string integrationGapKind)
    {
        return integrationGapKind switch
        {
            "missing_service_registration" when IsSettingsSurface(relativePath, contract) => "register_settings_store_consumer",
            "missing_service_registration" when IsSnapshotSurface(relativePath, contract) => "register_snapshot_repository_consumer",
            "missing_service_registration" => "add_missing_service_registration",
            "missing_repository_store_consumer" => "wire_settings_store_into_feature_consumer",
            "missing_repository_consumer" when IsSnapshotSurface(relativePath, contract) => "wire_snapshot_repository_into_feature_consumer",
            "missing_repository_consumer" => "wire_repository_into_feature_consumer",
            "missing_viewmodel_consumer" when IsShellViewModelSurface(relativePath) => "bind_shell_viewmodel_to_shell_surface",
            "missing_viewmodel_consumer" => "wire_viewmodel_into_consuming_ui",
            "missing_binding_surface" when IsDashboardSurface(relativePath) => "bind_dashboard_surface_to_viewmodel",
            "missing_binding_surface" when IsSettingsSurface(relativePath, contract) => "bind_settings_surface_to_viewmodel",
            "missing_binding_surface" when IsHistorySurface(relativePath) => "bind_history_surface_to_viewmodel",
            "missing_binding_surface" => "add_missing_ui_binding_surface",
            "missing_navigation_use_site" => "register_navigation_surface_in_shell",
            "missing_helper_caller_path" => "wire_helper_into_check_runner",
            "missing_behavior_path_test" => "add_behavior_path_test_consumer",
            "required_adjacent_integration_not_attempted" => "complete_adjacent_integration_followthrough",
            _ => ""
        };
    }

    private static List<string> BuildCandidateConsumerSurfaceHints(
        CSharpGenerationPromptContractRecord contract,
        string relativePath,
        string integrationGapKind)
    {
        var hints = new List<string>();

        if (IsSettingsSurface(relativePath, contract))
        {
            hints.AddRange(["add_settings_page", "add_navigation_app_state"]);
        }
        else if (IsSnapshotSurface(relativePath, contract))
        {
            hints.AddRange(["add_history_log_view", "wire_dashboard", "check_runner"]);
        }
        else if (IsShellViewModelSurface(relativePath) || IsShellStateSurface(relativePath, contract))
        {
            hints.AddRange(["add_navigation_app_state", "wire_dashboard", "add_settings_page", "add_history_log_view"]);
        }
        else if (IsDashboardSurface(relativePath))
        {
            hints.AddRange(["wire_dashboard", "add_navigation_app_state"]);
        }
        else if (IsHistorySurface(relativePath))
        {
            hints.AddRange(["add_history_log_view", "add_navigation_app_state", "check_runner"]);
        }
        else if (IsSettingsPageSurface(relativePath))
        {
            hints.AddRange(["add_settings_page", "add_navigation_app_state"]);
        }
        else if (IsTestHelperSurface(contract))
        {
            hints.Add("check_runner");
        }

        if (string.Equals(integrationGapKind, "missing_service_registration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(integrationGapKind, "missing_repository_consumer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(integrationGapKind, "missing_repository_store_consumer", StringComparison.OrdinalIgnoreCase))
        {
            hints.Add("repository_scaffold");
        }

        if (string.Equals(integrationGapKind, "missing_navigation_use_site", StringComparison.OrdinalIgnoreCase)
            || string.Equals(integrationGapKind, "missing_viewmodel_consumer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(integrationGapKind, "missing_binding_surface", StringComparison.OrdinalIgnoreCase))
        {
            hints.Add("ui_wiring");
            hints.Add("ui_shell_sections");
        }

        return hints
            .Where(hint => !string.IsNullOrWhiteSpace(hint))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsSettingsSurface(string relativePath, CSharpGenerationPromptContractRecord contract)
    {
        return ContainsAny(relativePath, "settings", "isettingsstore", "filesettingsstore")
            || ContainsAny(contract.NamespaceName, ".Storage", ".Settings")
            || ContainsAny(contract.TargetPath, "settings");
    }

    private static bool IsSnapshotSurface(string relativePath, CSharpGenerationPromptContractRecord contract)
    {
        return ContainsAny(relativePath, "snapshot", "repository")
            || ContainsAny(contract.NamespaceName, ".History", ".Snapshots")
            || ContainsAny(contract.TargetPath, "snapshot", "repository");
    }

    private static bool IsShellStateSurface(string relativePath, CSharpGenerationPromptContractRecord contract)
    {
        return contract.Profile == CSharpGenerationProfile.WpfShellIntegration
            || ContainsAny(relativePath, "appstate", "navigationitem", "shellnavigationregistry")
            || ContainsAny(contract.TargetPath, "appstate", "navigationitem", "shellnavigationregistry");
    }

    private static bool IsShellViewModelSurface(string relativePath)
    {
        return ContainsAny(relativePath, "shellviewmodel");
    }

    private static bool IsUiSurface(string relativePath, CSharpGenerationPromptContractRecord contract)
    {
        return contract.Profile is CSharpGenerationProfile.WpfViewmodelImplementation
            or CSharpGenerationProfile.WpfXamlLayoutImplementation
            or CSharpGenerationProfile.WpfShellIntegration
            || relativePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            || relativePath.EndsWith("ViewModel.cs", StringComparison.OrdinalIgnoreCase)
            || IsDashboardSurface(relativePath)
            || IsHistorySurface(relativePath)
            || IsSettingsPageSurface(relativePath);
    }

    private static bool IsViewmodelSurface(string relativePath)
    {
        return relativePath.EndsWith("ViewModel.cs", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(relativePath, "viewmodel");
    }

    private static bool IsDashboardSurface(string relativePath)
    {
        return ContainsAny(relativePath, "dashboard");
    }

    private static bool IsHistorySurface(string relativePath)
    {
        return ContainsAny(relativePath, "history");
    }

    private static bool IsSettingsPageSurface(string relativePath)
    {
        return ContainsAny(relativePath, "settingspage", "settingsview");
    }

    private static bool IsTestHelperSurface(CSharpGenerationPromptContractRecord contract)
    {
        return contract.Profile is CSharpGenerationProfile.TestRegistryImplementation
            or CSharpGenerationProfile.SnapshotBuilderImplementation
            or CSharpGenerationProfile.FindingsNormalizerImplementation
            or CSharpGenerationProfile.TestHelperImplementation
            or CSharpGenerationProfile.BuilderImplementation
            or CSharpGenerationProfile.NormalizerImplementation;
    }

    private static bool ContainsIdentifier(string content, string identifier)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(identifier))
            return false;

        return Regex.IsMatch(
            content,
            $@"(?<![A-Za-z0-9_]){Regex.Escape(identifier)}(?![A-Za-z0-9_])",
            RegexOptions.CultureInvariant);
    }

    private static string SafeReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return "";
        }
    }

    private static string FormatProfile(CSharpGenerationProfile profile)
    {
        return profile switch
        {
            CSharpGenerationProfile.ContractGeneration => "contract_generation",
            CSharpGenerationProfile.SimpleImplementation => "simple_implementation",
            CSharpGenerationProfile.TestRegistryImplementation => "test_registry_impl",
            CSharpGenerationProfile.SnapshotBuilderImplementation => "snapshot_builder_impl",
            CSharpGenerationProfile.FindingsNormalizerImplementation => "findings_normalizer_impl",
            CSharpGenerationProfile.TestHelperImplementation => "test_helper_impl",
            CSharpGenerationProfile.BuilderImplementation => "builder_impl",
            CSharpGenerationProfile.NormalizerImplementation => "normalizer_impl",
            CSharpGenerationProfile.RepositoryImplementation => "repository_implementation",
            CSharpGenerationProfile.ViewmodelGeneration => "viewmodel_generation",
            CSharpGenerationProfile.WpfXamlStubOnly => "wpf_xaml_stub_only",
            CSharpGenerationProfile.WpfXamlLayoutImplementation => "wpf_xaml_layout_impl",
            CSharpGenerationProfile.WpfViewmodelImplementation => "wpf_viewmodel_impl",
            CSharpGenerationProfile.WpfShellIntegration => "wpf_shell_integration",
            CSharpGenerationProfile.RuntimeWiring => "runtime_wiring",
            _ => "none"
        };
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

    private static string NormalizeRelativePath(string value)
    {
        return (value ?? "").Replace('\\', '/');
    }

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
