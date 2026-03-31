using System.IO;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardBuilderOperationResolutionService
{
    public const string ResolverContractVersion = "builder_operation_resolution.v8";
    private static readonly DotnetScaffoldSurfaceService DotnetScaffoldSurfaceService = new();

    private static readonly Regex ExplicitFileNamePattern = new(
        @"(?<name>[A-Za-z0-9_./\\-]+)\.(?<ext>sln|csproj)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitNamePattern = new(
        @"\b(?:named|called)\s+(?<name>(""[^""]+""|'[^']+'|[A-Za-z0-9_. -]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedNamePattern = new(
        @"[""'](?<name>[^""']+)[""']",
        RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitProjectToSolutionPattern = new(
        @"\b(?:add|attach|include|wire|register)\s+(?:app\s+|test\s+)?project\s+(?<project>[A-Za-z0-9_.-]+)\s+(?:to|into)\s+solution\s+(?<solution>[A-Za-z0-9_.-]+(?:\.sln)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitReferenceFromToPattern = new(
        @"\b(?:add\s+(?:dotnet\s+)?(?:project\s+)?)?reference\s+from\s+(?<source>[A-Za-z0-9_.-]+)\s+to\s+(?<target>[A-Za-z0-9_.-]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitReferencePairPattern = new(
        @"\badd\s+dotnet\s+project\s+reference\s+(?<source>[A-Za-z0-9_.-]+)\s+(?<target>[A-Za-z0-9_.-]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UnsafeAbsolutePathPattern = new(
        @"\b[A-Za-z]:\\",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] SystemMutationPhrases =
    [
        "registry",
        "regedit",
        "firewall",
        "bitlocker",
        "defender policy",
        "security policy",
        "group policy",
        "gpedit",
        "install service",
        "windows service",
        "service installation",
        "user account",
        "local account"
    ];

    private static readonly string[] ElevatedPhrases =
    [
        "run as administrator",
        "administrator",
        "admin rights",
        "elevation",
        "elevated",
        "machine-wide"
    ];

    private static readonly string[] DestructivePhrases =
    [
        "delete ",
        "remove ",
        "wipe ",
        "format ",
        "uninstall "
    ];

    private readonly CommandCanonicalizationService _commandCanonicalizationService = new();

    public TaskboardBuilderOperationResolutionResult Resolve(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle)
    {
        var prompt = Normalize(FirstNonEmpty(workItem.PromptText, workItem.Title, workItem.Summary));
        var summary = Normalize(workItem.Summary);
        var canonicalization = _commandCanonicalizationService.Canonicalize(
            FirstNonEmpty(workItem.PromptText, workItem.Title, workItem.Summary),
            workspaceRoot,
            workItemId: workItem.WorkItemId,
            workItemTitle: workItem.Title);
        if (string.IsNullOrWhiteSpace(prompt))
            return new TaskboardBuilderOperationResolutionResult();

        if (ContainsAny(prompt, SystemMutationPhrases) || ContainsAny(summary, SystemMutationPhrases))
        {
            return BuildMatched(
                TaskboardExecutionEligibilityKind.ManualOnlySystemMutation,
                "",
                $"Taskboard auto-run paused: `{workItem.Title}` mutates system or security state and remains manual-only.");
        }

        if (ContainsAny(prompt, ElevatedPhrases) || ContainsAny(summary, ElevatedPhrases))
        {
            return BuildMatched(
                TaskboardExecutionEligibilityKind.ManualOnlyElevated,
                "",
                $"Taskboard auto-run paused: `{workItem.Title}` requires elevation and remains manual-only.");
        }

        if (ContainsAny(prompt, DestructivePhrases)
            && (UnsafeAbsolutePathPattern.IsMatch(workItem.PromptText ?? "")
                || prompt.Contains("outside workspace", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains(@"c:\windows", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("program files", StringComparison.OrdinalIgnoreCase)))
        {
            return BuildMatched(
                TaskboardExecutionEligibilityKind.BlockedUnsafe,
                "",
                $"Taskboard auto-run blocked: `{workItem.Title}` is destructive outside the workspace.");
        }

        var preferCanonicalOperation = ShouldPreferCanonicalOperation(workItem, canonicalization);

        if (preferCanonicalOperation
            && TryResolveCanonicalOperation(workspaceRoot, workItem, planTitle, canonicalization, out var preferredCanonicalOperation))
        {
            return preferredCanonicalOperation;
        }

        if (TryResolveExplicitOperation(workspaceRoot, workItem, planTitle, out var explicitOperationRequest))
            return explicitOperationRequest;

        if (TryResolveStorageBootstrap(workspaceRoot, workItem, planTitle, out var storageBootstrapRequest))
            return storageBootstrapRequest;

        if (TryResolveCanonicalOperation(workspaceRoot, workItem, planTitle, canonicalization, out var canonicalOperation))
            return canonicalOperation;

        if (TryResolveCreateSolution(workspaceRoot, workItem, planTitle, out var createSolution))
            return createSolution;

        if (TryResolveCreateProject(workspaceRoot, workItem, planTitle, out var createProject))
            return createProject;

        if (TryResolveAddProjectToSolution(workspaceRoot, workItem, planTitle, out var addProjectToSolution))
            return addProjectToSolution;

        if (TryResolveAddProjectReference(workspaceRoot, workItem, planTitle, out var addProjectReference))
            return addProjectReference;

        if (TryResolveBuildRequest(workspaceRoot, workItem, planTitle, out var buildRequest))
            return buildRequest;

        if (TryResolveTestRequest(workspaceRoot, workItem, planTitle, out var testRequest))
            return testRequest;

        return new TaskboardBuilderOperationResolutionResult();
    }

    private static bool ShouldPreferCanonicalOperation(
        TaskboardRunWorkItem workItem,
        CommandCanonicalizationRecord canonicalization)
    {
        var canonicalOperation = Normalize(canonicalization.NormalizedOperationKind);
        if (string.IsNullOrWhiteSpace(canonicalOperation))
            return false;

        var currentOperation = Normalize(workItem.OperationKind);
        if (string.IsNullOrWhiteSpace(currentOperation))
            return true;

        if (currentOperation is "build_solution" or "build_native_workspace")
            return !string.Equals(canonicalOperation, "dotnet.build", StringComparison.OrdinalIgnoreCase);

        if (currentOperation == "run_test_project")
            return !string.Equals(canonicalOperation, "dotnet.test", StringComparison.OrdinalIgnoreCase);

        if (currentOperation is "create_project" or "create_test_project" or "create_core_library"
            or "add_project_to_solution" or "attach_test_project" or "attach_core_library"
            or "add_project_reference" or "add_domain_reference")
        {
            return canonicalOperation.StartsWith("filesystem.", StringComparison.OrdinalIgnoreCase)
                || canonicalOperation.StartsWith("dotnet.create_", StringComparison.OrdinalIgnoreCase)
                || canonicalOperation is "dotnet.add_project_to_solution" or "dotnet.add_project_reference";
        }

        return false;
    }

    private static bool TryResolveExplicitOperation(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle,
        out TaskboardBuilderOperationResolutionResult result)
    {
        var operationKind = Normalize(workItem.OperationKind);
        switch (operationKind)
        {
            case "inspect_context_artifacts":
            {
                result = BuildExecutable(
                    TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                    "show_artifacts",
                    $"Taskboard auto-run executing bounded maintenance-context item: `{workItem.Title}`.",
                    "",
                    Array.Empty<(string Key, string Value)>());
                return true;
            }

            case "inspect_solution_wiring":
            {
                var targetPath = ResolveExistingSolutionPath(workspaceRoot, workItem, planTitle);
                targetPath = FirstNonEmpty(targetPath, ResolveExistingProjectPath(workspaceRoot, workItem, planTitle));
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    result = BuildMatched(
                        TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                        "",
                        $"Taskboard auto-run paused: `{workItem.Title}` resolved to operation_kind=inspect_solution_wiring, but no deterministic workspace build target could be found.");
                    return true;
                }

                result = BuildExecutable(
                    TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                    "plan_repair",
                    $"Taskboard auto-run executing bounded build-repair item: `{workItem.Title}`.",
                    targetPath,
                    ("scope", "build"),
                    ("path", targetPath));
                return true;
            }

            case "inspect_project_reference_graph":
            case "repair_project_attachment":
            case "repair_generated_build_targets":
            {
                var targetPath = ResolveExistingProjectPath(workspaceRoot, workItem, planTitle);
                targetPath = FirstNonEmpty(targetPath, ResolveExistingSolutionPath(workspaceRoot, workItem, planTitle));
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    result = BuildMatched(
                        TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                        "",
                        $"Taskboard auto-run paused: `{workItem.Title}` resolved to operation_kind={operationKind}, but no deterministic workspace project target could be found.");
                    return true;
                }

                result = BuildExecutable(
                    TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                    "plan_repair",
                    $"Taskboard auto-run executing bounded build-repair item: `{workItem.Title}`.",
                    targetPath,
                    ("scope", "build"),
                    ("path", targetPath));
                return true;
            }

            case "build_solution":
            {
                var targetPath = ResolveWorkspaceBuildTarget(workspaceRoot, workItem, planTitle);
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    result = BuildMatched(
                        TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                        "",
                        $"Taskboard auto-run paused: `{workItem.Title}` resolved to operation_kind=build_solution, but no deterministic .NET build target could be found in the workspace.");
                    return true;
                }

                result = BuildExecutable(
                    TaskboardExecutionEligibilityKind.WorkspaceBuildSafe,
                    "dotnet_build",
                    $"Taskboard auto-run executing workspace-safe verification item: `{workItem.Title}`.",
                    targetPath,
                    ("project", targetPath));
                return true;
            }

            case "make_storage_dir":
            {
                var storageDirectory = ResolveStorageDirectory(workspaceRoot, workItem, planTitle);
                if (string.IsNullOrWhiteSpace(storageDirectory))
                {
                    result = BuildMatched(
                        TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                        "",
                        $"Taskboard auto-run paused: `{workItem.Title}` resolved to operation_kind=make_storage_dir, but no deterministic storage directory could be found.");
                    return true;
                }

                result = BuildExecutable(
                    TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                    "make_dir",
                    $"Taskboard auto-run executing bounded storage bootstrap item: `{workItem.Title}`.",
                    storageDirectory,
                    ("path", storageDirectory));
                return true;
            }

            case "write_storage_contract":
            {
                if (!TryResolveStorageWriteTarget(workspaceRoot, workItem, planTitle, "ISettingsStore.cs", out var targetPath, out var projectName))
                {
                    result = BuildMatched(
                        TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                        "",
                        $"Taskboard auto-run paused: `{workItem.Title}` resolved to operation_kind=write_storage_contract, but the storage boundary target could not be inferred.");
                    return true;
                }

                result = BuildExecutable(
                    TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                    "initialize_sqlite_storage_boundary",
                    $"Taskboard auto-run executing bounded storage bootstrap item: `{workItem.Title}`.",
                    targetPath,
                    ("path", targetPath),
                    ("content", BuildISettingsStoreCs(projectName)));
                return true;
            }

            case "write_storage_impl":
            {
                if (!TryResolveStorageWriteTarget(workspaceRoot, workItem, planTitle, "FileSettingsStore.cs", out var targetPath, out var projectName))
                {
                    result = BuildMatched(
                        TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                        "",
                        $"Taskboard auto-run paused: `{workItem.Title}` resolved to operation_kind=write_storage_impl, but the storage implementation target could not be inferred.");
                    return true;
                }

                result = BuildExecutable(
                    TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                    "register_di_service",
                    $"Taskboard auto-run executing bounded storage bootstrap item: `{workItem.Title}`.",
                    targetPath,
                    ("path", targetPath),
                    ("content", BuildFileSettingsStoreCs(projectName)));
                return true;
            }

            case "write_repository_contract":
            {
                if (!TryResolveStorageWriteTarget(workspaceRoot, workItem, planTitle, "ISnapshotRepository.cs", out var targetPath, out var projectName))
                {
                    result = BuildMatched(
                        TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                        "",
                        $"Taskboard auto-run paused: `{workItem.Title}` resolved to operation_kind=write_repository_contract, but the repository contract target could not be inferred.");
                    return true;
                }

                result = BuildExecutable(
                    TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                    "write_file",
                    $"Taskboard auto-run executing bounded storage scaffold item: `{workItem.Title}`.",
                    targetPath,
                    ("path", targetPath),
                    ("content", BuildSnapshotRepositoryContractCs(projectName)));
                return true;
            }

            case "write_repository_impl":
            {
                if (!TryResolveStorageWriteTarget(workspaceRoot, workItem, planTitle, "SqliteSnapshotRepository.cs", out var targetPath, out var projectName))
                {
                    result = BuildMatched(
                        TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                        "",
                        $"Taskboard auto-run paused: `{workItem.Title}` resolved to operation_kind=write_repository_impl, but the repository implementation target could not be inferred.");
                    return true;
                }

                result = BuildExecutable(
                    TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                    "write_file",
                    $"Taskboard auto-run executing bounded storage scaffold item: `{workItem.Title}`.",
                    targetPath,
                    ("path", targetPath),
                    ("content", BuildSnapshotRepositoryImplCs(projectName)));
                return true;
            }

            case "run_test_project":
            {
                var targetPath = ResolveWorkspaceTestTarget(workspaceRoot, workItem, planTitle);
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    result = BuildMatched(
                        TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                        "",
                        $"Taskboard auto-run paused: `{workItem.Title}` resolved to operation_kind=run_test_project, but no deterministic test target could be found in the workspace.");
                    return true;
                }

                result = BuildExecutable(
                    TaskboardExecutionEligibilityKind.WorkspaceTestSafe,
                    "dotnet_test",
                    $"Taskboard auto-run executing workspace-safe verification item: `{workItem.Title}`.",
                    targetPath,
                    ("project", targetPath));
                return true;
            }
        }

        result = new TaskboardBuilderOperationResolutionResult();
        return false;
    }

    private static bool TryResolveCreateSolution(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle,
        out TaskboardBuilderOperationResolutionResult result)
    {
        var prompt = Normalize(workItem.PromptText);
        if (!LooksLikeCreateSolutionCommand(prompt)
            || LooksLikeAddProjectToSolutionCommand(prompt))
        {
            result = new TaskboardBuilderOperationResolutionResult();
            return false;
        }

        var solutionName = DetermineSolutionName(workspaceRoot, workItem, planTitle);
        var targetPath = $"{solutionName}.sln";
        result = BuildExecutable(
            TaskboardExecutionEligibilityKind.WorkspaceBuildSafe,
            "create_dotnet_solution",
            $"Taskboard auto-run executing workspace-safe builder item: `{workItem.Title}`.",
            targetPath,
            ("solution_name", solutionName));
        return true;
    }

    private static bool TryResolveStorageBootstrap(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle,
        out TaskboardBuilderOperationResolutionResult result)
    {
        var operationKind = Normalize(workItem.OperationKind);
        var phraseFamily = Normalize(workItem.PhraseFamily);
        var workFamily = Normalize(workItem.WorkFamily);
        var prompt = Normalize($"{workItem.Title} {workItem.PromptText} {workItem.Summary}");
        var looksLikeGenericStorageBootstrap = operationKind.Length == 0
            && (string.Equals(phraseFamily, "setup_storage_layer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(workFamily, "storage_bootstrap", StringComparison.OrdinalIgnoreCase))
            && ContainsAny(prompt, new[] { "storage layer", "sqlite storage", "storage boundary", "persistence files" });
        if (!looksLikeGenericStorageBootstrap)
        {
            result = new TaskboardBuilderOperationResolutionResult();
            return false;
        }

        var storageDirectory = ResolveStorageDirectory(workspaceRoot, workItem, planTitle);
        var projectName = ResolveStorageProjectName(workspaceRoot, workItem, planTitle);
        if (string.IsNullOrWhiteSpace(storageDirectory) || string.IsNullOrWhiteSpace(projectName))
        {
            result = BuildMatched(
                TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                "",
                $"Taskboard auto-run paused: `{workItem.Title}` requires a deterministic storage target before storage bootstrap can continue.");
            return true;
        }

        if (!Directory.Exists(Path.Combine(workspaceRoot, storageDirectory.Replace('/', Path.DirectorySeparatorChar))))
        {
            result = BuildExecutable(
                TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                "make_dir",
                $"Taskboard auto-run executing bounded storage bootstrap item: `{workItem.Title}`.",
                storageDirectory,
                ("path", storageDirectory));
            return true;
        }

        var contractPath = NormalizeRelativePath(Path.Combine(storageDirectory, "ISettingsStore.cs"));
        if (!WorkspaceFileExists(workspaceRoot, contractPath))
        {
            result = BuildExecutable(
                TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                "initialize_sqlite_storage_boundary",
                $"Taskboard auto-run executing bounded storage bootstrap item: `{workItem.Title}`.",
                contractPath,
                ("path", contractPath),
                ("content", BuildISettingsStoreCs(projectName)));
            return true;
        }

        var implPath = NormalizeRelativePath(Path.Combine(storageDirectory, "FileSettingsStore.cs"));
        if (!WorkspaceFileExists(workspaceRoot, implPath))
        {
            result = BuildExecutable(
                TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                "register_di_service",
                $"Taskboard auto-run executing bounded storage bootstrap item: `{workItem.Title}`.",
                implPath,
                ("path", implPath),
                ("content", BuildFileSettingsStoreCs(projectName)));
            return true;
        }

        var repositoryContractPath = NormalizeRelativePath(Path.Combine(storageDirectory, "ISnapshotRepository.cs"));
        if (!WorkspaceFileExists(workspaceRoot, repositoryContractPath))
        {
            result = BuildExecutable(
                TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                "write_file",
                $"Taskboard auto-run executing bounded storage scaffold item: `{workItem.Title}`.",
                repositoryContractPath,
                ("path", repositoryContractPath),
                ("content", BuildSnapshotRepositoryContractCs(projectName)));
            return true;
        }

        var repositoryImplPath = NormalizeRelativePath(Path.Combine(storageDirectory, "SqliteSnapshotRepository.cs"));
        if (!WorkspaceFileExists(workspaceRoot, repositoryImplPath))
        {
            result = BuildExecutable(
                TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                "write_file",
                $"Taskboard auto-run executing bounded storage scaffold item: `{workItem.Title}`.",
                repositoryImplPath,
                ("path", repositoryImplPath),
                ("content", BuildSnapshotRepositoryImplCs(projectName)));
            return true;
        }

        result = new TaskboardBuilderOperationResolutionResult();
        return false;
    }

    private static bool TryResolveCreateProject(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle,
        out TaskboardBuilderOperationResolutionResult result)
    {
        var prompt = Normalize(workItem.PromptText);
        if (!LooksLikeCreateProjectCommand(prompt)
            || LooksLikeAddProjectReferenceCommand(prompt)
            || LooksLikeAddProjectToSolutionCommand(prompt))
        {
            result = new TaskboardBuilderOperationResolutionResult();
            return false;
        }

        var template = ResolveProjectTemplate(workItem, planTitle);
        if (string.IsNullOrWhiteSpace(template))
        {
            result = BuildMatched(
                TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                "",
                $"Taskboard auto-run paused: `{workItem.Title}` does not resolve to a deterministic project template.");
            return true;
        }
        if (!string.Equals(DotnetScaffoldSurfaceService.ResolveSupportStatus(template), "supported_complete", StringComparison.OrdinalIgnoreCase))
        {
            result = BuildMatched(
                TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                "",
                $"Taskboard auto-run paused: `{workItem.Title}` resolved deferred scaffold template `{template}`. {DotnetScaffoldSurfaceService.ResolveSummary(template)}");
            return true;
        }

        var projectName = DetermineProjectName(workspaceRoot, workItem, planTitle, template);
        var outputPath = DetermineProjectOutputPath(projectName, template);
        var targetFramework = DotnetScaffoldSurfaceService.ResolveTargetFramework(template);
        var templateSwitches = DotnetScaffoldSurfaceService.ResolveDefaultSwitches(template);
        var targetPath = NormalizeRelativePath(Path.Combine(outputPath, $"{projectName}.csproj"));
        result = BuildExecutable(
            TaskboardExecutionEligibilityKind.WorkspaceBuildSafe,
            "create_dotnet_project",
            $"Taskboard auto-run executing workspace-safe builder item: `{workItem.Title}`.",
            targetPath,
            ("template", template),
            ("project_name", projectName),
            ("output_path", outputPath),
            ("target_framework", targetFramework),
            ("template_switches", templateSwitches),
            ("role", DotnetScaffoldSurfaceService.ResolveDefaultRole(template, projectName)));
        return true;
    }

    private static bool TryResolveAddProjectToSolution(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle,
        out TaskboardBuilderOperationResolutionResult result)
    {
        var prompt = Normalize(workItem.PromptText);
        if (!LooksLikeAddProjectToSolutionCommand(prompt))
        {
            result = new TaskboardBuilderOperationResolutionResult();
            return false;
        }

        var solutionPath = ResolveExistingSolutionPath(workspaceRoot, workItem, planTitle);
        var projectPath = ResolveExistingProjectPath(workspaceRoot, workItem, planTitle);
        if (TryResolveExplicitSolutionAttachTargets(workspaceRoot, workItem, planTitle, out var explicitSolutionPath, out var explicitProjectPath))
        {
            solutionPath = explicitSolutionPath;
            projectPath = explicitProjectPath;
        }

        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            result = BuildMatched(
                TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                "",
                $"Taskboard auto-run paused: `{workItem.Title}` could not resolve a solution target inside the workspace.");
            return true;
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            result = BuildMatched(
                TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                "",
                $"Taskboard auto-run paused: `{workItem.Title}` could not resolve a project target inside the workspace.");
            return true;
        }

        result = BuildExecutable(
            TaskboardExecutionEligibilityKind.WorkspaceBuildSafe,
            "add_project_to_solution",
            $"Taskboard auto-run executing workspace-safe builder item: `{workItem.Title}`.",
            solutionPath,
            ("solution_path", solutionPath),
            ("project_path", projectPath));
        return true;
    }

    private static bool TryResolveAddProjectReference(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle,
        out TaskboardBuilderOperationResolutionResult result)
    {
        var prompt = Normalize(workItem.PromptText);
        if (!LooksLikeAddProjectReferenceCommand(prompt))
        {
            result = new TaskboardBuilderOperationResolutionResult();
            return false;
        }

        var projectCandidates = FindWorkspaceFiles(workspaceRoot, "*.csproj");
        if (projectCandidates.Count < 2)
        {
            result = BuildMatched(
                TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                "",
                $"Taskboard auto-run paused: `{workItem.Title}` needs at least two resolved projects before a project reference can be added.");
            return true;
        }

        var sourceProject = "";
        var referencedProject = "";
        if (!TryResolveExplicitProjectReferenceTargets(projectCandidates, workItem, out sourceProject, out referencedProject))
        {
            sourceProject = ResolvePrimaryProjectPath(projectCandidates, workItem, planTitle);
            referencedProject = ResolveReferencedProjectPath(projectCandidates, workItem, planTitle, sourceProject);
        }
        if (string.IsNullOrWhiteSpace(sourceProject) || string.IsNullOrWhiteSpace(referencedProject))
        {
            result = BuildMatched(
                TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                "",
                $"Taskboard auto-run paused: `{workItem.Title}` does not identify a deterministic project-reference pair.");
            return true;
        }

        var referenceDecision = new DotnetProjectReferencePolicyService().Evaluate(workspaceRoot, sourceProject, referencedProject);
        if (referenceDecision.DecisionKind == DotnetProjectReferenceDecisionKind.Blocked)
        {
            result = BuildMatched(
                TaskboardExecutionEligibilityKind.BlockedUnsafe,
                "",
                $"Taskboard auto-run blocked: `{workItem.Title}` project-reference policy refused the resolved pair. {referenceDecision.DecisionSummary}");
            return true;
        }

        result = BuildExecutable(
            TaskboardExecutionEligibilityKind.WorkspaceBuildSafe,
            "add_dotnet_project_reference",
            referenceDecision.DecisionKind == DotnetProjectReferenceDecisionKind.Corrected
                ? $"Taskboard auto-run corrected the project-reference direction for `{workItem.Title}` before execution. {referenceDecision.DecisionSummary}"
                : $"Taskboard auto-run executing workspace-safe builder item: `{workItem.Title}`. {referenceDecision.DecisionSummary}",
            referenceDecision.EffectiveProjectPath,
            ("project_path", referenceDecision.EffectiveProjectPath),
            ("reference_path", referenceDecision.EffectiveReferencePath),
            ("reference_decision_summary", referenceDecision.DecisionSummary));
        return true;
    }

    private static bool TryResolveBuildRequest(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle,
        out TaskboardBuilderOperationResolutionResult result)
    {
        var prompt = Normalize(workItem.PromptText);
        if (!LooksLikeBuildCommand(prompt))
        {
            result = new TaskboardBuilderOperationResolutionResult();
            return false;
        }

        if (!LooksLikeWorkspaceBuildRequest(prompt))
        {
            result = new TaskboardBuilderOperationResolutionResult();
            return false;
        }

        var targetPath = ResolveWorkspaceBuildTarget(workspaceRoot, workItem, planTitle);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            result = BuildMatched(
                TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                "",
                $"Taskboard auto-run paused: `{workItem.Title}` could not resolve a build target inside the workspace.");
            return true;
        }

        result = BuildExecutable(
            TaskboardExecutionEligibilityKind.WorkspaceBuildSafe,
            "dotnet_build",
            $"Taskboard auto-run executing workspace-safe builder item: `{workItem.Title}`.",
            targetPath,
            ("project", targetPath));
        return true;
    }

    private static bool TryResolveTestRequest(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle,
        out TaskboardBuilderOperationResolutionResult result)
    {
        var prompt = Normalize(workItem.PromptText);
        if (!LooksLikeTestCommand(prompt))
        {
            result = new TaskboardBuilderOperationResolutionResult();
            return false;
        }

        if (!LooksLikeWorkspaceTestRequest(prompt))
        {
            result = new TaskboardBuilderOperationResolutionResult();
            return false;
        }

        var targetPath = ResolveWorkspaceTestTarget(workspaceRoot, workItem, planTitle);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            result = BuildMatched(
                TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous,
                "",
                $"Taskboard auto-run paused: `{workItem.Title}` could not resolve a test target inside the workspace.");
            return true;
        }

        result = BuildExecutable(
            TaskboardExecutionEligibilityKind.WorkspaceTestSafe,
            "dotnet_test",
            $"Taskboard auto-run executing workspace-safe builder item: `{workItem.Title}`.",
            targetPath,
            ("project", targetPath));
        return true;
    }

    private bool TryResolveCanonicalOperation(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle,
        CommandCanonicalizationRecord canonicalization,
        out TaskboardBuilderOperationResolutionResult result)
    {
        var normalizedOperation = Normalize(canonicalization.NormalizedOperationKind);
        if (string.IsNullOrWhiteSpace(normalizedOperation))
        {
            result = new TaskboardBuilderOperationResolutionResult();
            return false;
        }

        switch (normalizedOperation)
        {
            case "filesystem.create_directory":
            {
                var targetPath = NormalizeRelativePath(canonicalization.NormalizedTargetPath);
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    result = BuildCanonicalManualOnly(
                        canonicalization,
                        workItem,
                        "Taskboard auto-run paused: canonical directory creation did not resolve a deterministic workspace-relative target path.");
                    return true;
                }

                result = BuildCanonicalExecutable(
                    canonicalization,
                    TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                    "make_dir",
                    $"Taskboard auto-run executing canonical filesystem builder item: `{workItem.Title}`.",
                    targetPath,
                    ("path", targetPath));
                return true;
            }

            case "dotnet.create_solution":
            {
                var solutionName = FirstNonEmpty(
                    GetCanonicalArgument(canonicalization, "name"),
                    TrimSolutionExtension(GetCanonicalArgument(canonicalization, "solution")),
                    canonicalization.NormalizedProjectName,
                    DetermineSolutionName(workspaceRoot, workItem, planTitle));
                if (string.IsNullOrWhiteSpace(solutionName))
                {
                    result = BuildCanonicalManualOnly(
                        canonicalization,
                        workItem,
                        "Taskboard auto-run paused: canonical solution scaffold did not resolve a deterministic solution name.");
                    return true;
                }

                var targetPath = FirstNonEmpty(
                    EnsureSolutionPath(GetCanonicalArgument(canonicalization, "path")),
                    EnsureSolutionPath(GetCanonicalArgument(canonicalization, "solution")),
                    canonicalization.NormalizedTargetPath,
                    $"{solutionName}.sln");
                result = BuildCanonicalExecutable(
                    canonicalization,
                    TaskboardExecutionEligibilityKind.WorkspaceBuildSafe,
                    "create_dotnet_solution",
                    $"Taskboard auto-run executing canonical solution scaffold item: `{workItem.Title}`.",
                    targetPath,
                    ("solution_name", solutionName),
                    ("path", targetPath));
                return true;
            }

            case "dotnet.create_project.wpf":
            case "dotnet.create_project.classlib":
            case "dotnet.create_project.xunit":
            case "dotnet.create_project.console":
            case "dotnet.create_project.worker":
            case "dotnet.create_project.webapi":
            case "dotnet.create_project":
            {
                var template = FirstNonEmpty(
                    GetCanonicalArgument(canonicalization, "template"),
                    canonicalization.NormalizedTemplateHint,
                    ResolveProjectTemplate(workItem, planTitle));
                if (string.IsNullOrWhiteSpace(template))
                {
                    result = BuildCanonicalManualOnly(
                        canonicalization,
                        workItem,
                        $"Taskboard auto-run paused: canonical project scaffold for `{workItem.Title}` does not resolve a deterministic project template.");
                    return true;
                }
                if (!string.Equals(DotnetScaffoldSurfaceService.ResolveSupportStatus(template), "supported_complete", StringComparison.OrdinalIgnoreCase))
                {
                    result = BuildCanonicalManualOnly(
                        canonicalization,
                        workItem,
                        $"Taskboard auto-run paused: canonical project scaffold for `{workItem.Title}` resolved deferred template `{template}`. {DotnetScaffoldSurfaceService.ResolveSummary(template)}");
                    return true;
                }

                var projectName = FirstNonEmpty(
                    GetCanonicalArgument(canonicalization, "name"),
                    GetCanonicalArgument(canonicalization, "project"),
                    canonicalization.NormalizedProjectName,
                    DetermineProjectName(workspaceRoot, workItem, planTitle, template));
                if (string.IsNullOrWhiteSpace(projectName))
                {
                    result = BuildCanonicalManualOnly(
                        canonicalization,
                        workItem,
                        $"Taskboard auto-run paused: canonical project scaffold for `{workItem.Title}` does not resolve a deterministic project name.");
                    return true;
                }

                var explicitPath = GetCanonicalArgument(canonicalization, "path");
                var outputPath = DetermineProjectOutputPathFromArguments(projectName, template, explicitPath);
                var targetFramework = DotnetScaffoldSurfaceService.ResolveTargetFramework(template, GetCanonicalArgument(canonicalization, "target_framework"));
                var templateSwitches = DotnetScaffoldSurfaceService.ResolveDefaultSwitches(template, GetCanonicalArgument(canonicalization, "template_switches"));
                var targetPath = FirstNonEmpty(
                    ResolveProjectTargetPathFromArguments(projectName, outputPath, explicitPath),
                    NormalizeRelativePath(canonicalization.NormalizedTargetPath),
                    NormalizeRelativePath(Path.Combine(outputPath, $"{projectName}.csproj")));
                var solutionPath = EnsureSolutionPath(GetCanonicalArgument(canonicalization, "solution"));
                result = BuildCanonicalExecutable(
                    canonicalization,
                    TaskboardExecutionEligibilityKind.WorkspaceBuildSafe,
                    "create_dotnet_project",
                    $"Taskboard auto-run executing canonical project scaffold item: `{workItem.Title}`.",
                    targetPath,
                    ("template", template),
                    ("project_name", projectName),
                    ("output_path", outputPath),
                    ("path", targetPath),
                    ("name", projectName),
                    ("role", FirstNonEmpty(GetCanonicalArgument(canonicalization, "role"), canonicalization.TargetRoleHint, DotnetScaffoldSurfaceService.ResolveDefaultRole(template, projectName))),
                    ("target_framework", targetFramework),
                    ("template_switches", templateSwitches));
                if (!string.IsNullOrWhiteSpace(solutionPath))
                    result.Arguments["solution_path"] = solutionPath;
                return true;
            }

            case "dotnet.add_project_to_solution":
            {
                var projectCandidates = FindWorkspaceFiles(workspaceRoot, "*.csproj");
                var solutionPath = FirstNonEmpty(
                    ResolveSolutionPathFromCanonicalArguments(canonicalization),
                    ResolveExistingSolutionPath(workspaceRoot, workItem, planTitle));
                var projectPath = FirstNonEmpty(
                    ResolveProjectPathFromCanonicalArguments(projectCandidates, canonicalization),
                    ResolveExistingProjectPath(workspaceRoot, workItem, planTitle));
                if (TryResolveExplicitSolutionAttachTargets(workspaceRoot, workItem, planTitle, out var explicitSolutionPath, out var explicitProjectPath))
                {
                    solutionPath = explicitSolutionPath;
                    projectPath = explicitProjectPath;
                }
                if (string.IsNullOrWhiteSpace(solutionPath) || string.IsNullOrWhiteSpace(projectPath))
                {
                    result = BuildCanonicalManualOnly(
                        canonicalization,
                        workItem,
                        $"Taskboard auto-run paused: canonical solution attach for `{workItem.Title}` could not resolve deterministic solution/project targets.");
                    return true;
                }

                result = BuildCanonicalExecutable(
                    canonicalization,
                    TaskboardExecutionEligibilityKind.WorkspaceBuildSafe,
                    "add_project_to_solution",
                    $"Taskboard auto-run executing canonical solution attach item: `{workItem.Title}`.",
                    solutionPath,
                    ("solution_path", solutionPath),
                    ("project_path", projectPath),
                    ("project", ResolveProjectNameFromPath(projectPath)));
                return true;
            }

            case "dotnet.add_project_reference":
            {
                var projectCandidates = FindWorkspaceFiles(workspaceRoot, "*.csproj");
                var sourceProject = "";
                var referencedProject = "";
                if (!TryResolveProjectReferenceTargetsFromCanonicalArguments(projectCandidates, canonicalization, out sourceProject, out referencedProject)
                    && !TryResolveExplicitProjectReferenceTargets(projectCandidates, workItem, out sourceProject, out referencedProject))
                {
                    sourceProject = ResolvePrimaryProjectPath(projectCandidates, workItem, planTitle);
                    referencedProject = ResolveReferencedProjectPath(projectCandidates, workItem, planTitle, sourceProject);
                }
                if (string.IsNullOrWhiteSpace(sourceProject) || string.IsNullOrWhiteSpace(referencedProject))
                {
                    result = BuildCanonicalManualOnly(
                        canonicalization,
                        workItem,
                        $"Taskboard auto-run paused: canonical project reference for `{workItem.Title}` does not identify a deterministic source/reference pair.");
                    return true;
                }

                var referenceDecision = new DotnetProjectReferencePolicyService().Evaluate(workspaceRoot, sourceProject, referencedProject);
                if (referenceDecision.DecisionKind == DotnetProjectReferenceDecisionKind.Blocked)
                {
                    result = BuildCanonicalBlocked(
                        canonicalization,
                        workItem,
                        $"Taskboard auto-run blocked: canonical project-reference policy refused the resolved pair. {referenceDecision.DecisionSummary}");
                    return true;
                }

                result = BuildCanonicalExecutable(
                    canonicalization,
                    TaskboardExecutionEligibilityKind.WorkspaceBuildSafe,
                    "add_dotnet_project_reference",
                    $"Taskboard auto-run executing canonical project reference item: `{workItem.Title}`. {referenceDecision.DecisionSummary}",
                    referenceDecision.EffectiveProjectPath,
                    ("project_path", referenceDecision.EffectiveProjectPath),
                    ("reference_path", referenceDecision.EffectiveReferencePath),
                    ("reference_decision_summary", referenceDecision.DecisionSummary),
                    ("reference_from", ResolveProjectNameFromPath(referenceDecision.EffectiveProjectPath)),
                    ("reference_to", ResolveProjectNameFromPath(referenceDecision.EffectiveReferencePath)));
                return true;
            }

            case "dotnet.build":
            {
                var targetPath = ResolveBuildTargetFromCanonicalArguments(workspaceRoot, canonicalization);
                targetPath = FirstNonEmpty(targetPath, ResolveWorkspaceBuildTarget(workspaceRoot, workItem, planTitle));
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    result = BuildCanonicalManualOnly(
                        canonicalization,
                        workItem,
                        $"Taskboard auto-run paused: canonical build request for `{workItem.Title}` could not resolve a deterministic workspace build target.");
                    return true;
                }

                result = BuildCanonicalExecutable(
                    canonicalization,
                    TaskboardExecutionEligibilityKind.WorkspaceBuildSafe,
                    "dotnet_build",
                    $"Taskboard auto-run executing canonical build item: `{workItem.Title}`.",
                    targetPath,
                    ("project", targetPath));
                return true;
            }

            case "dotnet.test":
            {
                var targetPath = ResolveTestTargetFromCanonicalArguments(workspaceRoot, canonicalization);
                targetPath = FirstNonEmpty(targetPath, ResolveWorkspaceTestTarget(workspaceRoot, workItem, planTitle));
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    result = BuildCanonicalManualOnly(
                        canonicalization,
                        workItem,
                        $"Taskboard auto-run paused: canonical test request for `{workItem.Title}` could not resolve a deterministic workspace test target.");
                    return true;
                }

                result = BuildCanonicalExecutable(
                    canonicalization,
                    TaskboardExecutionEligibilityKind.WorkspaceTestSafe,
                    "dotnet_test",
                    $"Taskboard auto-run executing canonical test item: `{workItem.Title}`.",
                    targetPath,
                    ("project", targetPath));
                return true;
            }

            case "file.write":
            {
                var targetPath = FirstNonEmpty(
                    NormalizeRelativePath(GetCanonicalArgument(canonicalization, "path")),
                    NormalizeRelativePath(canonicalization.NormalizedTargetPath));
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    result = BuildCanonicalManualOnly(
                        canonicalization,
                        workItem,
                        $"Taskboard auto-run paused: canonical file generation for `{workItem.Title}` did not resolve a deterministic target path.");
                    return true;
                }

                result = BuildCanonicalExecutable(
                    canonicalization,
                    TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
                    "write_file",
                    $"Taskboard auto-run executing canonical file generation item: `{workItem.Title}`.",
                    targetPath,
                    ("path", targetPath));
                return true;
            }
        }

        result = new TaskboardBuilderOperationResolutionResult();
        return false;
    }

    private static TaskboardBuilderOperationResolutionResult BuildExecutable(
        TaskboardExecutionEligibilityKind eligibility,
        string toolName,
        string reason,
        string resolvedTargetPath,
        params (string Key, string Value)[] arguments)
    {
        var result = BuildMatched(eligibility, toolName, reason);
        result.ResolvedTargetPath = NormalizeRelativePath(resolvedTargetPath);
        foreach (var (key, value) in arguments)
        {
            result.Arguments[key] = value;
        }

        return result;
    }

    private static TaskboardBuilderOperationResolutionResult BuildCanonicalExecutable(
        CommandCanonicalizationRecord canonicalization,
        TaskboardExecutionEligibilityKind eligibility,
        string toolName,
        string reason,
        string resolvedTargetPath,
        params (string Key, string Value)[] arguments)
    {
        var result = BuildExecutable(eligibility, toolName, reason, resolvedTargetPath, arguments);
        result.CanonicalOperationKind = canonicalization.NormalizedOperationKind;
        result.CanonicalTargetPath = canonicalization.NormalizedTargetPath;
        result.CanonicalizationTrace = canonicalization.NormalizationTrace;
        foreach (var argument in canonicalization.NormalizedArguments)
        {
            if (string.IsNullOrWhiteSpace(argument.Value))
                continue;

            var targetKey = argument.Key;
            if (result.Arguments.TryGetValue(argument.Key, out var existingValue)
                && !string.Equals(existingValue, argument.Value, StringComparison.OrdinalIgnoreCase))
            {
                targetKey = $"declared_{argument.Key}";
            }

            result.Arguments[targetKey] = argument.Value;
        }
        return result;
    }

    private static TaskboardBuilderOperationResolutionResult BuildCanonicalManualOnly(
        CommandCanonicalizationRecord canonicalization,
        TaskboardRunWorkItem workItem,
        string reason)
    {
        var result = BuildMatched(TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous, "", reason);
        result.CanonicalOperationKind = canonicalization.NormalizedOperationKind;
        result.CanonicalTargetPath = canonicalization.NormalizedTargetPath;
        result.CanonicalizationTrace = canonicalization.NormalizationTrace;
        result.ResolvedTargetPath = NormalizeRelativePath(canonicalization.NormalizedTargetPath);
        return result;
    }

    private static TaskboardBuilderOperationResolutionResult BuildCanonicalBlocked(
        CommandCanonicalizationRecord canonicalization,
        TaskboardRunWorkItem workItem,
        string reason)
    {
        var result = BuildMatched(TaskboardExecutionEligibilityKind.BlockedUnsafe, "", reason);
        result.CanonicalOperationKind = canonicalization.NormalizedOperationKind;
        result.CanonicalTargetPath = canonicalization.NormalizedTargetPath;
        result.CanonicalizationTrace = canonicalization.NormalizationTrace;
        result.ResolvedTargetPath = NormalizeRelativePath(canonicalization.NormalizedTargetPath);
        return result;
    }

    private static TaskboardBuilderOperationResolutionResult BuildMatched(
        TaskboardExecutionEligibilityKind eligibility,
        string toolName,
        string reason)
    {
        return new TaskboardBuilderOperationResolutionResult
        {
            Matched = true,
            Eligibility = eligibility,
            ToolName = toolName,
            Reason = reason
        };
    }

    private static bool PathsResolveToSameWorkspaceTarget(string workspaceRoot, string leftPath, string rightPath)
    {
        if (string.IsNullOrWhiteSpace(leftPath) || string.IsNullOrWhiteSpace(rightPath))
            return false;

        var leftFullPath = Path.GetFullPath(Path.Combine(workspaceRoot, NormalizeRelativePath(leftPath).Replace('/', Path.DirectorySeparatorChar)));
        var rightFullPath = Path.GetFullPath(Path.Combine(workspaceRoot, NormalizeRelativePath(rightPath).Replace('/', Path.DirectorySeparatorChar)));
        return string.Equals(leftFullPath, rightFullPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string DetermineSolutionName(string workspaceRoot, TaskboardRunWorkItem workItem, string planTitle)
    {
        var explicitName = ExtractExplicitName(workItem, "sln");
        if (!string.IsNullOrWhiteSpace(explicitName))
            return SanitizeIdentifier(explicitName);

        var existingSolution = FindWorkspaceFiles(workspaceRoot, "*.sln").FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(existingSolution))
            return Path.GetFileNameWithoutExtension(existingSolution);

        return DeriveBaseName(planTitle, workspaceRoot);
    }

    private static string DetermineProjectName(string workspaceRoot, TaskboardRunWorkItem workItem, string planTitle, string template)
    {
        var explicitName = ExtractExplicitName(workItem, "csproj");
        if (!string.IsNullOrWhiteSpace(explicitName))
            return SanitizeProjectName(explicitName);

        var baseName = DeriveBaseName(planTitle, workspaceRoot);
        var prompt = Normalize(workItem.PromptText);
        var summary = Normalize(workItem.Summary);
        var context = $"{prompt} {summary}".Trim();

        if (template == "xunit")
            return $"{baseName}.Tests";
        if (ContainsAnyPhrase(context, "core", "core project", "core library"))
            return $"{baseName}.Core";
        if (ContainsAnyPhrase(context, "contract", "contracts", "contracts library"))
            return $"{baseName}.Contracts";
        if (ContainsAnyPhrase(context, "storage", "storage project"))
            return $"{baseName}.Storage";
        if (ContainsAnyPhrase(context, "repository", "repository project"))
            return $"{baseName}.Repository";
        if (ContainsAnyPhrase(context, "shared", "shared project"))
            return $"{baseName}.Shared";
        if (ContainsAnyPhrase(context, "services", "services project"))
            return $"{baseName}.Services";
        if (ContainsAnyPhrase(context, "service", "service project"))
            return $"{baseName}.Services";

        return baseName;
    }

    private static string DetermineProjectOutputPath(string projectName, string template)
    {
        var root = DotnetScaffoldSurfaceService.ResolveDefaultProjectRoot(template);
        return NormalizeRelativePath(Path.Combine(root, projectName));
    }

    private static string ResolveProjectTemplate(TaskboardRunWorkItem workItem, string planTitle)
    {
        var context = Normalize($"{workItem.PromptText} {workItem.Summary}");
        if (ContainsAnyPhrase(context,
                "wpf",
                "desktop app",
                "windows app",
                "window app",
                "client project"))
        {
            return "wpf";
        }

        if (ContainsAnyPhrase(context, "winforms", "windows forms"))
        {
            return "winforms";
        }

        if (ContainsAnyPhrase(context, "web api", "webapi"))
        {
            return DotnetScaffoldSurfaceService.NormalizeTemplate("webapi");
        }

        if (ContainsAnyPhrase(context, "worker", "worker service", "background worker"))
        {
            return DotnetScaffoldSurfaceService.NormalizeTemplate("worker");
        }

        if (ContainsAnyPhrase(context,
                "xunit",
                "test project",
                "tests project",
                "tests",
                "scaffold tests"))
        {
            return "xunit";
        }

        if (ContainsAnyPhrase(context,
                "class library",
                "library project",
                "contracts library",
                "core project",
                "services project",
                "service project",
                "storage project",
                "repository project",
                "library"))
        {
            return DotnetScaffoldSurfaceService.NormalizeTemplate("classlib");
        }

        if (ContainsAnyPhrase(context, "console"))
            return DotnetScaffoldSurfaceService.NormalizeTemplate("console");

        if (ContainsAnyPhrase(context, "app project", "application project"))
        {
            if (ContainsAnyPhrase(context, "web api", "webapi", "api"))
                return DotnetScaffoldSurfaceService.NormalizeTemplate("webapi");
            if (ContainsAnyPhrase(context, "worker", "worker service", "background worker"))
                return DotnetScaffoldSurfaceService.NormalizeTemplate("worker");
            if (ContainsAnyPhrase(context, "console"))
                return DotnetScaffoldSurfaceService.NormalizeTemplate("console");
            return DotnetScaffoldSurfaceService.NormalizeTemplate(
                planTitle.Contains("windows", StringComparison.OrdinalIgnoreCase)
                    ? "wpf"
                    : "console");
        }

        return "";
    }

    private static string ResolveSolutionPath(string workspaceRoot, TaskboardRunWorkItem workItem, string planTitle)
    {
        var explicitPath = ExtractExplicitRelativePath(workItem, "sln");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        var solutionCandidates = FindWorkspaceFiles(workspaceRoot, "*.sln");
        if (solutionCandidates.Count == 1)
            return solutionCandidates[0];

        var expected = $"{DetermineSolutionName(workspaceRoot, workItem, planTitle)}.sln";
        if (File.Exists(Path.Combine(workspaceRoot, expected)))
            return NormalizeRelativePath(expected);

        return solutionCandidates.Count == 0 ? NormalizeRelativePath(expected) : "";
    }

    private static string ResolveExistingSolutionPath(string workspaceRoot, TaskboardRunWorkItem workItem, string planTitle)
    {
        var path = ResolveSolutionPath(workspaceRoot, workItem, planTitle);
        return WorkspaceFileExists(workspaceRoot, path) ? path : "";
    }

    private static string ResolveWorkspaceBuildTarget(string workspaceRoot, TaskboardRunWorkItem workItem, string planTitle)
    {
        var requestedTarget = FirstNonEmpty(
            ExtractExplicitRelativePath(workItem, "sln"),
            ExtractExplicitRelativePath(workItem, "csproj"),
            ExtractExplicitName(workItem, "sln"),
            ExtractExplicitName(workItem, "csproj"));
        var resolution = new WorkspaceBuildIndexService().ResolveForBuild(workspaceRoot, requestedTarget, "");
        if (resolution.Success && resolution.Item is not null)
            return resolution.Item.RelativePath;

        var fallback = ResolveExistingSolutionPath(workspaceRoot, workItem, planTitle);
        return FirstNonEmpty(fallback, ResolveExistingProjectPath(workspaceRoot, workItem, planTitle));
    }

    private static string ResolveProjectPath(string workspaceRoot, TaskboardRunWorkItem workItem, string planTitle)
    {
        var explicitPath = ExtractExplicitRelativePath(workItem, "csproj");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        var projectCandidates = FindWorkspaceFiles(workspaceRoot, "*.csproj");
        if (projectCandidates.Count == 1)
            return projectCandidates[0];

        var template = ResolveProjectTemplate(workItem, planTitle);
        if (string.IsNullOrWhiteSpace(template))
            return "";

        var expectedName = DetermineProjectName(workspaceRoot, workItem, planTitle, template);
        var expectedRelativePath = NormalizeRelativePath(Path.Combine(DetermineProjectOutputPath(expectedName, template), $"{expectedName}.csproj"));
        if (File.Exists(Path.Combine(workspaceRoot, expectedRelativePath.Replace('/', Path.DirectorySeparatorChar))))
            return expectedRelativePath;

        return projectCandidates.Count == 0 ? expectedRelativePath : SelectMatchingProjectCandidate(projectCandidates, expectedName);
    }

    private static string ResolveExistingProjectPath(string workspaceRoot, TaskboardRunWorkItem workItem, string planTitle)
    {
        var path = ResolveProjectPath(workspaceRoot, workItem, planTitle);
        return WorkspaceFileExists(workspaceRoot, path) ? path : "";
    }

    private static string ResolveWorkspaceTestTarget(string workspaceRoot, TaskboardRunWorkItem workItem, string planTitle)
    {
        var requestedTarget = FirstNonEmpty(
            ExtractExplicitRelativePath(workItem, "csproj"),
            ExtractExplicitRelativePath(workItem, "sln"),
            ExtractExplicitName(workItem, "csproj"),
            ExtractExplicitName(workItem, "sln"));
        var resolution = new WorkspaceBuildIndexService().ResolveForTesting(workspaceRoot, requestedTarget, "");
        if (resolution.Success && resolution.Item is not null)
            return resolution.Item.RelativePath;

        if (!string.IsNullOrWhiteSpace(requestedTarget))
            return "";

        var projectCandidates = FindWorkspaceFiles(workspaceRoot, "*.csproj");
        var fileIdentityService = new FileIdentityService();
        return projectCandidates
            .FirstOrDefault(path => string.Equals(fileIdentityService.Identify(path).Role, "tests", StringComparison.OrdinalIgnoreCase))
            ?? ResolveExistingProjectPath(workspaceRoot, workItem, planTitle);
    }

    private static string ResolvePrimaryProjectPath(IReadOnlyList<string> projectCandidates, TaskboardRunWorkItem workItem, string planTitle)
    {
        var fileIdentityService = new FileIdentityService();
        var context = $"{workItem.PromptText} {workItem.Summary}".ToLowerInvariant();
        if (ContainsAnyPhrase(context, "test", "tests"))
        {
            return projectCandidates.FirstOrDefault(path => string.Equals(fileIdentityService.Identify(path).Role, "tests", StringComparison.OrdinalIgnoreCase))
                ?? "";
        }

        if (ContainsAnyPhrase(context, "app", "ui"))
        {
            var baseName = DeriveBaseName(planTitle, "");
            return projectCandidates.FirstOrDefault(path =>
                       string.Equals(fileIdentityService.Identify(path).ProjectName, baseName, StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(fileIdentityService.Identify(path).Role, "tests", StringComparison.OrdinalIgnoreCase))
                   ?? projectCandidates.FirstOrDefault(path => path.Contains("app", StringComparison.OrdinalIgnoreCase))
                   ?? "";
        }

        return projectCandidates.FirstOrDefault(path => !string.Equals(fileIdentityService.Identify(path).Role, "tests", StringComparison.OrdinalIgnoreCase))
               ?? projectCandidates.FirstOrDefault()
               ?? "";
    }

    private static string ResolveReferencedProjectPath(
        IReadOnlyList<string> projectCandidates,
        TaskboardRunWorkItem workItem,
        string planTitle,
        string sourceProject)
    {
        var fileIdentityService = new FileIdentityService();
        var context = $"{workItem.PromptText} {workItem.Summary}".ToLowerInvariant();
        if (ContainsAnyPhrase(context, "core"))
        {
            return projectCandidates.FirstOrDefault(path =>
                       string.Equals(fileIdentityService.Identify(path).Role, "core", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(path, sourceProject, StringComparison.OrdinalIgnoreCase))
                   ?? "";
        }

        if (ContainsAnyPhrase(context, "services", "service"))
        {
            return projectCandidates.FirstOrDefault(path =>
                       string.Equals(fileIdentityService.Identify(path).Role, "services", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(path, sourceProject, StringComparison.OrdinalIgnoreCase))
                   ?? projectCandidates.FirstOrDefault(path =>
                       Path.GetFileNameWithoutExtension(path).EndsWith(".Services", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(path, sourceProject, StringComparison.OrdinalIgnoreCase))
                   ?? "";
        }

        if (ContainsAnyPhrase(context, "storage"))
        {
            return projectCandidates.FirstOrDefault(path =>
                       string.Equals(fileIdentityService.Identify(path).Role, "storage", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(path, sourceProject, StringComparison.OrdinalIgnoreCase))
                   ?? projectCandidates.FirstOrDefault(path =>
                       Path.GetFileNameWithoutExtension(path).EndsWith(".Storage", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(path, sourceProject, StringComparison.OrdinalIgnoreCase))
                   ?? "";
        }

        if (ContainsAnyPhrase(context, "shared"))
        {
            return projectCandidates.FirstOrDefault(path =>
                       path.Contains("shared", StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(path, sourceProject, StringComparison.OrdinalIgnoreCase))
                   ?? "";
        }

        var baseName = DeriveBaseName(planTitle, "");
        var preferred = projectCandidates.FirstOrDefault(path =>
            string.Equals(fileIdentityService.Identify(path).ProjectName, $"{baseName}.Core", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(path, sourceProject, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(preferred))
            return preferred;

        return projectCandidates.FirstOrDefault(path => !string.Equals(path, sourceProject, StringComparison.OrdinalIgnoreCase))
            ?? "";
    }

    private static string ResolveStorageDirectory(string workspaceRoot, TaskboardRunWorkItem workItem, string planTitle)
    {
        var explicitStorageProject = FindWorkspaceFiles(workspaceRoot, "*.Storage.csproj").FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(explicitStorageProject))
        {
            return NormalizeRelativePath(Path.GetDirectoryName(explicitStorageProject)?.Replace('\\', Path.DirectorySeparatorChar) ?? "");
        }

        var projectPath = ResolveExistingProjectPath(workspaceRoot, workItem, planTitle);
        if (string.IsNullOrWhiteSpace(projectPath))
            return "";

        var projectDirectory = Path.GetDirectoryName(projectPath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        return string.IsNullOrWhiteSpace(projectDirectory)
            ? ""
            : NormalizeRelativePath(Path.Combine(projectDirectory, "Storage"));
    }

    private static bool TryResolveStorageWriteTarget(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle,
        string fileName,
        out string targetPath,
        out string projectName)
    {
        targetPath = "";
        projectName = "";
        var storageDirectory = ResolveStorageDirectory(workspaceRoot, workItem, planTitle);
        if (string.IsNullOrWhiteSpace(storageDirectory))
            return false;

        projectName = ResolveStorageProjectName(workspaceRoot, workItem, planTitle);
        if (string.IsNullOrWhiteSpace(projectName))
            return false;

        targetPath = NormalizeRelativePath(Path.Combine(storageDirectory, fileName));
        return !string.IsNullOrWhiteSpace(targetPath);
    }

    private static string ResolveStorageProjectName(string workspaceRoot, TaskboardRunWorkItem workItem, string planTitle)
    {
        var explicitStorageProject = FindWorkspaceFiles(workspaceRoot, "*.Storage.csproj").FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(explicitStorageProject))
            return Path.GetFileNameWithoutExtension(explicitStorageProject);

        var projectPath = ResolveExistingProjectPath(workspaceRoot, workItem, planTitle);
        if (string.IsNullOrWhiteSpace(projectPath))
            return "";

        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        return string.IsNullOrWhiteSpace(projectName)
            ? ""
            : $"{projectName}.Storage";
    }

    private static bool TryResolveExplicitSolutionAttachTargets(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle,
        out string solutionPath,
        out string projectPath)
    {
        solutionPath = "";
        projectPath = "";
        var solutionCandidates = FindWorkspaceFiles(workspaceRoot, "*.sln");
        var projectCandidates = FindWorkspaceFiles(workspaceRoot, "*.csproj");
        foreach (var source in EnumerateSources(workItem))
        {
            var match = ExplicitProjectToSolutionPattern.Match(source);
            if (!match.Success)
                continue;

            solutionPath = ResolveSolutionCandidateByName(solutionCandidates, match.Groups["solution"].Value, planTitle);
            projectPath = ResolveProjectCandidateByName(projectCandidates, match.Groups["project"].Value);
            return !string.IsNullOrWhiteSpace(solutionPath) && !string.IsNullOrWhiteSpace(projectPath);
        }

        return false;
    }

    private static bool TryResolveExplicitProjectReferenceTargets(
        IReadOnlyList<string> projectCandidates,
        TaskboardRunWorkItem workItem,
        out string sourceProject,
        out string referencedProject)
    {
        sourceProject = "";
        referencedProject = "";
        foreach (var source in EnumerateSources(workItem))
        {
            var match = ExplicitReferenceFromToPattern.Match(source);
            if (!match.Success)
                match = ExplicitReferencePairPattern.Match(source);
            if (!match.Success)
                continue;

            sourceProject = ResolveProjectCandidateByName(projectCandidates, match.Groups["source"].Value);
            referencedProject = ResolveProjectCandidateByName(projectCandidates, match.Groups["target"].Value);
            return !string.IsNullOrWhiteSpace(sourceProject) && !string.IsNullOrWhiteSpace(referencedProject);
        }

        return false;
    }

    private static string ResolveSolutionCandidateByName(IReadOnlyList<string> solutionCandidates, string value, string planTitle)
    {
        if (solutionCandidates.Count == 0)
            return "";

        var normalized = NormalizeSolutionName(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        return solutionCandidates.FirstOrDefault(path =>
                   string.Equals(Path.GetFileNameWithoutExtension(path), normalized, StringComparison.OrdinalIgnoreCase))
               ?? solutionCandidates.FirstOrDefault(path =>
                   string.Equals(path, $"{normalized}.sln", StringComparison.OrdinalIgnoreCase))
               ?? "";
    }

    private static string ResolveProjectCandidateByName(IReadOnlyList<string> projectCandidates, string value)
    {
        if (projectCandidates.Count == 0)
            return "";

        var normalized = SanitizeProjectName(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        var fileIdentityService = new FileIdentityService();
        return projectCandidates.FirstOrDefault(path =>
                   string.Equals(Path.GetFileNameWithoutExtension(path), normalized, StringComparison.OrdinalIgnoreCase))
               ?? projectCandidates.FirstOrDefault(path =>
                   string.Equals(fileIdentityService.Identify(path).ProjectName, normalized, StringComparison.OrdinalIgnoreCase))
               ?? "";
    }

    private static string NormalizeSolutionName(string value)
    {
        var cleaned = CleanIdentifier(value);
        if (cleaned.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            cleaned = Path.GetFileNameWithoutExtension(cleaned);

        return SanitizeProjectName(cleaned);
    }

    private static string ExtractExplicitName(TaskboardRunWorkItem workItem, string extension)
    {
        foreach (var source in EnumerateSources(workItem))
        {
            var fileMatch = ExplicitFileNamePattern.Match(source);
            if (fileMatch.Success
                && string.Equals(fileMatch.Groups["ext"].Value, extension, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileNameWithoutExtension(fileMatch.Groups["name"].Value.Replace('\\', '/'));
            }

            var namedMatch = ExplicitNamePattern.Match(source);
            if (namedMatch.Success)
                return CleanIdentifier(namedMatch.Groups["name"].Value);

            var quotedMatch = QuotedNamePattern.Match(source);
            if (quotedMatch.Success)
                return CleanIdentifier(quotedMatch.Groups["name"].Value);
        }

        return "";
    }

    private static string ExtractExplicitRelativePath(TaskboardRunWorkItem workItem, string extension)
    {
        foreach (var source in EnumerateSources(workItem))
        {
            var fileMatch = ExplicitFileNamePattern.Match(source);
            if (fileMatch.Success
                && string.Equals(fileMatch.Groups["ext"].Value, extension, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeRelativePath($"{CleanRelativePath(fileMatch.Groups["name"].Value)}.{extension}");
            }
        }

        return "";
    }

    private static IEnumerable<string> EnumerateSources(TaskboardRunWorkItem workItem)
    {
        if (!string.IsNullOrWhiteSpace(workItem.PromptText))
            yield return workItem.PromptText;
        if (!string.IsNullOrWhiteSpace(workItem.Summary))
            yield return workItem.Summary;
        if (!string.IsNullOrWhiteSpace(workItem.Title))
            yield return workItem.Title;
    }

    private static string DeriveBaseName(string planTitle, string workspaceRoot)
    {
        var source = CleanIdentifier(planTitle);
        if (source.Contains(':'))
            source = source[(source.LastIndexOf(':') + 1)..].Trim();

        source = Regex.Replace(source, @"^\s*phase\s+\d+(\.\d+)?\s*[-:]\s*", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        source = Regex.Replace(source, @"\b(taskboard|starter|phase)\b", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        if (string.IsNullOrWhiteSpace(source))
            source = Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var words = Regex.Matches(source, @"[A-Za-z0-9]+")
            .Select(match => match.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        if (words.Count == 0)
            return "WorkspaceApp";

        return string.Concat(words.Select(Capitalize));
    }

    private static List<string> FindWorkspaceFiles(string workspaceRoot, string searchPattern)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return [];

        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(workspaceRoot);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (IsIgnoredDirectory(directory))
                    continue;

                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current, searchPattern))
            {
                files.Add(NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, file)));
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static string SelectMatchingProjectCandidate(IEnumerable<string> candidates, string expectedName)
    {
        return candidates.FirstOrDefault(path =>
                   Path.GetFileNameWithoutExtension(path).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
               ?? "";
    }

    private static bool WorkspaceFileExists(string workspaceRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(relativePath))
            return false;

        var fullPath = Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(fullPath);
    }

    private static bool IsIgnoredDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, ".ram", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string text, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (text.Contains(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string Normalize(string value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        normalized = normalized.Replace('\\', '/');
        normalized = normalized.Replace("c++", "cpp");
        normalized = normalized.Replace("c#", "csharp");
        normalized = Regex.Replace(normalized, @"[""']", " ");
        normalized = Regex.Replace(normalized, @"[,:;!?\(\)\[\]\{\}]", " ");
        normalized = Regex.Replace(normalized, @"\b(?:the|a|an|please|now)\b", " ");
        normalized = Regex.Replace(normalized, @"\bnew\b", " create ");
        normalized = Regex.Replace(normalized, "\\s+", " ");
        return normalized.Trim();
    }

    private static string NormalizeRelativePath(string path)
    {
        return (path ?? "").Replace('\\', '/');
    }

    private static string CleanIdentifier(string value)
    {
        var cleaned = (value ?? "").Trim().Trim('"', '\'').TrimEnd('.', ',', ';', ':', '!', '?');
        cleaned = cleaned.Replace("TASKBOARD", "", StringComparison.OrdinalIgnoreCase).Trim();
        return cleaned;
    }

    private static string CleanRelativePath(string value)
    {
        var cleaned = CleanIdentifier(value)
            .Replace('\\', '/');
        cleaned = Regex.Replace(cleaned, @"[^\w./-]", "");
        return cleaned.Trim('/');
    }

    private static string SanitizeIdentifier(string value)
    {
        var words = Regex.Matches(CleanIdentifier(value), @"[A-Za-z0-9]+")
            .Select(match => match.Value)
            .Where(valuePart => !string.IsNullOrWhiteSpace(valuePart))
            .ToList();
        return words.Count == 0 ? "WorkspaceApp" : string.Concat(words.Select(Capitalize));
    }

    private static string SanitizeProjectName(string value)
    {
        var cleaned = CleanIdentifier(value)
            .Replace('\\', '.')
            .Replace('/', '.')
            .Replace(' ', '.');
        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeIdentifier)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
        return parts.Count == 0 ? "WorkspaceApp" : string.Join(".", parts);
    }

    private static string Capitalize(string value)
    {
        return value.Length == 0
            ? ""
            : char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string BuildISettingsStoreCs(string projectName)
    {
        return $$"""
namespace {{projectName}}.Storage;

public interface ISettingsStore
{
    string Load();
    void Save(string json);
}
""";
    }

    private static string BuildFileSettingsStoreCs(string projectName)
    {
        return $$"""
using System.IO;

namespace {{projectName}}.Storage;

public sealed class FileSettingsStore : ISettingsStore
{
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    public string Load()
    {
        return File.Exists(_settingsPath) ? File.ReadAllText(_settingsPath) : "{}";
    }

    public void Save(string json)
    {
        File.WriteAllText(_settingsPath, json ?? "{}");
    }
}
""";
    }

    private static string BuildSnapshotRepositoryContractCs(string projectName)
    {
        return $$"""
namespace {{projectName}}.Storage;

public interface ISnapshotRepository
{
    string LoadSnapshotJson();
    void SaveSnapshotJson(string json);
}
""";
    }

    private static string BuildSnapshotRepositoryImplCs(string projectName)
    {
        return $$"""
using System.IO;

namespace {{projectName}}.Storage;

public sealed class SqliteSnapshotRepository : ISnapshotRepository
{
    private readonly string _snapshotPath = Path.Combine(AppContext.BaseDirectory, "snapshot-cache.json");

    public string LoadSnapshotJson()
    {
        return File.Exists(_snapshotPath) ? File.ReadAllText(_snapshotPath) : "[]";
    }

    public void SaveSnapshotJson(string json)
    {
        File.WriteAllText(_snapshotPath, json ?? "[]");
    }
}
""";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static bool LooksLikeCreateSolutionCommand(string prompt)
    {
        return ContainsAnyPhrase(
            prompt,
            "create solution",
            "create dotnet solution",
            "scaffold solution",
            "initialize solution",
            "make solution",
            "create sln",
            "setup solution");
    }

    private static bool LooksLikeCreateProjectCommand(string prompt)
    {
        return ContainsAnyPhrase(
            prompt,
            "create dotnet project",
            "create project",
            "create app project",
            "create wpf project",
            "create console project",
            "create console app",
            "create worker project",
            "create worker service",
            "create web api",
            "create webapi",
            "create api project",
            "create desktop app project",
            "scaffold wpf app",
            "create window app",
            "create client project",
            "make app project",
            "create class library",
            "create core project",
            "create contracts library",
            "create storage project",
            "create repository project",
            "create xunit project",
            "create test project",
            "scaffold tests",
            "add test project");
    }

    private static bool LooksLikeAddProjectToSolutionCommand(string prompt)
    {
        return ContainsAnyPhrase(
            prompt,
            "add project to solution",
            "attach project to solution",
            "include project in solution",
            "wire project into solution",
            "register project in solution",
            "add app project to solution",
            "add test project to solution");
    }

    private static bool LooksLikeAddProjectReferenceCommand(string prompt)
    {
        return ContainsAnyPhrase(
            prompt,
            "add project reference",
            "add reference from",
            "reference core library from app",
            "attach reference",
            "add dependency reference",
            "wire project reference",
            "add app reference to core");
    }

    private static bool LooksLikeBuildCommand(string prompt)
    {
        return ContainsAnyPhrase(
            prompt,
            "build",
            "compile",
            "restore",
            "run dotnet build",
            "build solution",
            "verify build",
            "validate build",
            "run workspace build verification",
            "rerun build",
            "check build",
            "ensure solution builds");
    }

    private static bool LooksLikeTestCommand(string prompt)
    {
        return ContainsAnyPhrase(
            prompt,
            "test",
            "tests",
            "run dotnet test",
            "run test project",
            "verify tests",
            "validate tests",
            "rerun tests",
            "execute tests",
            "run solution tests",
            "run direct test target");
    }

    private static bool LooksLikeWorkspaceBuildRequest(string prompt)
    {
        return ContainsAnyPhrase(
            prompt,
            "solution",
            "project",
            "workspace build",
            "current solution",
            "current project",
            "verify build",
            "validate build",
            "ensure solution builds",
            "run dotnet build");
    }

    private static bool LooksLikeWorkspaceTestRequest(string prompt)
    {
        return ContainsAnyPhrase(
            prompt,
            "solution",
            "project",
            "run tests",
            "run test",
            "verify tests",
            "validate tests",
            "current project",
            "current solution",
            "direct test target");
    }

    private static string GetCanonicalArgument(CommandCanonicalizationRecord canonicalization, string key)
    {
        if (canonicalization?.NormalizedArguments is null)
            return "";

        return canonicalization.NormalizedArguments.TryGetValue(key, out var value)
            ? value
            : "";
    }

    private static string ResolveSolutionPathFromCanonicalArguments(CommandCanonicalizationRecord canonicalization)
    {
        var explicitSolution = GetCanonicalArgument(canonicalization, "solution");
        if (!string.IsNullOrWhiteSpace(explicitSolution))
            return EnsureSolutionPath(explicitSolution);

        var explicitPath = GetCanonicalArgument(canonicalization, "path");
        if (!string.IsNullOrWhiteSpace(explicitPath) && explicitPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return NormalizeRelativePath(explicitPath);

        return "";
    }

    private static string ResolveProjectPathFromCanonicalArguments(IReadOnlyList<string> projectCandidates, CommandCanonicalizationRecord canonicalization)
    {
        var explicitPath = GetCanonicalArgument(canonicalization, "path");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var normalizedPath = NormalizeRelativePath(explicitPath);
            if (normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                return normalizedPath;
        }

        var projectName = FirstNonEmpty(
            GetCanonicalArgument(canonicalization, "project"),
            GetCanonicalArgument(canonicalization, "name"),
            canonicalization.NormalizedProjectName);
        return ResolveProjectPathByName(projectCandidates, projectName);
    }

    private static bool TryResolveProjectReferenceTargetsFromCanonicalArguments(
        IReadOnlyList<string> projectCandidates,
        CommandCanonicalizationRecord canonicalization,
        out string sourceProject,
        out string referencedProject)
    {
        sourceProject = ResolveProjectPathByName(projectCandidates, GetCanonicalArgument(canonicalization, "reference_from"));
        referencedProject = ResolveProjectPathByName(projectCandidates, GetCanonicalArgument(canonicalization, "reference_to"));
        return !string.IsNullOrWhiteSpace(sourceProject) && !string.IsNullOrWhiteSpace(referencedProject);
    }

    private static string ResolveBuildTargetFromCanonicalArguments(string workspaceRoot, CommandCanonicalizationRecord canonicalization)
    {
        var requestedTarget = FirstNonEmpty(
            GetCanonicalArgument(canonicalization, "validation"),
            GetCanonicalArgument(canonicalization, "path"),
            canonicalization.NormalizedTargetPath);
        if (string.IsNullOrWhiteSpace(requestedTarget))
            return "";

        var resolution = new WorkspaceBuildIndexService().ResolveForBuild(workspaceRoot, requestedTarget, "");
        return resolution.Success && resolution.Item is not null
            ? resolution.Item.RelativePath
            : "";
    }

    private static string ResolveTestTargetFromCanonicalArguments(string workspaceRoot, CommandCanonicalizationRecord canonicalization)
    {
        var requestedTarget = FirstNonEmpty(
            GetCanonicalArgument(canonicalization, "validation"),
            GetCanonicalArgument(canonicalization, "path"),
            canonicalization.NormalizedTargetPath);
        if (string.IsNullOrWhiteSpace(requestedTarget))
            return "";

        var resolution = new WorkspaceBuildIndexService().ResolveForTesting(workspaceRoot, requestedTarget, "");
        return resolution.Success && resolution.Item is not null
            ? resolution.Item.RelativePath
            : "";
    }

    private static string DetermineProjectOutputPathFromArguments(string projectName, string template, string explicitPath)
    {
        if (string.IsNullOrWhiteSpace(explicitPath))
            return DetermineProjectOutputPath(projectName, template);

        var normalizedPath = NormalizeRelativePath(explicitPath);
        if (normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return NormalizeRelativePath(Path.GetDirectoryName(normalizedPath.Replace('/', Path.DirectorySeparatorChar)) ?? "");

        return normalizedPath;
    }

    private static string ResolveProjectTargetPathFromArguments(string projectName, string outputPath, string explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && explicitPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return NormalizeRelativePath(explicitPath);

        return NormalizeRelativePath(Path.Combine(outputPath, $"{projectName}.csproj"));
    }

    private static string ResolveProjectPathByName(IReadOnlyList<string> projectCandidates, string requestedProject)
    {
        if (string.IsNullOrWhiteSpace(requestedProject))
            return "";

        var normalizedRequested = NormalizeRelativePath(requestedProject);
        if (normalizedRequested.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return normalizedRequested;

        var sanitizedRequested = SanitizeProjectName(requestedProject);
        if (string.IsNullOrWhiteSpace(sanitizedRequested))
            return "";

        foreach (var candidate in projectCandidates)
        {
            var candidateName = Path.GetFileNameWithoutExtension(candidate);
            if (string.Equals(candidateName, sanitizedRequested, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        var expectedSrcPath = NormalizeRelativePath(Path.Combine("src", sanitizedRequested, $"{sanitizedRequested}.csproj"));
        if (projectCandidates.Contains(expectedSrcPath, StringComparer.OrdinalIgnoreCase))
            return expectedSrcPath;

        var expectedTestPath = NormalizeRelativePath(Path.Combine("tests", sanitizedRequested, $"{sanitizedRequested}.csproj"));
        return projectCandidates.Contains(expectedTestPath, StringComparer.OrdinalIgnoreCase)
            ? expectedTestPath
            : "";
    }

    private static string ResolveProjectNameFromPath(string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? ""
            : Path.GetFileNameWithoutExtension(path.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string EnsureSolutionPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = NormalizeRelativePath(value);
        return normalized.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"{TrimSolutionExtension(normalized)}.sln";
    }

    private static string TrimSolutionExtension(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return value.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(value)
            : value;
    }

    private static bool ContainsAnyPhrase(string text, params string[] phrases)
    {
        foreach (var phrase in phrases)
        {
            if (ContainsPhrase(text, phrase))
                return true;
        }

        return false;
    }

    private static bool ContainsPhrase(string text, string phrase)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(phrase))
            return false;

        var tokens = Normalize(phrase)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return false;

        return ContainsOrderedTokens(text, tokens);
    }

    private static bool ContainsOrderedTokens(string text, IReadOnlyList<string> tokens)
    {
        var searchStart = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var relativeMatch = Regex.Match(text[searchStart..], $@"\b{Regex.Escape(token)}\b", RegexOptions.CultureInvariant);
            if (!relativeMatch.Success)
                return false;

            searchStart += relativeMatch.Index + relativeMatch.Length;
        }

        return true;
    }
}
