using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RAM.Models;

namespace RAM.Services;

public sealed class CSharpGenerationGuardrailService
{
    private static readonly string[] DefaultForbiddenNamespaces =
    [
        "Newtonsoft.Json",
        "CommunityToolkit.Mvvm",
        "ReactiveUI",
        "Prism",
        "Dapper",
        "Microsoft.EntityFrameworkCore"
    ];

    private readonly CSharpGenerationValidationService _cSharpGenerationValidationService = new();
    private readonly CSharpGenerationArgumentResolverService _cSharpGenerationArgumentResolverService = new();
    private readonly WorkspacePreparationQueryService _workspacePreparationQueryService = new();
    private readonly WorkspaceTruthQueryService _workspaceTruthQueryService = new();
    private readonly RamDbService _ramDbService = new();

    public CSharpGenerationPromptContractRecord BuildContract(
        string workspaceRoot,
        ToolRequest request,
        string fullPath)
    {
        var toolName = NormalizeToolName(request.ToolName);
        var relativePath = NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, fullPath));
        var extension = Path.GetExtension(relativePath);
        if (!(string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            return new CSharpGenerationPromptContractRecord
            {
                ToolName = toolName,
                TargetPath = relativePath
            };
        }

        var templateKind = GetOptionalArgument(request, "template");
        var declaredRole = GetOptionalArgument(request, "role");
        var declaredPattern = GetOptionalArgument(request, "pattern");
        var declaredProject = GetOptionalArgument(request, "project");
        var declaredNamespace = GetOptionalArgument(request, "namespace");
        var implementationDepth = ResolveImplementationDepth(request);
        var followThroughRequirements = ResolveFollowThroughRequirements(request);
        var validationTarget = FirstNonEmpty(GetOptionalArgument(request, "validation"), GetOptionalArgument(request, "validation_target"));
        var projectInfo = ResolveProjectInfo(workspaceRoot, fullPath);
        var effectiveProjectName = FirstNonEmpty(declaredProject, projectInfo.ProjectName);
        var namespaceName = FirstNonEmpty(declaredNamespace, ResolveNamespace(relativePath, effectiveProjectName));
        var truthSnapshot = _workspaceTruthQueryService.LoadLatestSnapshot(_ramDbService, workspaceRoot);
        var truthGraph = _workspaceTruthQueryService.LoadLatestProjectGraph(_ramDbService, workspaceRoot);
        var truthProject = _workspaceTruthQueryService.GetProjectByPathOrName(_ramDbService, workspaceRoot, FirstNonEmpty(declaredProject, projectInfo.ProjectPath, projectInfo.ProjectName));
        var retrievalReadinessStatus = _workspacePreparationQueryService.GetRetrievalReadinessStatus(_ramDbService, workspaceRoot);
        var argumentContract = _cSharpGenerationArgumentResolverService.Resolve(
            request,
            relativePath,
            namespaceName,
            effectiveProjectName,
            FirstNonEmpty(truthProject?.RelativePath, projectInfo.ProjectPath),
            retrievalReadinessStatus,
            FirstNonEmpty(truthSnapshot?.SnapshotId, truthGraph?.GraphId));
        declaredRole = FirstNonEmpty(argumentContract.FileRole, declaredRole);
        declaredPattern = FirstNonEmpty(argumentContract.Pattern, declaredPattern);
        declaredProject = FirstNonEmpty(argumentContract.TargetProject, declaredProject);
        declaredNamespace = FirstNonEmpty(argumentContract.NamespaceName, declaredNamespace);
        implementationDepth = FirstNonEmpty(argumentContract.ImplementationDepth, implementationDepth);
        followThroughRequirements = followThroughRequirements.Count == 0
            && !string.IsNullOrWhiteSpace(argumentContract.FollowThroughMode)
            && !string.Equals(argumentContract.FollowThroughMode, "single_file", StringComparison.OrdinalIgnoreCase)
            ? [argumentContract.FollowThroughMode]
            : followThroughRequirements;
        var profile = ResolveProfile(request, toolName, relativePath, declaredRole, declaredPattern, templateKind);
        var fileName = Path.GetFileName(relativePath);
        var intent = ResolveIntent(profile, fileName, declaredPattern, followThroughRequirements);
        var fileRole = ResolveFileRole(profile, fileName, declaredRole, declaredPattern);
        var requiredTypeNames = ResolveRequiredTypeNames(fileName, declaredPattern);
        requiredTypeNames = requiredTypeNames
            .Concat([argumentContract.ClassName])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var requiredMemberNames = ResolveRequiredMemberNames(fileName, declaredPattern);
        var requiredApiTokens = ResolveRequiredApiTokens(fileName, declaredPattern);
        var allowedNamespaces = ResolveAllowedNamespaces(namespaceName, fileName)
            .Concat(argumentContract.RequiredUsings)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var allowedApiOwners = ResolveAllowedApiOwners(requiredTypeNames, requiredMemberNames, fileName, effectiveProjectName);
        var siblingFiles = ResolveSiblingFiles(fullPath);
        var contextHints = ResolveContextHints(workspaceRoot, fullPath, fileName, siblingFiles);
        contextHints.AddRange(BuildArgumentContextHints(templateKind, declaredRole, declaredPattern, implementationDepth, followThroughRequirements, validationTarget));
        var dependencyPrerequisites = ResolveDependencyPrerequisites(request);
        var dependencyStatus = GetOptionalArgument(request, "dependency_ordering_status");
        var dependencySummary = GetOptionalArgument(request, "dependency_ordering_summary");
        var companionArtifactHints = ResolveCompanionArtifactHints(relativePath, fileName, declaredPattern, implementationDepth, followThroughRequirements)
            .Concat(argumentContract.SupportingSurfaces)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var contract = new CSharpGenerationPromptContractRecord
        {
            Applicable = true,
            ArgumentContract = argumentContract,
            ToolName = toolName,
            TargetPath = relativePath,
            TargetFramework = projectInfo.TargetFramework,
            TemplateKind = templateKind,
            NamespaceName = namespaceName,
            FileRole = fileRole,
            DeclaredRole = declaredRole,
            DeclaredPattern = declaredPattern,
            DeclaredProject = effectiveProjectName,
            DeclaredNamespace = declaredNamespace,
            ImplementationDepth = implementationDepth,
            FollowThroughRequirements = followThroughRequirements,
            ValidationTarget = validationTarget,
            CompanionArtifactHints = companionArtifactHints,
            Intent = intent,
            Profile = profile,
            AllowPlaceholders = profile is CSharpGenerationProfile.ContractGeneration or CSharpGenerationProfile.WpfXamlStubOnly,
            AllowAsync = ResolveAllowAsync(declaredPattern, profile),
            BehaviorFirstAcceptance = intent is CSharpGenerationIntent.ImplementBehavior or CSharpGenerationIntent.WireRuntimeIntegration,
            RequiredTypeNames = requiredTypeNames,
            RequiredMemberNames = requiredMemberNames,
            RequiredApiTokens = requiredApiTokens,
            AllowedNamespaces = allowedNamespaces,
            ForbiddenNamespaces = [.. DefaultForbiddenNamespaces.Where(value => !allowedNamespaces.Contains(value, StringComparer.OrdinalIgnoreCase))],
            AllowedApiOwnerTokens = allowedApiOwners,
            ExpectedSiblingFiles = siblingFiles,
            LocalContextHints = contextHints,
            DependencyPrerequisites = dependencyPrerequisites,
            DependencyStatus = dependencyStatus,
            DependencySummary = dependencySummary
        };

        contract.ProfileRequirements = _cSharpGenerationValidationService.BuildProfileRequirements(contract);
        contract.ArgumentContract.CompletionContract = argumentContract.CompletionContract.Count == 0
            ? [.. contract.ProfileRequirements]
            : argumentContract.CompletionContract
                .Concat(contract.ProfileRequirements)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        contract.PromptContractText = BuildPromptContractText(contract);
        contract.Summary = BuildContractSummary(contract);
        return contract;
    }

    public CSharpGenerationGuardrailEvaluationRecord Evaluate(
        string workspaceRoot,
        ToolRequest request,
        string fullPath,
        string content)
    {
        var contract = BuildContract(workspaceRoot, request, fullPath);
        if (!contract.Applicable)
        {
            return new CSharpGenerationGuardrailEvaluationRecord
            {
                Contract = contract,
                Accepted = true,
                DecisionCode = "not_applicable",
                AntiStubStatus = "not_applicable",
                AntiHallucinationStatus = "not_applicable",
                BehaviorStatus = "not_applicable",
                ProfileEnforcement = new CSharpGenerationProfileEnforcementRecord
                {
                    Status = "not_applicable",
                    Summary = "profile_enforcement not_applicable"
                },
                PrimaryRejectionClass = "none",
                RetryStatus = "not_needed",
                Summary = "generation_guardrails not_applicable"
            };
        }

        var antiStubFailures = EvaluateAntiStub(contract, content);
        var antiHallucinationFailures = EvaluateAntiHallucination(contract, content, out var unexpectedNamespaces, out var unexpectedApiOwners);
        var behaviorFailures = EvaluateBehavior(contract, content, out var missingTypes, out var missingMembers, out var missingApiTokens);
        var profileEnforcement = _cSharpGenerationValidationService.Validate(contract, content);

        var antiStubPassed = antiStubFailures.Count == 0;
        var antiHallucinationPassed = antiHallucinationFailures.Count == 0;
        var behaviorPassed = behaviorFailures.Count == 0;
        var profileEnforcementPassed = string.Equals(profileEnforcement.Status, "passed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(profileEnforcement.Status, "not_applicable", StringComparison.OrdinalIgnoreCase);
        var accepted = antiStubPassed && antiHallucinationPassed && behaviorPassed && profileEnforcementPassed;
        var reasons = antiStubFailures
            .Concat(antiHallucinationFailures)
            .Concat(behaviorFailures)
            .Concat(profileEnforcement.FailedRules)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var postWriteAssessment = EvaluatePostWriteQuality(workspaceRoot, fullPath, contract, content, accepted);

        var evaluation = new CSharpGenerationGuardrailEvaluationRecord
        {
            Contract = contract,
            Accepted = accepted,
            DecisionCode = ResolveDecisionCode(antiStubPassed, antiHallucinationPassed, behaviorPassed, profileEnforcementPassed),
            AntiStubStatus = antiStubPassed ? "passed" : "rejected",
            AntiHallucinationStatus = antiHallucinationPassed ? "passed" : "rejected",
            BehaviorStatus = behaviorPassed ? "passed" : "rejected",
            ProfileEnforcement = profileEnforcement,
            PostWriteCheckStatus = postWriteAssessment.Status,
            FamilyAlignmentStatus = postWriteAssessment.FamilyAlignmentStatus,
            IntegrationStatus = postWriteAssessment.IntegrationStatus,
            BehaviorDepthTier = postWriteAssessment.BehaviorDepthTier,
            PrimaryRejectionClass = ResolvePrimaryRejectionClass(antiStubPassed, antiHallucinationPassed, behaviorPassed, profileEnforcementPassed),
            RetrySuggested = !accepted,
            RetryStatus = accepted ? "not_needed" : "not_attempted",
            RejectionReasons = reasons,
            UnexpectedNamespaces = unexpectedNamespaces,
            UnexpectedApiOwners = unexpectedApiOwners,
            MissingRequiredTypes = missingTypes,
            MissingRequiredMembers = missingMembers,
            MissingRequiredApiTokens = missingApiTokens,
            DependencyPrerequisites = [.. contract.DependencyPrerequisites],
            DependencyStatus = contract.DependencyStatus ?? "",
            DependencySummary = contract.DependencySummary ?? "",
            EscalationStatus = accepted ? "not_needed" : "not_attempted",
            EscalationSummary = accepted ? "no escalation needed" : "",
            PostWriteFailedRules = postWriteAssessment.FailedRules,
            PostWriteObservedSignals = postWriteAssessment.ObservedSignals,
            OutputQuality = ResolveOutputQuality(contract, accepted, postWriteAssessment.BehaviorDepthTier),
            CompletionStrength = ResolveCompletionStrength(contract, accepted, postWriteAssessment.BehaviorDepthTier),
            StrongerBehaviorProofStillMissing = ResolveStrongerBehaviorProofStillMissing(contract, accepted, postWriteAssessment.BehaviorDepthTier),
            Summary = BuildEvaluationSummary(contract, accepted, antiStubPassed, antiHallucinationPassed, behaviorPassed, profileEnforcementPassed, postWriteAssessment, reasons)
        };

        return evaluation;
    }

    private static CSharpGenerationProfile ResolveProfile(
        ToolRequest request,
        string toolName,
        string relativePath,
        string declaredRole,
        string declaredPattern,
        string templateKind)
    {
        var overrideProfile = ResolveOverrideProfile(GetOptionalArgument(request, "generation_profile_override"));
        if (overrideProfile != CSharpGenerationProfile.None)
            return overrideProfile;

        var fileName = Path.GetFileName(relativePath);
        if (string.Equals(declaredPattern, "interface", StringComparison.OrdinalIgnoreCase))
            return CSharpGenerationProfile.ContractGeneration;
        if (string.Equals(declaredPattern, "repository", StringComparison.OrdinalIgnoreCase))
            return CSharpGenerationProfile.RepositoryImplementation;
        if (string.Equals(declaredPattern, "viewmodel", StringComparison.OrdinalIgnoreCase))
            return CSharpGenerationProfile.WpfViewmodelImplementation;
        if (string.Equals(declaredPattern, "page", StringComparison.OrdinalIgnoreCase))
            return CSharpGenerationProfile.WpfXamlLayoutImplementation;
        if (string.Equals(declaredPattern, "test_harness", StringComparison.OrdinalIgnoreCase))
        {
            return fileName switch
            {
                "CheckRegistry.cs" => CSharpGenerationProfile.TestRegistryImplementation,
                "SnapshotBuilder.cs" => CSharpGenerationProfile.SnapshotBuilderImplementation,
                "FindingsNormalizer.cs" => CSharpGenerationProfile.FindingsNormalizerImplementation,
                _ => CSharpGenerationProfile.TestHelperImplementation
            };
        }
        if (string.Equals(declaredPattern, "controller", StringComparison.OrdinalIgnoreCase))
            return CSharpGenerationProfile.RuntimeWiring;
        if (string.Equals(declaredPattern, "dto", StringComparison.OrdinalIgnoreCase))
            return CSharpGenerationProfile.ContractGeneration;
        if (string.Equals(declaredPattern, "worker_support", StringComparison.OrdinalIgnoreCase))
            return CSharpGenerationProfile.RuntimeWiring;
        if (string.Equals(declaredPattern, "service", StringComparison.OrdinalIgnoreCase))
            return string.Equals(templateKind, "worker", StringComparison.OrdinalIgnoreCase)
                ? CSharpGenerationProfile.RuntimeWiring
                : CSharpGenerationProfile.SimpleImplementation;
        if (string.Equals(declaredPattern, "model", StringComparison.OrdinalIgnoreCase))
            return string.Equals(declaredRole, "state", StringComparison.OrdinalIgnoreCase)
                ? CSharpGenerationProfile.WpfViewmodelImplementation
                : CSharpGenerationProfile.ContractGeneration;

        if (string.Equals(Path.GetExtension(relativePath), ".xaml", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(fileName, "MainWindow.xaml", StringComparison.OrdinalIgnoreCase))
                return CSharpGenerationProfile.WpfShellIntegration;

            return CSharpGenerationProfile.WpfXamlLayoutImplementation;
        }

        if (fileName.StartsWith("I", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return CSharpGenerationProfile.ContractGeneration;
        }

        if (fileName.Contains("Verifier", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("Validation", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("Tests", StringComparison.OrdinalIgnoreCase))
        {
            return CSharpGenerationProfile.SimpleImplementation;
        }

        if (string.Equals(fileName, "CheckRegistry.cs", StringComparison.OrdinalIgnoreCase))
            return CSharpGenerationProfile.TestRegistryImplementation;

        if (string.Equals(fileName, "SnapshotBuilder.cs", StringComparison.OrdinalIgnoreCase))
            return CSharpGenerationProfile.SnapshotBuilderImplementation;

        if (string.Equals(fileName, "FindingsNormalizer.cs", StringComparison.OrdinalIgnoreCase))
            return CSharpGenerationProfile.FindingsNormalizerImplementation;

        if (fileName.Contains("Repository", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("Store", StringComparison.OrdinalIgnoreCase))
        {
            return fileName.StartsWith("I", StringComparison.OrdinalIgnoreCase)
                ? CSharpGenerationProfile.ContractGeneration
                : CSharpGenerationProfile.RepositoryImplementation;
        }

        if (fileName.Contains("ViewModel", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("AppState", StringComparison.OrdinalIgnoreCase))
        {
            return CSharpGenerationProfile.WpfViewmodelImplementation;
        }

        if (toolName is "register_navigation" or "register_di_service" or "initialize_sqlite_storage_boundary")
            return CSharpGenerationProfile.RuntimeWiring;

        if (relativePath.Contains("/Contracts/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("/Models/", StringComparison.OrdinalIgnoreCase))
        {
            return CSharpGenerationProfile.ContractGeneration;
        }

        return CSharpGenerationProfile.SimpleImplementation;
    }

    private static CSharpGenerationIntent ResolveIntent(
        CSharpGenerationProfile profile,
        string fileName,
        string declaredPattern,
        IReadOnlyList<string> followThroughRequirements)
    {
        if (string.Equals(declaredPattern, "page", StringComparison.OrdinalIgnoreCase))
            return CSharpGenerationIntent.WireRuntimeIntegration;
        if (string.Equals(declaredPattern, "viewmodel", StringComparison.OrdinalIgnoreCase))
            return CSharpGenerationIntent.ImplementBehavior;
        if (string.Equals(declaredPattern, "repository", StringComparison.OrdinalIgnoreCase)
            || string.Equals(declaredPattern, "service", StringComparison.OrdinalIgnoreCase)
            || string.Equals(declaredPattern, "test_harness", StringComparison.OrdinalIgnoreCase))
        {
            return CSharpGenerationIntent.ImplementBehavior;
        }
        if (followThroughRequirements.Contains("binding_required", StringComparer.OrdinalIgnoreCase)
            || followThroughRequirements.Contains("registration_required", StringComparer.OrdinalIgnoreCase)
            || followThroughRequirements.Contains("consumer_required", StringComparer.OrdinalIgnoreCase)
            || followThroughRequirements.Contains("use_site_required", StringComparer.OrdinalIgnoreCase))
        {
            return CSharpGenerationIntent.WireRuntimeIntegration;
        }

        if (fileName.Contains("Verifier", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("Validation", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("Tests", StringComparison.OrdinalIgnoreCase))
        {
            return CSharpGenerationIntent.VerifyBehavior;
        }

        return profile switch
        {
            CSharpGenerationProfile.ContractGeneration => CSharpGenerationIntent.ScaffoldFile,
            CSharpGenerationProfile.WpfXamlStubOnly => CSharpGenerationIntent.ScaffoldFile,
            CSharpGenerationProfile.WpfXamlLayoutImplementation => CSharpGenerationIntent.WireRuntimeIntegration,
            CSharpGenerationProfile.WpfViewmodelImplementation => CSharpGenerationIntent.ImplementBehavior,
            CSharpGenerationProfile.WpfShellIntegration => CSharpGenerationIntent.WireRuntimeIntegration,
            CSharpGenerationProfile.TestRegistryImplementation => CSharpGenerationIntent.ImplementBehavior,
            CSharpGenerationProfile.SnapshotBuilderImplementation => CSharpGenerationIntent.ImplementBehavior,
            CSharpGenerationProfile.FindingsNormalizerImplementation => CSharpGenerationIntent.ImplementBehavior,
            CSharpGenerationProfile.TestHelperImplementation => CSharpGenerationIntent.ImplementBehavior,
            CSharpGenerationProfile.BuilderImplementation => CSharpGenerationIntent.ImplementBehavior,
            CSharpGenerationProfile.NormalizerImplementation => CSharpGenerationIntent.ImplementBehavior,
            CSharpGenerationProfile.RepositoryImplementation => CSharpGenerationIntent.ImplementBehavior,
            CSharpGenerationProfile.RuntimeWiring => CSharpGenerationIntent.WireRuntimeIntegration,
            CSharpGenerationProfile.ViewmodelGeneration => CSharpGenerationIntent.WireRuntimeIntegration,
            CSharpGenerationProfile.SimpleImplementation => CSharpGenerationIntent.ImplementBehavior,
            _ => CSharpGenerationIntent.None
        };
    }

    private static string ResolveFileRole(CSharpGenerationProfile profile, string fileName, string declaredRole, string declaredPattern)
    {
        if (!string.IsNullOrWhiteSpace(declaredRole) || !string.IsNullOrWhiteSpace(declaredPattern))
            return $"declared:{DisplayValue(declaredRole)}/{DisplayValue(declaredPattern)}:{fileName}";

        return profile switch
        {
            CSharpGenerationProfile.ContractGeneration => $"contract:{fileName}",
            CSharpGenerationProfile.TestRegistryImplementation => $"test_registry_impl:{fileName}",
            CSharpGenerationProfile.SnapshotBuilderImplementation => $"snapshot_builder_impl:{fileName}",
            CSharpGenerationProfile.FindingsNormalizerImplementation => $"findings_normalizer_impl:{fileName}",
            CSharpGenerationProfile.TestHelperImplementation => $"test_helper_impl:{fileName}",
            CSharpGenerationProfile.BuilderImplementation => $"builder_impl:{fileName}",
            CSharpGenerationProfile.NormalizerImplementation => $"normalizer_impl:{fileName}",
            CSharpGenerationProfile.RepositoryImplementation => $"repository_implementation:{fileName}",
            CSharpGenerationProfile.ViewmodelGeneration => $"viewmodel_generation:{fileName}",
            CSharpGenerationProfile.WpfXamlLayoutImplementation => $"wpf_xaml_layout_impl:{fileName}",
            CSharpGenerationProfile.WpfViewmodelImplementation => $"wpf_viewmodel_impl:{fileName}",
            CSharpGenerationProfile.WpfShellIntegration => $"wpf_shell_integration:{fileName}",
            CSharpGenerationProfile.RuntimeWiring => $"runtime_wiring:{fileName}",
            CSharpGenerationProfile.WpfXamlStubOnly => $"xaml_stub:{fileName}",
            CSharpGenerationProfile.SimpleImplementation => $"simple_implementation:{fileName}",
            _ => $"unknown:{fileName}"
        };
    }

    private static List<string> ResolveRequiredTypeNames(string fileName, string declaredPattern)
    {
        if (string.Equals(declaredPattern, "service", StringComparison.OrdinalIgnoreCase))
            return InferTypeNamesFromFileName(fileName);

        return fileName.ToLowerInvariant() switch
        {
            "navigatioitem.cs" => ["NavigationItem"],
            "navigationitem.cs" => ["NavigationItem"],
            "shellnavigationregistry.cs" => ["ShellNavigationRegistry", "NavigationItem"],
            "appstate.cs" => ["AppState", "NavigationItem"],
            "shellviewmodel.cs" => ["ShellViewModel", "AppState"],
            "mainwindow.xaml" => [],
            "dashboardpage.xaml" => [],
            "findingspage.xaml" => [],
            "historypage.xaml" => [],
            "settingspage.xaml" => [],
            "isettingsstore.cs" => ["ISettingsStore"],
            "filesettingsstore.cs" => ["FileSettingsStore", "ISettingsStore"],
            "isnapshotrepository.cs" => ["ISnapshotRepository"],
            "sqlitesnapshotrepository.cs" => ["SqliteSnapshotRepository", "ISnapshotRepository"],
            "checkdefinition.cs" => ["CheckDefinition"],
            "findingrecord.cs" => ["FindingRecord"],
            "checkregistry.cs" => ["CheckRegistry"],
            "snapshotbuilder.cs" => ["SnapshotBuilder"],
            "findingsnormalizer.cs" => ["FindingsNormalizer"],
            _ => InferTypeNamesFromFileName(fileName)
        };
    }

    private static List<string> ResolveRequiredMemberNames(string fileName, string declaredPattern)
    {
        if (string.Equals(declaredPattern, "service", StringComparison.OrdinalIgnoreCase))
            return ["Execute"];
        if (string.Equals(declaredPattern, "controller", StringComparison.OrdinalIgnoreCase))
            return ["Get", "Create"];
        if (string.Equals(declaredPattern, "worker_support", StringComparison.OrdinalIgnoreCase))
            return ["ExecuteAsync"];

        return fileName.ToLowerInvariant() switch
        {
            "navigationitem.cs" => ["Title", "RouteKey"],
            "shellnavigationregistry.cs" => ["CreateDefault"],
            "appstate.cs" => ["CurrentRoute", "NavigationItems", "StatusMessage", "LastBuildResult"],
            "shellviewmodel.cs" => ["State", "Navigate", "WindowTitle", "CurrentStatusSummary", "DashboardHighlights", "RecentFindings", "HistoryEntries", "SettingsItems"],
            "mainwindow.xaml" => ["WindowTitle", "CurrentStatusSummary", "NavigationItems"],
            "dashboardpage.xaml" => ["CurrentStatusSummary", "DashboardHighlights"],
            "findingspage.xaml" => ["RecentFindings"],
            "historypage.xaml" => ["HistoryEntries"],
            "settingspage.xaml" => ["SettingsItems"],
            "isettingsstore.cs" => ["Load", "Save"],
            "filesettingsstore.cs" => ["Load", "Save"],
            "isnapshotrepository.cs" => ["LoadSnapshotJson", "SaveSnapshotJson"],
            "sqlitesnapshotrepository.cs" => ["LoadSnapshotJson", "SaveSnapshotJson"],
            "checkdefinition.cs" => ["CheckId", "DisplayName", "Severity"],
            "findingrecord.cs" => ["FindingId", "Title", "Severity", "IsResolved"],
            "checkregistry.cs" => ["CreateDefaultChecks", "Contains", "FindByKey"],
            "snapshotbuilder.cs" => ["BuildDefaultSnapshotJson", "BuildDefaultSnapshot", "BuildSnapshotJson"],
            "findingsnormalizer.cs" => ["NormalizeSeverity", "NormalizeStatus", "NormalizeFindings"],
            _ => []
        };
    }

    private static List<string> ResolveRequiredApiTokens(string fileName, string declaredPattern)
    {
        if (string.Equals(declaredPattern, "service", StringComparison.OrdinalIgnoreCase))
            return ["ArgumentException.ThrowIfNullOrWhiteSpace("];
        if (string.Equals(declaredPattern, "controller", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "[ApiController]",
                "[Route(\"api/[controller]\")]",
                "[HttpGet]",
                "[HttpPost]"
            ];
        }
        if (string.Equals(declaredPattern, "worker_support", StringComparison.OrdinalIgnoreCase))
            return ["BackgroundService", "CancellationToken"];

        return fileName.ToLowerInvariant() switch
        {
            "filesettingsstore.cs" or "sqlitesnapshotrepository.cs" =>
            [
                "File.Exists(",
                "File.ReadAllText(",
                "File.WriteAllText(",
                "Path.Combine("
            ],
            "appstate.cs" =>
            [
                "ShellNavigationRegistry.CreateDefault("
            ],
            "shellviewmodel.cs" =>
            [
                "string.IsNullOrWhiteSpace("
            ],
            "mainwindow.xaml" =>
            [
                "ItemsSource=\"{Binding State.NavigationItems}\"",
                "Source=\"Views/DashboardPage.xaml\"",
                "Source=\"Views/FindingsPage.xaml\"",
                "Source=\"Views/HistoryPage.xaml\"",
                "Source=\"Views/SettingsPage.xaml\""
            ],
            "dashboardpage.xaml" =>
            [
                "Text=\"{Binding CurrentStatusSummary}\"",
                "ItemsSource=\"{Binding DashboardHighlights}\""
            ],
            "findingspage.xaml" =>
            [
                "ItemsSource=\"{Binding RecentFindings}\""
            ],
            "historypage.xaml" =>
            [
                "ItemsSource=\"{Binding HistoryEntries}\""
            ],
            "settingspage.xaml" =>
            [
                "ItemsSource=\"{Binding SettingsItems}\""
            ],
            "findingsnormalizer.cs" =>
            [
                "string.IsNullOrWhiteSpace(",
                "ToLowerInvariant("
            ],
            "snapshotbuilder.cs" =>
            [
                "JsonSerializer.Serialize(",
                "BuildDefaultSnapshot("
            ],
            _ => []
        };
    }

    private static List<string> ResolveAllowedNamespaces(string namespaceName, string fileName)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            namespaceName
        };

        switch (fileName.ToLowerInvariant())
        {
            case "shellnavigationregistry.cs":
            case "appstate.cs":
            case "shellviewmodel.cs":
            case "checkregistry.cs":
            case "snapshotbuilder.cs":
            case "findingsnormalizer.cs":
                values.Add("System.Collections.Generic");
                break;
            case "filesettingsstore.cs":
            case "sqlitesnapshotrepository.cs":
                values.Add("System.IO");
                break;
        }

        if (string.Equals(fileName, "SnapshotBuilder.cs", StringComparison.OrdinalIgnoreCase))
            values.Add("System.Text.Json");

        return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ResolveAllowedApiOwners(
        IReadOnlyList<string> requiredTypeNames,
        IReadOnlyList<string> requiredMemberNames,
        string fileName,
        string projectName)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "File",
            "Path",
            "Directory",
            "AppContext",
            "Environment",
            "ArgumentException",
            "ArgumentNullException",
            "Assert",
            "Task",
            "TimeSpan",
            "StringComparison",
            "StringComparer"
        };

        foreach (var typeName in requiredTypeNames)
            values.Add(typeName);
        foreach (var memberName in requiredMemberNames)
            values.Add(memberName);
        if (!string.IsNullOrWhiteSpace(projectName))
            values.Add(projectName);

        if (fileName.Contains("ViewModel", StringComparison.OrdinalIgnoreCase))
            values.Add("AppState");
        if (fileName.Contains("AppState", StringComparison.OrdinalIgnoreCase))
            values.Add("ShellNavigationRegistry");
        if (string.Equals(fileName, "SnapshotBuilder.cs", StringComparison.OrdinalIgnoreCase))
            values.Add("JsonSerializer");

        return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ResolveSiblingFiles(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return [];

        return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(path), ".xaml", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList()!;
    }

    private static List<string> ResolveContextHints(string workspaceRoot, string fullPath, string fileName, IReadOnlyList<string> siblingFiles)
    {
        var hints = new List<string>();
        if (siblingFiles.Count > 0)
            hints.Add($"siblings={string.Join(", ", siblingFiles)}");

        var directory = Path.GetDirectoryName(fullPath) ?? "";
        var adjacentInterface = fileName switch
        {
            "FileSettingsStore.cs" => Path.Combine(directory, "ISettingsStore.cs"),
            "SqliteSnapshotRepository.cs" => Path.Combine(directory, "ISnapshotRepository.cs"),
            "ShellViewModel.cs" => Path.Combine(directory, "AppState.cs"),
            _ => ""
        };

        if (!string.IsNullOrWhiteSpace(adjacentInterface) && File.Exists(adjacentInterface))
        {
            var relative = NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, adjacentInterface));
            var members = ExtractMemberNames(File.ReadAllText(adjacentInterface));
            hints.Add($"adjacent={relative} members={string.Join(", ", members.Take(6))}");
        }

        return hints;
    }

    private static ProjectContext ResolveProjectInfo(string workspaceRoot, string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath) ?? workspaceRoot;
        while (!string.IsNullOrWhiteSpace(directory) && directory.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory))
            {
                var missingParent = Path.GetDirectoryName(directory);
                if (string.IsNullOrWhiteSpace(missingParent) || string.Equals(missingParent, directory, StringComparison.OrdinalIgnoreCase))
                    break;

                directory = missingParent;
                continue;
            }

            var csproj = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(csproj))
            {
                var projectName = Path.GetFileNameWithoutExtension(csproj);
                var targetFramework = TryReadTargetFramework(csproj);
                return new ProjectContext(projectName, targetFramework, NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, csproj)));
            }

            var parent = Path.GetDirectoryName(directory);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                break;
            directory = parent;
        }

        return new ProjectContext("", "", "");
    }

    private static string TryReadTargetFramework(string projectPath)
    {
        try
        {
            var document = XDocument.Load(projectPath);
            var targetFramework = document.Root?
                .Elements()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "PropertyGroup", StringComparison.OrdinalIgnoreCase))?
                .Elements()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "TargetFramework", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim();
            if (!string.IsNullOrWhiteSpace(targetFramework))
                return targetFramework;

            var targetFrameworks = document.Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "TargetFrameworks", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return targetFrameworks ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string ResolveNamespace(string relativePath, string projectName)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var resolvedProjectName = string.IsNullOrWhiteSpace(projectName)
            ? InferProjectNameFromPath(normalized)
            : projectName;
        var directory = Path.GetDirectoryName(normalized.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        var segments = directory
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => !string.Equals(segment, "src", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(segment, resolvedProjectName, StringComparison.OrdinalIgnoreCase))
            .Select(SanitizeIdentifier)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();
        return segments.Count == 0
            ? resolvedProjectName
            : string.Join(".", new[] { resolvedProjectName }.Where(value => !string.IsNullOrWhiteSpace(value)).Concat(segments));
    }

    private static List<string> EvaluateAntiStub(CSharpGenerationPromptContractRecord contract, string content)
    {
        if (contract.AllowPlaceholders)
            return [];

        var failures = new List<string>();
        if (ContainsAny(content, "NotImplementedException", "TODO", "stub", "placeholder", "implement later"))
            failures.Add("stub_output_detected");

        if (Regex.IsMatch(content, @"\b(public|internal|protected)\s+[^\n;]+\([^;\n]*\)\s*\{\s*\}", RegexOptions.CultureInvariant))
            failures.Add("empty_method_detected");

        var targetIsXaml = string.Equals(Path.GetExtension(contract.TargetPath), ".xaml", StringComparison.OrdinalIgnoreCase);

        if (contract.Intent is CSharpGenerationIntent.ImplementBehavior or CSharpGenerationIntent.WireRuntimeIntegration
            && Regex.IsMatch(content, @"\breturn\s+(?:""[^""]*""|true|false|null|0)\s*;", RegexOptions.CultureInvariant)
            && contract.RequiredApiTokens.Count > 0
            && contract.RequiredApiTokens.All(token => content.IndexOf(token, StringComparison.Ordinal) < 0))
        {
            failures.Add("constant_return_cheat_detected");
        }

        if (contract.Intent is CSharpGenerationIntent.ImplementBehavior or CSharpGenerationIntent.WireRuntimeIntegration
            && !targetIsXaml
            && !Regex.IsMatch(content, @"\b(class|record|struct)\b", RegexOptions.CultureInvariant))
        {
            failures.Add("implementation_type_missing");
        }

        return failures.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> EvaluateAntiHallucination(
        CSharpGenerationPromptContractRecord contract,
        string content,
        out List<string> unexpectedNamespaces,
        out List<string> unexpectedApiOwners)
    {
        unexpectedNamespaces = ExtractUsingNamespaces(content)
            .Where(current =>
                !current.StartsWith("System", StringComparison.OrdinalIgnoreCase)
                && !contract.AllowedNamespaces.Contains(current, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingUnexpectedNamespaces = unexpectedNamespaces.ToHashSet(StringComparer.OrdinalIgnoreCase);
        unexpectedNamespaces.AddRange(contract.ForbiddenNamespaces
            .Where(value => content.Contains(value, StringComparison.OrdinalIgnoreCase)
                && !existingUnexpectedNamespaces.Contains(value)));

        var declaredPropertyNames = ExtractDeclaredPropertyNames(content);
        unexpectedApiOwners = ExtractQualifiedOwners(content)
            .Where(owner => !declaredPropertyNames.Contains(owner, StringComparer.OrdinalIgnoreCase))
            .Where(owner => !contract.AllowedApiOwnerTokens.Contains(owner, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var failures = new List<string>();
        if (unexpectedNamespaces.Count > 0)
            failures.Add("unexpected_namespace_usage");
        if (unexpectedApiOwners.Count > 0)
            failures.Add("unexpected_api_owner_usage");
        if (!contract.AllowAsync && ContainsAny(content, "async ", "await ", "Task<", "ValueTask", "CancellationToken"))
            failures.Add("unexpected_async_usage");

        return failures.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> EvaluateBehavior(
        CSharpGenerationPromptContractRecord contract,
        string content,
        out List<string> missingTypes,
        out List<string> missingMembers,
        out List<string> missingApiTokens)
    {
        missingTypes = contract.RequiredTypeNames
            .Where(typeName => !ContainsIdentifier(content, typeName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        missingMembers = contract.RequiredMemberNames
            .Where(memberName => !ContainsIdentifier(content, memberName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        missingApiTokens = contract.RequiredApiTokens
            .Where(token => content.IndexOf(token, StringComparison.Ordinal) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var failures = new List<string>();
        var requireStructuralMembers = contract.Intent == CSharpGenerationIntent.ScaffoldFile
            && contract.Profile == CSharpGenerationProfile.ContractGeneration;

        if (contract.BehaviorFirstAcceptance || requireStructuralMembers)
        {
            if (missingTypes.Count > 0)
                failures.Add("missing_required_types");
            if (missingMembers.Count > 0)
                failures.Add("missing_required_members");
        }

        if (contract.BehaviorFirstAcceptance && missingApiTokens.Count > 0)
            failures.Add("missing_required_behavior_apis");
        return failures;
    }

    private static PostWriteQualityAssessment EvaluatePostWriteQuality(
        string workspaceRoot,
        string fullPath,
        CSharpGenerationPromptContractRecord contract,
        string content,
        bool accepted)
    {
        if (!accepted)
        {
            return new PostWriteQualityAssessment
            {
                Status = "rejected",
                FamilyAlignmentStatus = "not_applicable",
                IntegrationStatus = "not_applicable",
                BehaviorDepthTier = "rejected"
            };
        }

        if (contract.Profile == CSharpGenerationProfile.WpfXamlStubOnly)
        {
            return new PostWriteQualityAssessment
            {
                Status = "not_applicable",
                FamilyAlignmentStatus = "not_applicable",
                IntegrationStatus = "not_applicable",
                BehaviorDepthTier = "accepted_write_only"
            };
        }

        if (contract.Profile is CSharpGenerationProfile.ContractGeneration or CSharpGenerationProfile.RuntimeWiring)
        {
            return new PostWriteQualityAssessment
            {
                Status = "not_applicable",
                FamilyAlignmentStatus = "not_applicable",
                IntegrationStatus = "not_applicable",
                BehaviorDepthTier = "accepted_structural_impl"
            };
        }

        var observedSignals = new List<string>();
        var failedRules = new List<string>();
        var familyAlignmentPassed = EvaluateFamilyAlignment(contract, content, failedRules, observedSignals);
        var integrationStatus = familyAlignmentPassed
            ? EvaluateIntegration(workspaceRoot, fullPath, contract, content, observedSignals)
            : "not_attempted";
        var behaviorDepthTier = integrationStatus switch
        {
            "passed" => "integrated_behavior_impl",
            _ when familyAlignmentPassed => "family_aligned_impl",
            _ => "accepted_behavior_impl"
        };

        return new PostWriteQualityAssessment
        {
            Status = familyAlignmentPassed ? "passed" : "rejected",
            FamilyAlignmentStatus = familyAlignmentPassed ? "passed" : "rejected",
            IntegrationStatus = integrationStatus,
            BehaviorDepthTier = behaviorDepthTier,
            FailedRules = failedRules,
            ObservedSignals = observedSignals
        };
    }

    private static bool EvaluateFamilyAlignment(
        CSharpGenerationPromptContractRecord contract,
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        var fileName = Path.GetFileName(contract.TargetPath);

        switch (contract.Profile)
        {
            case CSharpGenerationProfile.TestRegistryImplementation:
                if (ContainsAny(content, "StringComparison.OrdinalIgnoreCase"))
                    observedSignals.Add("family_alignment=test_registry_case_insensitive_lookup");
                else
                    failedRules.Add("family_alignment:test_registry_case_insensitive_lookup_missing");

                if (ContainsAny(content, "Contains(", "FindByKey(") && Regex.IsMatch(content, @"Contains\s*\([^)]*\)\s*\{(?<body>.*?)FindByKey\(", RegexOptions.CultureInvariant | RegexOptions.Singleline))
                    observedSignals.Add("family_alignment=test_registry_lookup_flow");
                else
                    failedRules.Add("family_alignment:test_registry_lookup_flow_missing");

                if (Regex.Matches(content, "\"(?:defender|firewall|updates)\"", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase).Count >= 3)
                    observedSignals.Add("family_alignment=test_registry_seeded_defaults");
                else
                    failedRules.Add("family_alignment:test_registry_seeded_defaults_missing");
                break;

            case CSharpGenerationProfile.SnapshotBuilderImplementation:
                if (ContainsAny(content, "BuildSnapshotJson(BuildDefaultSnapshot())"))
                    observedSignals.Add("family_alignment=snapshot_builder_default_json_flow");
                else
                    failedRules.Add("family_alignment:snapshot_builder_default_json_flow_missing");

                if (ContainsAny(content, "JsonSerializer.Serialize("))
                    observedSignals.Add("family_alignment=snapshot_builder_serialization");
                else
                    failedRules.Add("family_alignment:snapshot_builder_serialization_missing");

                if (ContainsAny(content, "\"checks\"", "\"capturedBy\"", "\"machine\""))
                    observedSignals.Add("family_alignment=snapshot_builder_payload_shape");
                else
                    failedRules.Add("family_alignment:snapshot_builder_payload_shape_missing");
                break;

            case CSharpGenerationProfile.FindingsNormalizerImplementation:
                if (ContainsAny(content, "NormalizeSeverity(", "NormalizeStatus("))
                    observedSignals.Add("family_alignment=findings_normalizer_method_reuse");
                else
                    failedRules.Add("family_alignment:findings_normalizer_method_reuse_missing");

                if (ContainsAny(content, "foreach", "Select("))
                    observedSignals.Add("family_alignment=findings_normalizer_collection_iteration");
                else
                    failedRules.Add("family_alignment:findings_normalizer_collection_iteration_missing");

                if (ContainsAny(content, "Trim(", "Trim()", "ToLowerInvariant(", "switch"))
                    observedSignals.Add("family_alignment=findings_normalizer_string_transform");
                else
                    failedRules.Add("family_alignment:findings_normalizer_string_transform_missing");
                break;

            case CSharpGenerationProfile.RepositoryImplementation:
                if (ContainsAny(content, "Path.Combine(", "AppContext.BaseDirectory"))
                    observedSignals.Add("family_alignment=repository_path_resolution");
                else
                    failedRules.Add("family_alignment:repository_path_resolution_missing");

                if (ContainsAny(content, "File.ReadAllText(", "File.WriteAllText("))
                    observedSignals.Add("family_alignment=repository_read_write_behavior");
                else
                    failedRules.Add("family_alignment:repository_read_write_behavior_missing");

                if (Regex.IsMatch(content, @"\bclass\s+[A-Za-z_][A-Za-z0-9_]*\s*:\s*[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant))
                    observedSignals.Add("family_alignment=repository_contract_binding");
                else
                    failedRules.Add("family_alignment:repository_contract_binding_missing");
                break;

            case CSharpGenerationProfile.WpfViewmodelImplementation:
                if (ContainsAny(content, "public AppState State", "AppState State"))
                    observedSignals.Add("family_alignment=viewmodel_state_surface");
                else if (string.Equals(fileName, "ShellViewModel.cs", StringComparison.OrdinalIgnoreCase))
                    failedRules.Add("family_alignment:viewmodel_state_surface_missing");

                if (!string.Equals(fileName, "ShellViewModel.cs", StringComparison.OrdinalIgnoreCase)
                    || ContainsAny(content, "State.CurrentRoute ="))
                {
                    observedSignals.Add("family_alignment=viewmodel_navigation_mutation");
                }
                else
                {
                    failedRules.Add("family_alignment:viewmodel_navigation_mutation_missing");
                }

                if (ContainsAny(content, "DashboardHighlights", "RecentFindings", "HistoryEntries", "SettingsItems"))
                    observedSignals.Add("family_alignment=viewmodel_binding_surface");
                else
                    failedRules.Add("family_alignment:viewmodel_binding_surface_missing");
                break;

            case CSharpGenerationProfile.WpfXamlLayoutImplementation:
                if (Regex.Matches(content, @"x:Name=""[^""]+""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase).Count > 0)
                    observedSignals.Add("family_alignment=xaml_named_regions");
                else
                    failedRules.Add("family_alignment:xaml_named_regions_missing");

                if (Regex.Matches(content, @"\{Binding[^}]+\}", RegexOptions.CultureInvariant).Count >= 2)
                    observedSignals.Add("family_alignment=xaml_binding_surface");
                else
                    failedRules.Add("family_alignment:xaml_binding_surface_missing");

                if (string.Equals(fileName, "DashboardPage.xaml", StringComparison.OrdinalIgnoreCase)
                    && ContainsAny(content, "DashboardHighlights", "CurrentStatusSummary"))
                {
                    observedSignals.Add("family_alignment=dashboard_surface");
                }
                else if (string.Equals(fileName, "FindingsPage.xaml", StringComparison.OrdinalIgnoreCase)
                    && ContainsAny(content, "RecentFindings", "<GridView"))
                {
                    observedSignals.Add("family_alignment=findings_surface");
                }
                else if (string.Equals(fileName, "HistoryPage.xaml", StringComparison.OrdinalIgnoreCase)
                    && ContainsAny(content, "HistoryEntries"))
                {
                    observedSignals.Add("family_alignment=history_surface");
                }
                else if (string.Equals(fileName, "SettingsPage.xaml", StringComparison.OrdinalIgnoreCase)
                    && ContainsAny(content, "SettingsItems"))
                {
                    observedSignals.Add("family_alignment=settings_surface");
                }
                else
                {
                    failedRules.Add("family_alignment:xaml_family_surface_missing");
                }
                break;

            case CSharpGenerationProfile.WpfShellIntegration:
                if (ContainsAny(content, "<Window.DataContext>", "state:ShellViewModel"))
                    observedSignals.Add("family_alignment=shell_datacontext");
                else
                    failedRules.Add("family_alignment:shell_datacontext_missing");

                if (ContainsAny(content, "ItemsSource=\"{Binding State.NavigationItems}\"", "ItemsSource=\"{Binding NavigationItems}\""))
                    observedSignals.Add("family_alignment=shell_navigation_binding");
                else
                    failedRules.Add("family_alignment:shell_navigation_binding_missing");

                if (Regex.Matches(content, @"<Frame\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase).Count >= 4)
                    observedSignals.Add("family_alignment=shell_routed_sections");
                else
                    failedRules.Add("family_alignment:shell_routed_sections_missing");
                break;
        }

        return failedRules.Count == 0;
    }

    private static string EvaluateIntegration(
        string workspaceRoot,
        string fullPath,
        CSharpGenerationPromptContractRecord contract,
        string content,
        ICollection<string> observedSignals)
    {
        try
        {
            var directory = Path.GetDirectoryName(fullPath) ?? workspaceRoot;
            var fileName = Path.GetFileName(fullPath);
            switch (contract.Profile)
            {
                case CSharpGenerationProfile.RepositoryImplementation:
                {
                    var interfacePath = fileName switch
                    {
                        "FileSettingsStore.cs" => Path.Combine(directory, "ISettingsStore.cs"),
                        "SqliteSnapshotRepository.cs" => Path.Combine(directory, "ISnapshotRepository.cs"),
                        _ => ""
                    };

                    if (string.IsNullOrWhiteSpace(interfacePath) || !File.Exists(interfacePath))
                        return "missing_adjacent_surface";

                    observedSignals.Add($"integration_contract={NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, interfacePath))}");
                    var interfaceText = File.ReadAllText(interfacePath);
                    if (!contract.RequiredMemberNames.All(member => ContainsIdentifier(interfaceText, member) && ContainsIdentifier(content, member)))
                        return "missing_adjacent_surface";

                    var consumerPaths = FindWorkspaceIntegrationReferencePaths(
                        workspaceRoot,
                        [fullPath, interfacePath],
                        contract.RequiredTypeNames);
                    if (consumerPaths.Count == 0)
                        return "missing_consumer_surface";

                    observedSignals.Add($"integration_consumers={string.Join(",", consumerPaths)}");
                    return "passed";
                }

                case CSharpGenerationProfile.WpfViewmodelImplementation:
                    if (!string.Equals(fileName, "ShellViewModel.cs", StringComparison.OrdinalIgnoreCase))
                        return "not_applicable";

                    var appStatePath = Path.Combine(directory, "AppState.cs");
                    if (!File.Exists(appStatePath))
                        return "missing_adjacent_surface";

                    observedSignals.Add($"integration_state={NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, appStatePath))}");
                    var appStateText = File.ReadAllText(appStatePath);
                    return ContainsAny(appStateText, "NavigationItems", "CurrentRoute", "StatusMessage", "LastBuildResult")
                        && ContainsAny(content, "AppState", "State.CurrentRoute")
                        ? "passed"
                        : "missing_adjacent_surface";

                case CSharpGenerationProfile.WpfXamlLayoutImplementation:
                {
                    var shellViewModelPath = Path.Combine(Path.GetDirectoryName(directory) ?? workspaceRoot, "State", "ShellViewModel.cs");
                    if (!File.Exists(shellViewModelPath))
                        return "missing_adjacent_surface";

                    observedSignals.Add($"integration_viewmodel={NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, shellViewModelPath))}");
                    var shellViewModelText = File.ReadAllText(shellViewModelPath);
                    return contract.RequiredMemberNames.Count == 0
                        || contract.RequiredMemberNames.All(member => ContainsIdentifier(shellViewModelText, member))
                        ? "passed"
                        : "missing_adjacent_surface";
                }

                case CSharpGenerationProfile.WpfShellIntegration:
                {
                    var projectDirectory = directory;
                    var shellViewModelPath = Path.Combine(projectDirectory, "State", "ShellViewModel.cs");
                    var requiredPages = new[]
                    {
                        Path.Combine(projectDirectory, "Views", "DashboardPage.xaml"),
                        Path.Combine(projectDirectory, "Views", "FindingsPage.xaml"),
                        Path.Combine(projectDirectory, "Views", "HistoryPage.xaml"),
                        Path.Combine(projectDirectory, "Views", "SettingsPage.xaml")
                    };
                    if (!File.Exists(shellViewModelPath) || requiredPages.Any(path => !File.Exists(path)))
                        return "missing_adjacent_surface";

                    observedSignals.Add($"integration_viewmodel={NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, shellViewModelPath))}");
                    observedSignals.Add($"integration_pages={string.Join(",", requiredPages.Select(path => NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, path))))}");
                    return "passed";
                }

                default:
                    return "not_applicable";
            }
        }
        catch
        {
            return "not_applicable";
        }
    }

    private static List<string> FindWorkspaceIntegrationReferencePaths(
        string workspaceRoot,
        IReadOnlyList<string> excludedPaths,
        IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
            return [];

        var excludedFullPaths = excludedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();
        foreach (var file in EnumerateRelevantWorkspaceFiles(workspaceRoot))
        {
            var fullPath = Path.GetFullPath(file);
            if (excludedFullPaths.Contains(fullPath))
                continue;

            var text = SafeReadAllText(fullPath);
            if (!tokens.Any(token => ContainsIdentifier(text, token)))
                continue;

            results.Add(NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, fullPath)));
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

    private static string BuildPromptContractText(CSharpGenerationPromptContractRecord contract)
    {
        var arguments = contract.ArgumentContract ?? new CSharpGenerationArgumentContractRecord();
        var lines = new List<string>
        {
            "C# generation contract:",
            $"intent={FormatIntent(contract.Intent)}",
            $"profile={FormatProfile(contract.Profile)}",
            $"target_file={DisplayValue(contract.TargetPath)}",
            $"target_framework={DisplayValue(contract.TargetFramework)}",
            $"template={DisplayValue(contract.TemplateKind)}",
            $"namespace={DisplayValue(contract.NamespaceName)}",
            $"declared_namespace={DisplayValue(contract.DeclaredNamespace)}",
            $"file_role={DisplayValue(contract.FileRole)}",
            $"declared_role={DisplayValue(contract.DeclaredRole)}",
            $"declared_pattern={DisplayValue(contract.DeclaredPattern)}",
            $"declared_project={DisplayValue(contract.DeclaredProject)}",
            $"implementation_depth={DisplayValue(contract.ImplementationDepth)}",
            $"followthrough={DisplayValue(string.Join(", ", contract.FollowThroughRequirements))}",
            $"followthrough_mode={DisplayValue(arguments.FollowThroughMode)}",
            $"class_name={DisplayValue(arguments.ClassName)}",
            $"base_types={DisplayValue(string.Join(", ", arguments.BaseTypes))}",
            $"interfaces={DisplayValue(string.Join(", ", arguments.Interfaces))}",
            $"constructor_dependencies={DisplayValue(string.Join(", ", arguments.ConstructorDependencies))}",
            $"required_usings={DisplayValue(string.Join(", ", arguments.RequiredUsings))}",
            $"supporting_surfaces={DisplayValue(string.Join(", ", arguments.SupportingSurfaces))}",
            $"completion_contract={DisplayValue(string.Join(" | ", arguments.CompletionContract))}",
            $"target_project_path={DisplayValue(arguments.TargetProjectPath)}",
            $"workspace_truth_fingerprint={DisplayValue(arguments.WorkspaceTruthFingerprint)}",
            $"retrieval_readiness={DisplayValue(arguments.RetrievalReadinessStatus)}",
            $"validation_target={DisplayValue(contract.ValidationTarget)}",
            $"required_types={DisplayValue(string.Join(", ", contract.RequiredTypeNames))}",
            $"required_members={DisplayValue(string.Join(", ", contract.RequiredMemberNames))}",
            $"required_apis={DisplayValue(string.Join(", ", contract.RequiredApiTokens))}",
            $"allowed_namespaces={DisplayValue(string.Join(", ", contract.AllowedNamespaces))}",
            $"allowed_api_owners={DisplayValue(string.Join(", ", contract.AllowedApiOwnerTokens))}",
            $"forbidden_namespaces={DisplayValue(string.Join(", ", contract.ForbiddenNamespaces))}",
            $"profile_requirements={DisplayValue(string.Join(" | ", contract.ProfileRequirements))}",
            $"allow_placeholders={(contract.AllowPlaceholders ? "yes" : "no")}",
            $"allow_async={(contract.AllowAsync ? "yes" : "no")}",
            "rules=no TODO, no NotImplementedException, no invented helper APIs, block instead of guess"
        };

        if (contract.ExpectedSiblingFiles.Count > 0)
            lines.Add($"sibling_files={string.Join(", ", contract.ExpectedSiblingFiles)}");
        if (contract.CompanionArtifactHints.Count > 0)
            lines.Add($"companion_artifacts={string.Join(", ", contract.CompanionArtifactHints)}");
        if (contract.LocalContextHints.Count > 0)
            lines.Add($"local_context={string.Join(" | ", contract.LocalContextHints)}");
        if (contract.DependencyPrerequisites.Count > 0)
            lines.Add($"dependency_prerequisites={string.Join(", ", contract.DependencyPrerequisites)}");
        if (!string.IsNullOrWhiteSpace(contract.DependencyStatus))
            lines.Add($"dependency_status={contract.DependencyStatus}");
        if (!string.IsNullOrWhiteSpace(contract.DependencySummary))
            lines.Add($"dependency_summary={contract.DependencySummary}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildContractSummary(CSharpGenerationPromptContractRecord contract)
    {
        var arguments = contract.ArgumentContract ?? new CSharpGenerationArgumentContractRecord();
        return $"generation_contract intent={FormatIntent(contract.Intent)} profile={FormatProfile(contract.Profile)} template={DisplayValue(contract.TemplateKind)} role={DisplayValue(contract.DeclaredRole)} pattern={DisplayValue(contract.DeclaredPattern)} depth={DisplayValue(contract.ImplementationDepth)} followthrough_mode={DisplayValue(arguments.FollowThroughMode)} class_name={DisplayValue(arguments.ClassName)} interfaces={DisplayValue(string.Join(", ", arguments.Interfaces))} constructor_dependencies={DisplayValue(string.Join(", ", arguments.ConstructorDependencies))} required_usings={DisplayValue(string.Join(", ", arguments.RequiredUsings))} supporting_surfaces={DisplayValue(string.Join(", ", arguments.SupportingSurfaces))} validation={DisplayValue(contract.ValidationTarget)} framework={DisplayValue(contract.TargetFramework)} namespace={DisplayValue(contract.NamespaceName)} retrieval={DisplayValue(arguments.RetrievalReadinessStatus)}";
    }

    private static string BuildEvaluationSummary(
        CSharpGenerationPromptContractRecord contract,
        bool accepted,
        bool antiStubPassed,
        bool antiHallucinationPassed,
        bool behaviorPassed,
        bool profileEnforcementPassed,
        PostWriteQualityAssessment postWriteAssessment,
        IReadOnlyList<string> rejectionReasons)
    {
        var reasonText = rejectionReasons.Count == 0 ? "(none)" : string.Join(", ", rejectionReasons);
        var outputQuality = ResolveOutputQuality(contract, accepted, postWriteAssessment.BehaviorDepthTier);
        var completionStrength = ResolveCompletionStrength(contract, accepted, postWriteAssessment.BehaviorDepthTier);
        var strongerBehaviorProofStillMissing = ResolveStrongerBehaviorProofStillMissing(contract, accepted, postWriteAssessment.BehaviorDepthTier)
            ? "true"
            : "false";
        var postWriteRules = postWriteAssessment.FailedRules.Count == 0 ? "(none)" : string.Join(", ", postWriteAssessment.FailedRules);
        return $"generation_guardrails {(accepted ? "accepted" : "rejected")}: intent={FormatIntent(contract.Intent)} profile={FormatProfile(contract.Profile)} template={DisplayValue(contract.TemplateKind)} role={DisplayValue(contract.DeclaredRole)} pattern={DisplayValue(contract.DeclaredPattern)} depth={DisplayValue(contract.ImplementationDepth)} followthrough={DisplayValue(string.Join(", ", contract.FollowThroughRequirements))} validation={DisplayValue(contract.ValidationTarget)} framework={DisplayValue(contract.TargetFramework)} namespace={DisplayValue(contract.NamespaceName)} anti_stub={(antiStubPassed ? "passed" : "rejected")} anti_hallucination={(antiHallucinationPassed ? "passed" : "rejected")} behavior={(behaviorPassed ? "passed" : "rejected")} profile_enforcement={(profileEnforcementPassed ? "passed" : "rejected")} post_write={DisplayValue(postWriteAssessment.Status)} family_alignment={DisplayValue(postWriteAssessment.FamilyAlignmentStatus)} integration={DisplayValue(postWriteAssessment.IntegrationStatus)} behavior_depth={DisplayValue(postWriteAssessment.BehaviorDepthTier)} output_quality={DisplayValue(outputQuality)} completion_strength={DisplayValue(completionStrength)} stronger_behavior_proof_missing={strongerBehaviorProofStillMissing} retry={(accepted ? "not_needed" : "not_attempted")} post_write_rules={postWriteRules} reasons={reasonText}";
    }

    private static string ResolveDecisionCode(
        bool antiStubPassed,
        bool antiHallucinationPassed,
        bool behaviorPassed,
        bool profileEnforcementPassed)
    {
        if (antiStubPassed && antiHallucinationPassed && behaviorPassed && profileEnforcementPassed)
            return "accepted";

        var failedCount = 0;
        if (!antiStubPassed)
            failedCount++;
        if (!antiHallucinationPassed)
            failedCount++;
        if (!behaviorPassed)
            failedCount++;
        if (!profileEnforcementPassed)
            failedCount++;

        if (failedCount > 1)
            return "rejected_multiple_guardrails";
        if (!antiStubPassed)
            return "rejected_anti_stub";
        if (!antiHallucinationPassed)
            return "rejected_anti_hallucination";
        if (!behaviorPassed)
            return "rejected_behavior";
        return "rejected_profile_enforcement";
    }

    private static string ResolvePrimaryRejectionClass(
        bool antiStubPassed,
        bool antiHallucinationPassed,
        bool behaviorPassed,
        bool profileEnforcementPassed)
    {
        var failed = new List<string>();
        if (!antiStubPassed)
            failed.Add("anti_stub");
        if (!antiHallucinationPassed)
            failed.Add("anti_hallucination");
        if (!behaviorPassed)
            failed.Add("behavior");
        if (!profileEnforcementPassed)
            failed.Add("profile_enforcement");

        return failed.Count switch
        {
            0 => "none",
            1 => failed[0],
            _ => "multiple"
        };
    }

    private static string ResolveOutputQuality(CSharpGenerationPromptContractRecord contract, bool accepted, string behaviorDepthTier)
    {
        if (!accepted)
            return "rejected";

        if (!string.IsNullOrWhiteSpace(behaviorDepthTier))
            return behaviorDepthTier;

        return contract.Profile switch
        {
            CSharpGenerationProfile.WpfXamlStubOnly => "accepted_write_only",
            CSharpGenerationProfile.ContractGeneration => "accepted_structural_impl",
            CSharpGenerationProfile.RuntimeWiring => "accepted_structural_impl",
            CSharpGenerationProfile.TestRegistryImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.SnapshotBuilderImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.FindingsNormalizerImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.TestHelperImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.BuilderImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.NormalizerImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.WpfXamlLayoutImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.WpfViewmodelImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.WpfShellIntegration => "accepted_behavior_impl",
            CSharpGenerationProfile.RepositoryImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.SimpleImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.ViewmodelGeneration => "accepted_behavior_impl",
            _ => "accepted_behavior_impl"
        };
    }

    private static string ResolveCompletionStrength(CSharpGenerationPromptContractRecord contract, bool accepted, string behaviorDepthTier)
    {
        if (!accepted)
            return "rejected";

        if (!string.IsNullOrWhiteSpace(behaviorDepthTier))
            return behaviorDepthTier;

        return contract.Profile switch
        {
            CSharpGenerationProfile.WpfXamlStubOnly => "accepted_write_only",
            CSharpGenerationProfile.ContractGeneration => "accepted_structural_impl",
            CSharpGenerationProfile.RuntimeWiring => "accepted_structural_impl",
            CSharpGenerationProfile.TestRegistryImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.SnapshotBuilderImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.FindingsNormalizerImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.TestHelperImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.BuilderImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.NormalizerImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.WpfXamlLayoutImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.WpfViewmodelImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.WpfShellIntegration => "accepted_behavior_impl",
            CSharpGenerationProfile.RepositoryImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.SimpleImplementation => "accepted_behavior_impl",
            CSharpGenerationProfile.ViewmodelGeneration => "accepted_behavior_impl",
            _ => "accepted_behavior_impl"
        };
    }

    private static bool ResolveStrongerBehaviorProofStillMissing(CSharpGenerationPromptContractRecord contract, bool accepted, string behaviorDepthTier)
    {
        return !accepted
            || !string.Equals(
                ResolveCompletionStrength(contract, accepted, behaviorDepthTier),
                "verified_integrated_behavior",
                StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ExtractUsingNamespaces(string content)
    {
        return Regex.Matches(content ?? "", @"^\s*using\s+([A-Za-z0-9_\.]+)\s*;", RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ExtractQualifiedOwners(string content)
    {
        return Regex.Matches(content ?? "", @"(?<!\.)\b([A-Z][A-Za-z0-9_]*)\s*\.\s*[A-Za-z_][A-Za-z0-9_]*\s*\(", RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ExtractDeclaredPropertyNames(string content)
    {
        return Regex.Matches(content ?? "", @"\b(?:public|internal|protected)\s+(?:static\s+)?[A-Za-z0-9_<>,\[\]\?]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{", RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ResolveAllowAsync(string declaredPattern, CSharpGenerationProfile profile)
    {
        if (declaredPattern is "service" or "repository" or "controller" or "test_harness" or "worker_support")
            return true;

        return profile is CSharpGenerationProfile.RepositoryImplementation
            or CSharpGenerationProfile.RuntimeWiring
            or CSharpGenerationProfile.TestHelperImplementation
            or CSharpGenerationProfile.TestRegistryImplementation
            or CSharpGenerationProfile.SnapshotBuilderImplementation
            or CSharpGenerationProfile.FindingsNormalizerImplementation;
    }

    private static IEnumerable<string> ExtractMemberNames(string content)
    {
        return Regex.Matches(content ?? "", @"\b(?:public|internal|protected)\s+(?:static\s+|sealed\s+|override\s+|virtual\s+|async\s+)*[A-Za-z0-9_<>,\[\]\?]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:\(|\{)", RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool ContainsIdentifier(string content, string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return true;

        return Regex.IsMatch(content ?? "", $@"\b{Regex.Escape(identifier)}\b", RegexOptions.CultureInvariant);
    }

    private static List<string> InferTypeNamesFromFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(stem) ? [] : [stem];
    }

    private static bool ContainsAny(string content, params string[] values)
    {
        foreach (var value in values)
        {
            if (content.Contains(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizeRelativePath(string value)
    {
        return (value ?? "").Replace('\\', '/').Trim();
    }

    private static string NormalizeToolName(string value)
    {
        return (value ?? "").Trim().ToLowerInvariant();
    }

    private static string SanitizeIdentifier(string value)
    {
        var parts = Regex.Matches(value ?? "", @"[A-Za-z0-9]+", RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
        return parts.Count == 0 ? "" : string.Concat(parts.Select(Capitalize));
    }

    private static string Capitalize(string value)
    {
        return value.Length == 0 ? "" : char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static string FormatIntent(CSharpGenerationIntent intent)
    {
        return intent switch
        {
            CSharpGenerationIntent.ScaffoldFile => "scaffold_file",
            CSharpGenerationIntent.ImplementBehavior => "implement_behavior",
            CSharpGenerationIntent.WireRuntimeIntegration => "wire_runtime_integration",
            CSharpGenerationIntent.VerifyBehavior => "verify_behavior",
            _ => "none"
        };
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

    private static CSharpGenerationProfile ResolveOverrideProfile(string value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "contract_generation" => CSharpGenerationProfile.ContractGeneration,
            "simple_implementation" => CSharpGenerationProfile.SimpleImplementation,
            "test_registry_impl" => CSharpGenerationProfile.TestRegistryImplementation,
            "snapshot_builder_impl" => CSharpGenerationProfile.SnapshotBuilderImplementation,
            "findings_normalizer_impl" => CSharpGenerationProfile.FindingsNormalizerImplementation,
            "test_helper_impl" => CSharpGenerationProfile.TestRegistryImplementation,
            "builder_impl" => CSharpGenerationProfile.SnapshotBuilderImplementation,
            "normalizer_impl" => CSharpGenerationProfile.FindingsNormalizerImplementation,
            "repository_implementation" => CSharpGenerationProfile.RepositoryImplementation,
            "viewmodel_generation" => CSharpGenerationProfile.ViewmodelGeneration,
            "wpf_xaml_stub_only" => CSharpGenerationProfile.WpfXamlStubOnly,
            "wpf_xaml_layout_impl" => CSharpGenerationProfile.WpfXamlLayoutImplementation,
            "wpf_viewmodel_impl" => CSharpGenerationProfile.WpfViewmodelImplementation,
            "wpf_shell_integration" => CSharpGenerationProfile.WpfShellIntegration,
            "runtime_wiring" => CSharpGenerationProfile.RuntimeWiring,
            _ => CSharpGenerationProfile.None
        };
    }

    private static string ResolveImplementationDepth(ToolRequest request)
    {
        var value = GetOptionalArgument(request, "depth");
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "" => "standard",
            "scaffold" => "scaffold",
            "structural" => "scaffold",
            "basic" => "scaffold",
            "behavioral" => "standard",
            "standard" => "standard",
            "integrated" => "standard",
            "strong" => "strong",
            _ => normalized
        };
    }

    private static List<string> ResolveFollowThroughRequirements(ToolRequest request)
    {
        var rawValue = FirstNonEmpty(GetOptionalArgument(request, "followthrough"), GetOptionalArgument(request, "follow_through"));
        if (string.IsNullOrWhiteSpace(rawValue))
            return [];

        return rawValue
            .Split([',', '|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildArgumentContextHints(
        string templateKind,
        string declaredRole,
        string declaredPattern,
        string implementationDepth,
        IReadOnlyList<string> followThroughRequirements,
        string validationTarget)
    {
        var hints = new List<string>();
        if (!string.IsNullOrWhiteSpace(templateKind))
            hints.Add($"template={templateKind}");
        if (!string.IsNullOrWhiteSpace(declaredRole))
            hints.Add($"role={declaredRole}");
        if (!string.IsNullOrWhiteSpace(declaredPattern))
            hints.Add($"pattern={declaredPattern}");
        if (!string.IsNullOrWhiteSpace(implementationDepth))
            hints.Add($"depth={implementationDepth}");
        if (followThroughRequirements.Count > 0)
            hints.Add($"followthrough={string.Join(", ", followThroughRequirements)}");
        if (!string.IsNullOrWhiteSpace(validationTarget))
            hints.Add($"validation={validationTarget}");
        return hints;
    }

    private static List<string> ResolveCompanionArtifactHints(
        string relativePath,
        string fileName,
        string declaredPattern,
        string implementationDepth,
        IReadOnlyList<string> followThroughRequirements)
    {
        var hints = new List<string>();
        if (string.Equals(declaredPattern, "repository", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(fileName, "FileSettingsStore.cs", StringComparison.OrdinalIgnoreCase))
                hints.Add($"interface_contract:{BuildSiblingPath(relativePath, "ISettingsStore.cs")}");
            else if (string.Equals(fileName, "SqliteSnapshotRepository.cs", StringComparison.OrdinalIgnoreCase))
                hints.Add($"interface_contract:{BuildSiblingPath(relativePath, "ISnapshotRepository.cs")}");
            if (implementationDepth is "standard" or "strong")
                hints.Add("helper_methods:storage_path_and_payload_normalization");
        }

        if (string.Equals(declaredPattern, "viewmodel", StringComparison.OrdinalIgnoreCase)
            && implementationDepth is "standard" or "strong")
        {
            hints.Add("helper_methods:navigation_or_state_selection");
        }

        if (string.Equals(declaredPattern, "page", StringComparison.OrdinalIgnoreCase)
            && implementationDepth is "standard" or "strong")
        {
            hints.Add("binding_surface:root_named_regions_and_section_bindings");
        }

        if (string.Equals(declaredPattern, "service", StringComparison.OrdinalIgnoreCase))
            hints.Add("supporting_surface:service_contract_or_use_site");

        if (string.Equals(declaredPattern, "controller", StringComparison.OrdinalIgnoreCase))
            hints.Add("supporting_surface:request_response_contracts_and_service_dependency");

        if (string.Equals(declaredPattern, "test_harness", StringComparison.OrdinalIgnoreCase))
        {
            hints.Add("companion_test_support:registry_snapshot_or_normalizer_helpers");
        }

        if (followThroughRequirements.Contains("verification_required", StringComparer.OrdinalIgnoreCase))
            hints.Add("post_write_verification_required");

        return hints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string GetOptionalArgument(ToolRequest request, string key)
    {
        return request is not null && request.TryGetArgument(key, out var value)
            ? value
            : "";
    }

    private static List<string> ResolveDependencyPrerequisites(ToolRequest request)
    {
        var rawValue = GetOptionalArgument(request, "dependency_prerequisites");
        if (string.IsNullOrWhiteSpace(rawValue))
            return [];

        return rawValue
            .Split([',', '|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string InferProjectNameFromPath(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return "";

        string projectSegment;
        if ((string.Equals(segments[0], "src", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segments[0], "tests", StringComparison.OrdinalIgnoreCase))
            && segments.Length > 1)
        {
            projectSegment = segments[1];
        }
        else
        {
            projectSegment = segments[0];
        }

        var parts = projectSegment
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeIdentifier)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        return parts.Count == 0 ? "" : string.Join(".", parts);
    }

    private static string BuildSiblingPath(string relativePath, string siblingFileName)
    {
        var directory = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        return NormalizeRelativePath(Path.Combine(directory, siblingFileName));
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

    private readonly record struct ProjectContext(string ProjectName, string TargetFramework, string ProjectPath);

    private sealed class PostWriteQualityAssessment
    {
        public string Status { get; set; } = "";
        public string FamilyAlignmentStatus { get; set; } = "";
        public string IntegrationStatus { get; set; } = "";
        public string BehaviorDepthTier { get; set; } = "";
        public List<string> FailedRules { get; set; } = [];
        public List<string> ObservedSignals { get; set; } = [];
    }
}
