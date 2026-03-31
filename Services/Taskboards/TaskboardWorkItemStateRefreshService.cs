using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardWorkItemStateRefreshService
{
    public const string ResolverContractVersion = "taskboard_work_item_refresh.v2";

    private static readonly CommandCanonicalizationService CommandCanonicalizationService = new();
    private static readonly FileIdentityService FileIdentityService = new();

    public void Refresh(TaskboardWorkItemRunStateRecord item)
    {
        if (item is null)
            return;

        ApplyCanonicalCommandTruth(item);
        NormalizePlainSiblingProjectRouting(item);
        NormalizeDomainContractsRouting(item);
        NormalizeTestSupportWriteRouting(item);

        if (IsMissingOrUnknown(item.OperationKind))
        {
            item.OperationKind = FirstMeaningful(
                item.LastExecutionGoalResolution.OperationKind,
                item.LastExecutionGoalResolution.LaneResolution.OperationKind);
        }

        if (IsMissingOrUnknown(item.TargetStack))
        {
            item.TargetStack = FirstMeaningful(
                item.LastExecutionGoalResolution.TargetStack,
                item.LastExecutionGoalResolution.LaneResolution.TargetStack);
        }

        if (IsMissingOrUnknown(item.PhraseFamily))
        {
            item.PhraseFamily = FirstMeaningful(
                item.LastExecutionGoalResolution.PhraseFamily,
                item.LastExecutionGoalResolution.LaneResolution.PhraseFamily);
        }

        if (IsMissingOrUnknown(item.TemplateId))
        {
            item.TemplateId = FirstMeaningful(
                item.LastExecutionGoalResolution.TemplateId,
                item.LastExecutionGoalResolution.LaneResolution.TemplateId);
        }

        if ((item.TemplateCandidateIds is null || item.TemplateCandidateIds.Count == 0)
            && item.LastExecutionGoalResolution.TemplateCandidateIds.Count > 0)
        {
            item.TemplateCandidateIds = [.. item.LastExecutionGoalResolution.TemplateCandidateIds];
        }

        ApplyToolRequestFallback(item);
        ApplyCanonicalCommandTruth(item);
        NormalizePlainSiblingProjectRouting(item);
        NormalizeDomainContractsRouting(item);
    }

    public void Refresh(
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord item)
    {
        Refresh(item);
        if (item is null)
            return;

        var sourceItem = ResolveSourceWorkItem(runState, item);
        var contextText = BuildContextText(runState, batch, item, sourceItem);

        SealGenericFollowupTruth(runState, item, sourceItem, contextText);
        ApplyCanonicalCommandTruth(item);

        if (IsMissingOrUnknown(item.TargetStack))
            item.TargetStack = InferStackFromContext(runState, item, sourceItem, contextText);

        if (IsMissingOrUnknown(item.OperationKind))
            item.OperationKind = InferOperationKindFromContext(runState, item, sourceItem, contextText);

        if (IsMissingOrUnknown(item.WorkFamily))
            item.WorkFamily = FirstMeaningful(InferWorkFamily(item), InferWorkFamilyFromContext(runState, item, sourceItem, contextText));

        if (IsMissingOrUnknown(item.PhraseFamily))
            item.PhraseFamily = FirstMeaningful(
                InferPhraseFamily(item, NormalizeValue(item.DirectToolRequest?.ToolName)),
                InferPhraseFamilyFromContext(runState, item, sourceItem, contextText));

        if (IsMissingOrUnknown(item.OperationKind))
            item.OperationKind = InferOperationKindFromContext(runState, item, sourceItem, contextText);

        if (IsMissingOrUnknown(item.WorkFamily))
            item.WorkFamily = FirstMeaningful(InferWorkFamily(item), InferWorkFamilyFromContext(runState, item, sourceItem, contextText));

        if (IsMissingOrUnknown(item.PhraseFamily))
            item.PhraseFamily = FirstMeaningful(
                InferPhraseFamily(item, NormalizeValue(item.DirectToolRequest?.ToolName)),
                InferPhraseFamilyFromContext(runState, item, sourceItem, contextText));

        if (IsMissingOrUnknown(item.TemplateId))
            item.TemplateId = InferTemplateId(item);

        NormalizePlainSiblingProjectRouting(item);
        NormalizeDomainContractsRouting(item);

        if ((item.TemplateCandidateIds is null || item.TemplateCandidateIds.Count == 0)
            && !IsMissingOrUnknown(item.TemplateId))
        {
            item.TemplateCandidateIds = [item.TemplateId];
        }
    }

    private static void ApplyCanonicalCommandTruth(TaskboardWorkItemRunStateRecord item)
    {
        if (item is null
            || IsGenericFollowupTitle(item.Title))
        {
            return;
        }

        var rawPhrase = FirstMeaningful(item.PromptText, item.Title, item.Summary);
        if (string.IsNullOrWhiteSpace(rawPhrase))
            return;

        var canonicalization = CommandCanonicalizationService.Canonicalize(
            rawPhrase,
            workItemId: item.WorkItemId,
            workItemTitle: item.Title);
        var canonicalOperation = NormalizeValue(canonicalization.NormalizedOperationKind);
        if (string.IsNullOrWhiteSpace(canonicalOperation))
            return;

        var mappedOperationKind = MapCanonicalOperationKindToRuntimeOperation(item, canonicalization, rawPhrase);
        if (string.IsNullOrWhiteSpace(mappedOperationKind))
            return;

        var shouldOverrideOperation = ShouldPreferCanonicalOperation(item, mappedOperationKind, canonicalization);
        if (shouldOverrideOperation)
            item.OperationKind = mappedOperationKind;

        if (IsMissingOrUnknown(item.TargetStack)
            && canonicalOperation.StartsWith("dotnet.", StringComparison.OrdinalIgnoreCase))
        {
            item.TargetStack = "dotnet_desktop";
        }

        var canonicalWorkFamily = ResolveCanonicalWorkFamily(mappedOperationKind);
        if ((!string.IsNullOrWhiteSpace(canonicalWorkFamily) && shouldOverrideOperation)
            || IsMissingOrUnknown(item.WorkFamily))
        {
            item.WorkFamily = FirstMeaningful(canonicalWorkFamily, item.WorkFamily);
        }

        var canonicalPhraseFamily = ResolveCanonicalPhraseFamily(mappedOperationKind, item.WorkFamily);
        if ((!string.IsNullOrWhiteSpace(canonicalPhraseFamily) && shouldOverrideOperation)
            || IsMissingOrUnknown(item.PhraseFamily))
        {
            item.PhraseFamily = FirstMeaningful(canonicalPhraseFamily, item.PhraseFamily);
        }

        if (shouldOverrideOperation || IsMissingOrUnknown(item.TemplateId))
        {
            var inferredTemplateId = InferTemplateId(item);
            if (!string.IsNullOrWhiteSpace(inferredTemplateId))
                item.TemplateId = inferredTemplateId;
        }

        if ((item.TemplateCandidateIds is null || item.TemplateCandidateIds.Count == 0)
            && !IsMissingOrUnknown(item.TemplateId))
        {
            item.TemplateCandidateIds = [item.TemplateId];
        }
    }

    private static bool ShouldPreferCanonicalOperation(
        TaskboardWorkItemRunStateRecord item,
        string mappedOperationKind,
        CommandCanonicalizationRecord canonicalization)
    {
        var currentOperation = NormalizeValue(item.OperationKind);
        if (IsMissingOrUnknown(currentOperation))
            return true;

        if (string.Equals(currentOperation, mappedOperationKind, StringComparison.OrdinalIgnoreCase))
            return false;

        if (IsContextFallbackOperation(currentOperation))
            return true;

        var canonicalOperation = NormalizeValue(canonicalization.NormalizedOperationKind);
        if (canonicalOperation.StartsWith("filesystem.", StringComparison.OrdinalIgnoreCase))
            return true;

        if (canonicalOperation.StartsWith("dotnet.create_", StringComparison.OrdinalIgnoreCase)
            || canonicalOperation is "dotnet.add_project_to_solution" or "dotnet.add_project_reference")
        {
            return true;
        }

        if (currentOperation is "create_project" or "create_test_project" or "create_core_library"
            or "add_project_to_solution" or "attach_test_project" or "attach_core_library"
            or "add_project_reference" or "add_domain_reference")
        {
            return true;
        }

        return false;
    }

    private static bool IsContextFallbackOperation(string operationKind)
    {
        return NormalizeValue(operationKind) switch
        {
            "build_solution" or "build_native_workspace" or "run_test_project" or "create_project" or "add_project_to_solution" or "add_project_reference" => true,
            _ => false
        };
    }

    private static string MapCanonicalOperationKindToRuntimeOperation(
        TaskboardWorkItemRunStateRecord item,
        CommandCanonicalizationRecord canonicalization,
        string rawPhrase)
    {
        var canonicalOperation = NormalizeValue(canonicalization.NormalizedOperationKind);
        var roleHint = NormalizeValue(canonicalization.TargetRoleHint);
        var projectName = NormalizeValue(canonicalization.NormalizedProjectName);
        var fileIdentity = FileIdentityService.Identify(canonicalization.NormalizedTargetPath);
        var effectiveRole = FirstMeaningful(fileIdentity.Role, roleHint);
        var combinedHint = $"{rawPhrase} {canonicalization.NormalizedTargetPath}".Trim();

        return canonicalOperation switch
        {
            "filesystem.create_directory" => effectiveRole switch
            {
                "state" => "make_state_dir",
                "storage" => "make_storage_dir",
                "contracts" => "make_contracts_dir",
                "models" => "make_models_dir",
                _ => "make_dir"
            },
            "dotnet.create_solution" => "create_solution",
            "dotnet.create_project.xunit" => LooksLikePlainSiblingProjectScaffold(combinedHint)
                ? "create_project"
                : "create_test_project",
            "dotnet.create_project.classlib" => LooksLikePlainSiblingProjectScaffold(combinedHint)
                ? "create_project"
                : effectiveRole switch
                {
                    "core" or "contracts" or "storage" or "repository" => "create_core_library",
                    _ when LooksLikeSecondaryLibrary(projectName) => "create_core_library",
                    _ => "create_project"
                },
            "dotnet.create_project.wpf" or "dotnet.create_project.console" or "dotnet.create_project.worker" or "dotnet.create_project.webapi" or "dotnet.create_project" => "create_project",
            "dotnet.add_project_to_solution" => LooksLikePlainSiblingProjectScaffold(combinedHint)
                ? "add_project_to_solution"
                : effectiveRole switch
                {
                    "tests" => "attach_test_project",
                    "core" or "contracts" or "storage" or "repository" => "attach_core_library",
                    _ when LooksLikeSecondaryLibrary(projectName) => "attach_core_library",
                    _ => "add_project_to_solution"
                },
            "dotnet.add_project_reference" => LooksLikePlainSiblingProjectReference(combinedHint)
                ? "add_project_reference"
                : ContainsAny(combinedHint, ".core", " core ", "contracts", "storage")
                ? "add_domain_reference"
                : "add_project_reference",
            "dotnet.build" => "build_solution",
            "dotnet.test" => "run_test_project",
            "file.write" => InferWriteFileOperation(combinedHint),
            _ => ""
        };
    }

    private static bool LooksLikeSecondaryLibrary(string projectName)
    {
        return NormalizeValue(projectName) switch
        {
            var value when value.EndsWith(".Core", StringComparison.OrdinalIgnoreCase) => true,
            var value when value.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase) => true,
            var value when value.EndsWith(".Storage", StringComparison.OrdinalIgnoreCase) => true,
            var value when value.EndsWith(".Services", StringComparison.OrdinalIgnoreCase) => true,
            var value when value.EndsWith(".Repository", StringComparison.OrdinalIgnoreCase) => true,
            _ => false
        };
    }

    private static bool LooksLikePlainSiblingProjectScaffold(string combinedHint)
    {
        var text = NormalizeValue(combinedHint);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (ContainsAny(text, "check-runner", "check runner", "dotnet_test", "run test project", "test validation"))
            return false;

        var hasSiblingProjectIdentity = ContainsAny(
            text,
            ".core",
            ".storage",
            ".services",
            ".tests",
            ".contracts",
            ".repository",
            ".api",
            ".worker",
            ".console");
        if (!hasSiblingProjectIdentity)
            return false;

        return ContainsAny(
            text,
            "create dotnet project",
            "create class library",
            "create classlib project",
            "create wpf project",
            "create console project",
            "create console app",
            "create worker project",
            "create worker service",
            "create web api",
            "create webapi",
            "create xunit project",
            "create test project",
            "add project ",
            "add test project ",
            " to solution ",
            " into solution ");
    }

    private static bool LooksLikePlainSiblingProjectReference(string combinedHint)
    {
        var text = NormalizeValue(combinedHint);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasSiblingProjectIdentity = ContainsAny(
            text,
            ".core",
            ".storage",
            ".services",
            ".tests",
            ".contracts",
            ".repository",
            ".api",
            ".worker",
            ".console");
        if (!hasSiblingProjectIdentity)
            return false;

        return ContainsAny(text, "add reference from", "add dotnet project reference", "add project reference");
    }

    private static string ResolveCanonicalWorkFamily(string operationKind)
    {
        return NormalizeValue(operationKind) switch
        {
            "create_solution" or "create_project" or "add_project_to_solution" => "solution_scaffold",
            "create_test_project" or "attach_test_project" or "run_test_project" => "check_runner",
            "create_core_library" or "attach_core_library" or "add_domain_reference" or "make_contracts_dir" or "make_models_dir" => "core_domain_models_contracts",
            "make_state_dir" or "write_app_state" => "app_state_wiring",
            "make_storage_dir" or "write_storage_contract" or "write_storage_impl" => "storage_bootstrap",
            "write_repository_contract" or "write_repository_impl" => "repository_scaffold",
            "write_check_registry" or "write_snapshot_builder" or "write_findings_normalizer" => "findings_pipeline",
            "build_solution" or "build_native_workspace" => "build_verify",
            _ => ""
        };
    }

    private static string ResolveCanonicalPhraseFamily(string operationKind, string workFamily)
    {
        var normalizedOperation = NormalizeValue(operationKind);
        if (normalizedOperation is "make_state_dir" or "write_app_state")
            return "add_navigation_app_state";
        if (normalizedOperation is "make_storage_dir" or "write_storage_contract" or "write_storage_impl")
            return "setup_storage_layer";

        return NormalizeValue(workFamily) switch
        {
            "solution_scaffold" => "solution_scaffold",
            "check_runner" => "check_runner",
            "core_domain_models_contracts" => "core_domain_models_contracts",
            "app_state_wiring" => "add_navigation_app_state",
            "storage_bootstrap" => "setup_storage_layer",
            "repository_scaffold" => "repository_scaffold",
            "findings_pipeline" => "findings_pipeline",
            "build_verify" => "build_verify",
            _ => ""
        };
    }

    public TaskboardRunWorkItem ToRunWorkItem(TaskboardWorkItemRunStateRecord item)
    {
        return new TaskboardRunWorkItem
        {
            WorkItemId = item.WorkItemId,
            Ordinal = item.Ordinal,
            DisplayOrdinal = item.DisplayOrdinal,
            Title = item.Title,
            PromptText = item.PromptText,
            Summary = item.Summary,
            IsDecomposedItem = item.IsDecomposedItem,
            SourceWorkItemId = item.SourceWorkItemId,
            OperationKind = item.OperationKind,
            TargetStack = item.TargetStack,
            WorkFamily = item.WorkFamily,
            ExpectedArtifact = item.ExpectedArtifact,
            ValidationHint = item.ValidationHint,
            PhraseFamily = item.PhraseFamily,
            TemplateId = item.TemplateId,
            TemplateCandidateIds = [.. item.TemplateCandidateIds],
            DirectToolRequest = item.DirectToolRequest?.Clone()
        };
    }

    private static void ApplyToolRequestFallback(TaskboardWorkItemRunStateRecord item)
    {
        if (item.DirectToolRequest is null)
            return;

        var toolName = NormalizeValue(item.DirectToolRequest.ToolName);
        if (string.IsNullOrWhiteSpace(toolName))
            return;

        var path = FirstMeaningful(
            ReadArgument(item.DirectToolRequest, "path"),
            ReadArgument(item.DirectToolRequest, "project"),
            ReadArgument(item.DirectToolRequest, "project_path"),
            ReadArgument(item.DirectToolRequest, "reference_path"),
            ReadArgument(item.DirectToolRequest, "solution_path"),
            ReadArgument(item.DirectToolRequest, "output_path"),
            ReadArgument(item.DirectToolRequest, "directory"),
            ReadArgument(item.DirectToolRequest, "build_dir"),
            ReadArgument(item.DirectToolRequest, "source_dir"));
        var hint = $"{item.Title} {item.Summary} {item.PromptText} {path}".Trim();

        if (IsMissingOrUnknown(item.TargetStack))
        {
            item.TargetStack = toolName switch
                {
                    "create_dotnet_solution" or "create_dotnet_project" or "add_project_to_solution" or "add_dotnet_project_reference" or "create_dotnet_page_view" or "create_dotnet_viewmodel" or "register_navigation" or "register_di_service" or "initialize_sqlite_storage_boundary" or "dotnet_build" or "dotnet_test" => "dotnet_desktop",
                    "create_cmake_project" or "create_cpp_source_file" or "create_cpp_header_file" or "create_c_source_file" or "create_c_header_file" or "cmake_configure" or "cmake_build" or "ctest_run" => "native_cpp_desktop",
                    _ => IsNativePath(path)
                        ? "native_cpp_desktop"
                        : IsDotnetPath(path)
                            ? "dotnet_desktop"
                            : ""
                };
        }

        if (IsMissingOrUnknown(item.OperationKind))
        {
            item.OperationKind = toolName switch
            {
                "create_dotnet_solution" => "create_solution",
                "create_dotnet_project" => InferDotnetProjectOperation(hint),
                "add_project_to_solution" => LooksLikePlainSiblingProjectScaffold(hint)
                    ? "add_project_to_solution"
                    : ContainsAny(hint, "tests", ".tests")
                    ? "attach_test_project"
                    : ContainsAny(hint, ".core", " core ", "contracts", "models")
                        ? "attach_core_library"
                        : "add_project_to_solution",
                "add_dotnet_project_reference" => LooksLikePlainSiblingProjectReference(hint)
                    ? "add_project_reference"
                    : ContainsAny(hint, ".core", " core ", "contracts", "models")
                    ? "add_domain_reference"
                    : "add_project_reference",
                "create_dotnet_page_view" => "write_page",
                "create_dotnet_viewmodel" => ContainsAny(hint, "appstate", "app state") ? "write_app_state" : "write_shell_viewmodel",
                "register_navigation" => ContainsAny(hint, "shellregistration", "shell registration", "navigationregistry", "navigation registry")
                    ? "write_shell_registration"
                    : "write_navigation_item",
                "register_di_service" => "write_storage_impl",
                "initialize_sqlite_storage_boundary" => "write_storage_contract",
                "dotnet_build" => "build_solution",
                "dotnet_test" => "run_test_project",
                "make_dir" => InferMakeDirOperation(path),
                "create_cmake_project" => "write_cmake_lists",
                "create_cpp_source_file" => InferNativeSourceOperation(path),
                "create_cpp_header_file" => InferNativeHeaderOperation(path),
                "cmake_configure" => "configure_cmake",
                "cmake_build" => "build_native_workspace",
                "show_artifacts" or "show_memory" => "inspect_context_artifacts",
                "write_file" => InferWriteFileOperation(hint),
                _ => ""
            };
        }

        if (IsMissingOrUnknown(item.PhraseFamily))
            item.PhraseFamily = InferPhraseFamily(item, toolName);

        if (IsMissingOrUnknown(item.TemplateId))
            item.TemplateId = InferTemplateId(item);

        if (IsMissingOrUnknown(item.WorkFamily))
            item.WorkFamily = InferWorkFamily(item);

        NormalizeTestSupportWriteRouting(item);
    }

    private static TaskboardWorkItemRunStateRecord? ResolveSourceWorkItem(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord item)
    {
        if (runState is null || string.IsNullOrWhiteSpace(item.SourceWorkItemId))
            return null;

        return runState.Batches
            .SelectMany(batch => batch.WorkItems)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.WorkItemId, item.SourceWorkItemId, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildContextText(
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord item,
        TaskboardWorkItemRunStateRecord? sourceItem)
    {
        var path = FirstMeaningful(
            item.ExpectedArtifact,
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "path"),
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "project"),
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "solution_path"),
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "project_path"),
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "output_path"));

        var parts = new List<string>();
        if (!IsGenericFollowupTitle(item.Title))
            parts.Add(item.Title);
        parts.Add(item.Summary);
        parts.Add(item.PromptText);
        parts.Add(batch.Title);
        parts.Add(item.ValidationHint);
        parts.Add(item.ExpectedArtifact);
        parts.Add(path);
        parts.Add(runState.PlanTitle);

        if (sourceItem is not null)
        {
            parts.Add(sourceItem.Title);
            parts.Add(sourceItem.Summary);
            parts.Add(sourceItem.PromptText);
            parts.Add(sourceItem.ValidationHint);
            parts.Add(sourceItem.ExpectedArtifact);
            parts.Add(sourceItem.LastResultSummary);
            parts.Add(sourceItem.WorkFamily);
            parts.Add(sourceItem.PhraseFamily);
            parts.Add(sourceItem.OperationKind);
            parts.Add(sourceItem.TargetStack);
            parts.Add(sourceItem.TemplateId);
        }

        if (IsGenericFollowupTitle(item.Title))
        {
            parts.Add(runState.LastCompletedWorkItemTitle);
            parts.Add(runState.LastCompletedWorkFamily);
            parts.Add(runState.LastCompletedPhraseFamily);
            parts.Add(runState.LastCompletedOperationKind);
            parts.Add(runState.LastCompletedStackFamily);
            parts.Add(runState.LastCompletedStepSummary);
            parts.Add(runState.LastResultSummary);
            parts.Add(runState.LastExecutionGoalSummary);
            parts.Add(runState.LastFollowupWorkItemTitle);
            parts.Add(runState.LastFollowupWorkFamily);
            parts.Add(runState.LastFollowupPhraseFamily);
            parts.Add(runState.LastFollowupOperationKind);
            parts.Add(runState.LastFollowupStackFamily);
            parts.Add(runState.LastFollowupResolutionSummary);
            parts.Add(runState.LastNextWorkFamily);
            parts.Add(runState.LastFailureNormalizedSummary);
            parts.Add(runState.LastFailureErrorCode);
            parts.Add(runState.LastFailureTargetPath);
            parts.Add(runState.LastBlockerReason);
        }

        return string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
    }

    private static string InferStackFromContext(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord item,
        TaskboardWorkItemRunStateRecord? sourceItem,
        string contextText)
    {
        if (!IsMissingOrUnknown(sourceItem?.TargetStack))
            return sourceItem!.TargetStack;

        if (IsGenericFollowupTitle(item.Title))
        {
            var trackedStack = ResolveTrackedFollowupValue(
                runState,
                item,
                runState.LastFollowupStackFamily,
                runState.LastBlockerStackFamily);
            if (!IsMissingOrUnknown(trackedStack))
                return trackedStack;

            if (!IsMissingOrUnknown(runState.LastCompletedStackFamily))
                return runState.LastCompletedStackFamily;
        }

        if (ContainsAny(contextText, ".cs", ".xaml", ".csproj", ".sln", "wpf", "windows app sdk", "dotnet", ".net", "c#"))
            return "dotnet_desktop";

        if (ContainsAny(contextText, ".cpp", ".h", "cmakelists.txt", "cmake", "win32", "native", "c++", "cpp"))
            return "native_cpp_desktop";

        if (runState.LastResolvedBuildProfile.Status == TaskboardBuildProfileResolutionStatus.Resolved)
        {
            return runState.LastResolvedBuildProfile.StackFamily switch
            {
                TaskboardStackFamily.DotnetDesktop => "dotnet_desktop",
                TaskboardStackFamily.NativeCppDesktop => "native_cpp_desktop",
                _ => ""
            };
        }

        return "";
    }

    private static string InferWorkFamilyFromContext(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord item,
        TaskboardWorkItemRunStateRecord? sourceItem,
        string contextText)
    {
        var inferredFromText = InferTextualWorkFamily(contextText);
        if (!string.IsNullOrWhiteSpace(inferredFromText))
            return inferredFromText;

        if (IsGenericFollowupTitle(item.Title))
        {
            return FirstMeaningful(
                sourceItem?.WorkFamily,
                sourceItem?.LastExecutionGoalResolution.WorkFamily,
                sourceItem?.LastExecutionGoalResolution.LaneResolution.WorkFamily,
                ResolveTrackedFollowupValue(
                    runState,
                    item,
                    runState.LastFollowupWorkFamily,
                    runState.LastBlockerWorkFamily,
                    runState.LastNextWorkFamily),
                InferGenericGoalWorkFamilyFromLineage(runState, item, contextText));
        }

        return "";
    }

    private static string InferPhraseFamilyFromContext(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord item,
        TaskboardWorkItemRunStateRecord? sourceItem,
        string contextText)
    {
        if (!string.IsNullOrWhiteSpace(InferNotificationWiringWorkFamily(contextText)))
            return "add_navigation_app_state";

        var uiFeatureUpdatePhraseFamily = InferUiFeatureUpdatePhraseFamily(contextText);
        if (!string.IsNullOrWhiteSpace(uiFeatureUpdatePhraseFamily))
            return uiFeatureUpdatePhraseFamily;

        if (LooksLikeMaintenanceContext(contextText))
            return "maintenance_context";

        if (LooksLikeMaintenanceVerificationDisciplineContext(contextText))
            return "build_verify";

        if (LooksLikeMaintenanceDataDisciplineContext(contextText))
            return "maintenance_context";

        if (LooksLikeMaintenanceFeatureUpdateContext(contextText))
            return "maintenance_context";

        if (LooksLikeRulesSpecificationContext(contextText))
            return "core_domain_models_contracts";

        if (LooksLikeRemediationTypeContext(contextText))
            return "core_domain_models_contracts";

        if (LooksLikeFuturePlaceholderSpecificationContext(contextText))
            return "core_domain_models_contracts";

        var workFamily = FirstMeaningful(item.WorkFamily, InferWorkFamilyFromContext(runState, item, sourceItem, contextText));
        if (!string.IsNullOrWhiteSpace(workFamily))
        {
            return workFamily switch
            {
                "solution_scaffold" => "solution_scaffold",
                "ui_shell_sections" => "ui_shell_sections",
                "ui_wiring" or "app_state_wiring" => "add_navigation_app_state",
                "viewmodel_scaffold" => "ui_shell_sections",
                "storage_bootstrap" => "setup_storage_layer",
                "core_domain_models_contracts" => "core_domain_models_contracts",
                "repository_scaffold" => "repository_scaffold",
                "maintenance_context" => "maintenance_context",
                "check_runner" => "check_runner",
                "findings_pipeline" => "findings_pipeline",
                "build_verify" => "build_verify",
                "native_project_bootstrap" => "native_project_bootstrap",
                _ => ""
            };
        }

        if (ContainsAny(contextText, "snapshot repository", "repository implementation", "supporting storage files")
            && ContainsAny(contextText, "storage", "storage files", "sqlite", "settings store"))
        {
            return "setup_storage_layer";
        }

        if (ContainsAny(contextText, "shell behaviors", "shell behavior", "shell sections", "shell section", "page set", "top-level shell", "top level shell"))
            return "ui_shell_sections";
        if (ContainsAny(contextText, "sqlite", "storage layer", "settings store", "storage boundary", "storage files"))
            return "setup_storage_layer";
        if (ContainsAny(contextText, "check definition", "finding record", "core contracts", "domain contracts", "domain model", "domain models", "contracts and models"))
            return "core_domain_models_contracts";
        if (ContainsAny(contextText, "repository"))
            return "repository_scaffold";
        if (ContainsAny(contextText, "navigation", "app state", "shell registration"))
            return "add_navigation_app_state";
        if (ContainsAny(contextText, "check runner", "check registry"))
            return "check_runner";
        if (ContainsAny(contextText, "findings pipeline", "snapshot builder", "findings normalizer"))
            return "findings_pipeline";
        if (ContainsAny(contextText, "msb4006", "circular dependency", "target dependency graph", "nuget.targets", "project graph repair", "solution graph repair"))
            return "build_failure_repair";
        if (LooksLikeEvidenceValidationContext(contextText))
            return "build_verify";
        if (ContainsAny(contextText, "build verify", "validate build", "run tests"))
            return "build_verify";

        if (IsGenericFollowupTitle(item.Title))
        {
            return FirstMeaningful(
                sourceItem?.PhraseFamily,
                sourceItem?.LastExecutionGoalResolution.PhraseFamily,
                sourceItem?.LastExecutionGoalResolution.LaneResolution.PhraseFamily,
                ResolveTrackedFollowupValue(
                    runState,
                    item,
                    runState.LastFollowupPhraseFamily,
                    runState.LastBlockerPhraseFamily),
                InferPhraseFamilyFromWorkFamily(
                    InferGenericGoalWorkFamilyFromLineage(runState, item, contextText)));
        }

        return "";
    }

    private static string InferOperationKindFromContext(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord item,
        TaskboardWorkItemRunStateRecord? sourceItem,
        string contextText)
    {
        var notificationWiringFamily = InferNotificationWiringWorkFamily(contextText);
        if (!string.IsNullOrWhiteSpace(notificationWiringFamily))
        {
            return string.Equals(notificationWiringFamily, "ui_wiring", StringComparison.OrdinalIgnoreCase)
                ? "write_shell_registration"
                : "write_app_state";
        }

        var uiFeatureUpdatePhraseFamily = InferUiFeatureUpdatePhraseFamily(contextText);
        if (!string.IsNullOrWhiteSpace(uiFeatureUpdatePhraseFamily))
        {
            return uiFeatureUpdatePhraseFamily switch
            {
                "wire_dashboard" or "add_history_log_view" or "add_settings_page" => "write_page",
                _ => "shell_behavior_extend"
            };
        }

        if (LooksLikeMaintenanceContext(contextText))
            return "inspect_context_artifacts";

        if (LooksLikeMaintenanceVerificationDisciplineContext(contextText))
        {
            return string.Equals(NormalizeValue(item.TargetStack), "native_cpp_desktop", StringComparison.OrdinalIgnoreCase)
                ? "build_native_workspace"
                : "build_solution";
        }

        if (LooksLikeMaintenanceDataDisciplineContext(contextText))
            return "inspect_context_artifacts";

        if (LooksLikeMaintenanceFeatureUpdateContext(contextText))
            return "inspect_context_artifacts";

        if (LooksLikeRulesSpecificationContext(contextText))
        {
            return ContainsAny(contextText, "model", "models", "record", "records")
                && !ContainsAny(contextText, "contract", "contracts", "interface", "interfaces", "policy contract", "policy contracts", "specification", "specifications")
                ? "write_domain_model_file"
                : "write_contract_file";
        }

        if (LooksLikeRemediationTypeContext(contextText))
        {
            return ContainsAny(contextText, "contract", "contracts", "interface")
                ? "write_contract_file"
                : "write_domain_model_file";
        }

        if (LooksLikeFuturePlaceholderSpecificationContext(contextText))
        {
            return ContainsAny(contextText, "model", "models", "record", "records", "placeholder model", "placeholder models", "stub model", "stub models")
                && !ContainsAny(contextText, "contract", "contracts", "interface", "interfaces", "specification", "specifications", "template contract", "template contracts")
                ? "write_domain_model_file"
                : "write_contract_file";
        }

        if (LooksLikeEvidenceValidationContext(contextText))
        {
            return string.Equals(NormalizeValue(item.TargetStack), "native_cpp_desktop", StringComparison.OrdinalIgnoreCase)
                ? "build_native_workspace"
                : "build_solution";
        }

        if (ContainsAny(contextText, "msb4006", "circular dependency", "target dependency graph", "nuget.targets", "project graph repair", "solution graph repair"))
        {
            return ContainsAny(contextText, ".sln", "solution")
                ? "inspect_solution_wiring"
                : "inspect_project_reference_graph";
        }

        if (ContainsAny(contextText, "snapshot repository", "repository implementation")
            && ContainsAny(contextText, "storage", "storage files", "sqlite", "settings store"))
        {
            return "write_storage_impl";
        }

        if (ContainsAny(contextText, "supporting storage files")
            || ContainsAny(contextText, "sqlite storage", "settings store", "storage implementation", "storage files"))
        {
            return "write_storage_impl";
        }

        if (ContainsAny(contextText, "snapshot repository", "repository implementation", "repository class"))
            return "write_repository_impl";
        if (ContainsAny(contextText, "repository interface", "repository contract"))
            return "write_repository_contract";
        if (ContainsAny(contextText, "check registry"))
            return "write_check_registry";
        if (ContainsAny(contextText, "snapshot builder"))
            return "write_snapshot_builder";
        if (ContainsAny(contextText, "findings normalizer", "normalize findings"))
            return "write_findings_normalizer";
        if (ContainsAny(contextText, "shell registration", "navigation registry"))
            return "write_shell_registration";
        if (ContainsAny(contextText, "navigation item"))
            return "write_navigation_item";
        if (ContainsAny(contextText, "app state"))
            return "write_app_state";
        if (ContainsAny(contextText, "shell viewmodel", "main viewmodel", "section viewmodel"))
            return "write_shell_viewmodel";
        if (ContainsAny(contextText, "storage contract", "settings store interface", "sqlite contract"))
            return "write_storage_contract";
        if (ContainsAny(contextText, "shell layout"))
            return "shell_layout_update";
        if (ContainsAny(contextText, "shell behaviors", "shell behavior", "recommended implementation detail"))
            return "shell_behavior_extend";
        if (ContainsAny(contextText, "shell sections", "shell section"))
            return "shell_section_create";
        if (ContainsAny(contextText, "dashboard", "findings", "history", "settings")
            && ContainsAny(contextText, "page", "view"))
            return "write_page";
        if (ContainsAny(contextText, "build verify", "validate build", "run tests"))
        {
            return string.Equals(NormalizeValue(item.TargetStack), "native_cpp_desktop", StringComparison.OrdinalIgnoreCase)
                ? "build_native_workspace"
                : "build_solution";
        }

        var workFamily = FirstMeaningful(item.WorkFamily, InferWorkFamilyFromContext(runState, item, sourceItem, contextText));
        if (!string.IsNullOrWhiteSpace(workFamily))
        {
            return workFamily switch
            {
                "core_domain_models_contracts" => ContainsAny(contextText, "model", "record", "records")
                    && !ContainsAny(contextText, "contract", "contracts", "interface", "interfaces")
                    ? "write_domain_model_file"
                    : ContainsAny(contextText, ".core", " core ", "reference")
                        ? "add_domain_reference"
                        : "write_contract_file",
                "repository_scaffold" => ContainsAny(contextText, "contract", "interface")
                    ? "write_repository_contract"
                    : "write_repository_impl",
                "storage_bootstrap" => ContainsAny(contextText, "contract", "interface")
                    ? "write_storage_contract"
                    : "write_storage_impl",
                "ui_wiring" => "write_shell_registration",
                "app_state_wiring" => "write_app_state",
                "viewmodel_scaffold" => "write_shell_viewmodel",
                "ui_shell_sections" => "shell_behavior_extend",
                "check_runner" => "run_test_project",
                "findings_pipeline" => "write_snapshot_builder",
                "build_verify" => string.Equals(NormalizeValue(item.TargetStack), "native_cpp_desktop", StringComparison.OrdinalIgnoreCase)
                    ? "build_native_workspace"
                    : "build_solution",
                "build_repair" => ContainsAny(contextText, ".sln", "solution")
                    ? "inspect_solution_wiring"
                    : "inspect_project_reference_graph",
                _ => ""
            };
        }

        if (IsGenericFollowupTitle(item.Title))
        {
            return FirstMeaningful(
                sourceItem?.OperationKind,
                sourceItem?.LastExecutionGoalResolution.OperationKind,
                sourceItem?.LastExecutionGoalResolution.LaneResolution.OperationKind,
                ResolveTrackedFollowupValue(
                    runState,
                    item,
                    runState.LastFollowupOperationKind,
                    runState.LastBlockerOperationKind),
                InferOperationKindFromWorkFamily(
                    InferGenericGoalWorkFamilyFromLineage(runState, item, contextText),
                    item.TargetStack,
                    contextText));
        }

        return "";
    }

    private static void SealGenericFollowupTruth(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord item,
        TaskboardWorkItemRunStateRecord? sourceItem,
        string contextText)
    {
        if (!IsGenericFollowupTitle(item.Title))
            return;

        if (IsMissingOrUnknown(item.TargetStack))
        {
            item.TargetStack = FirstMeaningful(
                sourceItem?.TargetStack,
                ResolveTrackedFollowupValue(
                    runState,
                    item,
                    runState.LastFollowupStackFamily,
                    runState.LastBlockerStackFamily),
                runState.LastCompletedStackFamily);
        }

        if (IsMissingOrUnknown(item.WorkFamily))
        {
            item.WorkFamily = FirstMeaningful(
                sourceItem?.WorkFamily,
                ResolveTrackedFollowupValue(
                    runState,
                    item,
                    runState.LastFollowupWorkFamily,
                    runState.LastBlockerWorkFamily,
                    runState.LastNextWorkFamily),
                InferGenericGoalWorkFamilyFromLineage(runState, item, contextText));
        }

        if (IsMissingOrUnknown(item.PhraseFamily))
        {
            item.PhraseFamily = FirstMeaningful(
                sourceItem?.PhraseFamily,
                ResolveTrackedFollowupValue(
                    runState,
                    item,
                    runState.LastFollowupPhraseFamily,
                    runState.LastBlockerPhraseFamily),
                InferPhraseFamilyFromContext(runState, item, sourceItem, contextText),
                InferPhraseFamilyFromWorkFamily(item.WorkFamily));
        }

        if (IsMissingOrUnknown(item.OperationKind))
        {
            item.OperationKind = FirstMeaningful(
                sourceItem?.OperationKind,
                ResolveTrackedFollowupValue(
                    runState,
                    item,
                    runState.LastFollowupOperationKind,
                    runState.LastBlockerOperationKind),
                InferOperationKindFromContext(runState, item, sourceItem, contextText),
                InferOperationKindFromWorkFamily(item.WorkFamily, item.TargetStack, contextText));
        }
    }

    private static string InferTextualWorkFamily(string contextText)
    {
        var notificationWiringFamily = InferNotificationWiringWorkFamily(contextText);
        if (!string.IsNullOrWhiteSpace(notificationWiringFamily))
            return notificationWiringFamily;

        var uiFeatureUpdatePhraseFamily = InferUiFeatureUpdatePhraseFamily(contextText);
        if (!string.IsNullOrWhiteSpace(uiFeatureUpdatePhraseFamily))
            return "ui_shell_sections";

        if (LooksLikeMaintenanceContext(contextText))
            return "maintenance_context";

        if (LooksLikeMaintenanceVerificationDisciplineContext(contextText))
            return "build_verify";

        if (LooksLikeMaintenanceDataDisciplineContext(contextText))
            return "maintenance_context";

        if (LooksLikeMaintenanceFeatureUpdateContext(contextText))
            return "maintenance_context";

        if (LooksLikeRulesSpecificationContext(contextText))
            return "repository_scaffold";

        if (LooksLikeRemediationTypeContext(contextText))
            return "repository_scaffold";

        if (LooksLikeFuturePlaceholderSpecificationContext(contextText))
            return "repository_scaffold";

        if (LooksLikeEvidenceValidationContext(contextText))
            return "build_verify";

        if (ContainsAny(contextText, "msb4006", "circular dependency", "target dependency graph", "nuget.targets", "project graph repair", "solution graph repair"))
            return "build_repair";

        if (ContainsAny(contextText, "snapshot repository", "repository implementation", "supporting storage files")
            && ContainsAny(contextText, "storage", "storage files", "sqlite", "settings store"))
        {
            return "storage_bootstrap";
        }

        if (ContainsAny(contextText, "sqlite", "storage layer", "settings store", "storage boundary", "storage files"))
            return "storage_bootstrap";
        if (ContainsAny(contextText, "check definition", "finding record", "core contracts", "domain contracts", "domain model", "domain models", "contracts and models"))
            return "core_domain_models_contracts";
        if (ContainsAny(contextText, "repository"))
            return "repository_scaffold";
        if (ContainsAny(contextText, "shell registration", "navigation registry", "navigation item"))
            return "ui_wiring";
        if (ContainsAny(contextText, "app state", "startup state", "selected finding state"))
            return "app_state_wiring";
        if (ContainsAny(contextText, "shell viewmodel", "viewmodel"))
            return "viewmodel_scaffold";
        if (ContainsAny(contextText, "dashboard", "findings", "history", "settings", "shell behaviors", "shell behavior", "shell sections", "shell section", "page set", "top-level shell", "top level shell"))
            return "ui_shell_sections";
        if (ContainsAny(contextText, "check runner", "check registry"))
            return "check_runner";
        if (ContainsAny(contextText, "findings pipeline", "snapshot builder", "findings normalizer"))
            return "findings_pipeline";
        if (ContainsAny(contextText, "build verify", "validate build", "run tests"))
            return "build_verify";
        if (ContainsAny(contextText, "cmake", "cpp", "c++", "native", "win32"))
            return "native_project_bootstrap";

        return "";
    }

    private static string InferGenericGoalWorkFamilyFromLineage(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord item,
        string contextText)
    {
        var notificationWiringFamily = InferNotificationWiringWorkFamily(contextText);
        if (!string.IsNullOrWhiteSpace(notificationWiringFamily))
            return notificationWiringFamily;

        var uiFeatureUpdatePhraseFamily = InferUiFeatureUpdatePhraseFamily(contextText);
        if (!string.IsNullOrWhiteSpace(uiFeatureUpdatePhraseFamily))
            return "ui_shell_sections";

        if (LooksLikeMaintenanceContext(contextText))
            return "maintenance_context";

        if (LooksLikeMaintenanceVerificationDisciplineContext(contextText))
            return "build_verify";

        if (LooksLikeMaintenanceDataDisciplineContext(contextText))
            return "maintenance_context";

        if (LooksLikeMaintenanceFeatureUpdateContext(contextText))
            return "maintenance_context";

        if (LooksLikeRulesSpecificationContext(contextText))
            return "repository_scaffold";

        if (LooksLikeRemediationTypeContext(contextText))
            return "repository_scaffold";

        if (LooksLikeFuturePlaceholderSpecificationContext(contextText))
            return "repository_scaffold";

        if (LooksLikeEvidenceValidationContext(contextText))
            return "build_verify";

        if (ContainsAny(contextText, "msb4006", "circular dependency", "target dependency graph", "nuget.targets", "project graph repair", "solution graph repair"))
            return "build_repair";

        if (IsValidationContinuation(runState, item, contextText))
            return "build_verify";

        return "";
    }

    private static string InferPhraseFamilyFromWorkFamily(string? workFamily)
    {
        return NormalizeValue(workFamily) switch
        {
            "solution_scaffold" => "solution_scaffold",
            "ui_shell_sections" => "ui_shell_sections",
            "ui_wiring" or "app_state_wiring" => "add_navigation_app_state",
            "viewmodel_scaffold" => "ui_shell_sections",
            "storage_bootstrap" => "setup_storage_layer",
            "core_domain_models_contracts" => "core_domain_models_contracts",
            "repository_scaffold" => "repository_scaffold",
            "maintenance_context" => "maintenance_context",
            "check_runner" => "check_runner",
            "findings_pipeline" => "findings_pipeline",
            "build_verify" => "build_verify",
            "build_repair" => "build_failure_repair",
            "native_project_bootstrap" => "native_project_bootstrap",
            _ => ""
        };
    }

    private static string InferUiFeatureUpdatePhraseFamily(string? contextText)
    {
        if (string.IsNullOrWhiteSpace(contextText))
            return "";

        var normalized = NormalizeValue(contextText);
        var touchesUi = ContainsAny(normalized, "dashboard", "findings", "history", "settings", "shell", "page", "view", "viewmodel", "mainwindow", "main window");
        if (!touchesUi)
            return "";

        if (!ContainsAny(normalized, "feature update", "follow-up", "follow up", "baseline", "bounded", "ui", "behavior", "page", "view", "shell", "settings", "history", "dashboard", "findings"))
            return "";

        if (ContainsAny(normalized, "dashboard") && !ContainsAny(normalized, "findings", "history", "settings"))
            return "wire_dashboard";

        if (ContainsAny(normalized, "history", "log") && !ContainsAny(normalized, "dashboard", "findings", "settings"))
            return "add_history_log_view";

        if (ContainsAny(normalized, "settings") && !ContainsAny(normalized, "dashboard", "findings", "history"))
            return "add_settings_page";

        return "ui_shell_sections";
    }

    private static string InferOperationKindFromWorkFamily(string? workFamily, string? stackFamily, string contextText)
    {
        return NormalizeValue(workFamily) switch
        {
            "core_domain_models_contracts" => (LooksLikeRulesSpecificationContext(contextText)
                    || LooksLikeFuturePlaceholderSpecificationContext(contextText))
                ? (ContainsAny(contextText, "model", "models", "record", "records")
                    && !ContainsAny(contextText, "contract", "contracts", "interface", "interfaces", "policy contract", "policy contracts", "specification", "specifications")
                    ? "write_domain_model_file"
                    : "write_contract_file")
                : ContainsAny(contextText, ".sln", "solution", "project reference", "reference")
                    ? "add_domain_reference"
                    : ContainsAny(contextText, "model", "record", "records")
                        && !ContainsAny(contextText, "contract", "contracts", "interface", "interfaces")
                        ? "write_domain_model_file"
                        : "write_contract_file",
            "repository_scaffold" => (LooksLikeRulesSpecificationContext(contextText)
                    || LooksLikeFuturePlaceholderSpecificationContext(contextText))
                ? (ContainsAny(contextText, "model", "models", "record", "records")
                    && !ContainsAny(contextText, "contract", "contracts", "interface", "interfaces", "policy contract", "policy contracts", "specification", "specifications")
                    ? "write_domain_model_file"
                    : "write_contract_file")
                : ContainsAny(contextText, "contract", "interface")
                ? "write_repository_contract"
                : "write_repository_impl",
            "storage_bootstrap" => ContainsAny(contextText, "contract", "interface")
                ? "write_storage_contract"
                : "write_storage_impl",
            "ui_wiring" => "write_shell_registration",
            "app_state_wiring" => "write_app_state",
            "viewmodel_scaffold" => "write_shell_viewmodel",
            "ui_shell_sections" => "shell_behavior_extend",
            "maintenance_context" => "inspect_context_artifacts",
            "check_runner" => "run_test_project",
            "findings_pipeline" => "write_snapshot_builder",
            "build_verify" => string.Equals(NormalizeValue(stackFamily), "native_cpp_desktop", StringComparison.OrdinalIgnoreCase)
                ? "build_native_workspace"
                : "build_solution",
            "build_repair" => ContainsAny(contextText, ".sln", "solution")
                ? "inspect_solution_wiring"
                : "inspect_project_reference_graph",
            _ => ""
        };
    }

    private static string ResolveTrackedFollowupValue(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord item,
        params string?[] values)
    {
        if (!MatchesTrackedWorkItem(runState.LastFollowupWorkItemId, runState.LastFollowupWorkItemTitle, item)
            && !MatchesTrackedWorkItem(runState.LastBlockerWorkItemId, runState.LastBlockerWorkItemTitle, item))
        {
            return "";
        }

        return FirstMeaningful(values);
    }

    private static bool MatchesTrackedWorkItem(
        string? trackedWorkItemId,
        string? trackedWorkItemTitle,
        TaskboardWorkItemRunStateRecord item)
    {
        if (!string.IsNullOrWhiteSpace(trackedWorkItemId)
            && string.Equals(trackedWorkItemId, item.WorkItemId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(trackedWorkItemTitle)
            && string.Equals(trackedWorkItemTitle, item.Title, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidationContinuation(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord item,
        string contextText)
    {
        if (!string.Equals(NormalizeValue(item.TargetStack), "dotnet_desktop", StringComparison.OrdinalIgnoreCase)
            && runState.LastResolvedBuildProfile.StackFamily != TaskboardStackFamily.DotnetDesktop)
        {
            return false;
        }

        var hasValidationCue = ContainsAny(
            contextText,
            "acceptance",
            "acceptance gate",
            "final acceptance",
            "startup verification",
            "startup verify",
            "launch verification",
            "launch verify",
            "validate",
            "validation",
            "verify",
            "verification",
            "proof",
            "proof artifact",
            "proof artifacts",
            "success criteria");
        if (!hasValidationCue)
            return false;

        return ContainsAny(
            contextText,
            "build",
            "workspace",
            "solution",
            "project",
            ".sln",
            ".csproj",
            ".xaml",
            ".cs",
            "shellviewmodel",
            "dashboardpage",
            "findingspage",
            "historypage",
            "settingspage",
            runState.LastCompletedWorkFamily,
            runState.LastFollowupWorkFamily,
            runState.LastExecutionGoalSummary);
    }

    private static bool IsGenericFollowupTitle(string? title)
    {
        return TaskboardStructuralHeadingService.IsNonActionableHeading(title);
    }

    private static string InferNotificationWiringWorkFamily(string? contextText)
    {
        if (string.IsNullOrWhiteSpace(contextText))
            return "";

        var hasNotificationTitle = ContainsAny(
            contextText,
            "required notification events",
            "required notification event",
            "required handlers",
            "required handler",
            "required policies",
            "required policy",
            "required guards",
            "required guard",
            "required wiring");
        if (!hasNotificationTitle)
            return "";

        var hasLineageCue = ContainsAny(
            contextText,
            "guidance",
            "rules",
            "rule",
            "remediation",
            "app state",
            "state",
            "shell registration",
            "registration",
            "registry",
            "src\\",
            "/src/",
            ".cs",
            "write_file",
            "viewmodel");
        if (!hasLineageCue)
            return "";

        if (ContainsAny(contextText, "notification", "event", "events", "app state", "state"))
            return "app_state_wiring";

        if (ContainsAny(contextText, "handler", "handlers", "policy", "policies", "guard", "guards", "registration", "registry", "wiring", "wire"))
            return "ui_wiring";

        return "";
    }

    private static bool LooksLikeMaintenanceContext(string? contextText)
    {
        if (string.IsNullOrWhiteSpace(contextText))
            return false;

        var hasContextTitle = ContainsAny(
            contextText,
            "required context",
            "required baseline context",
            "required prior run data",
            "maintenance context packet",
            "context packet",
            "existing project intake context",
            "existing-project intake context",
            "reuse prior run data",
            "baseline context");
        if (!hasContextTitle)
            return false;

        return ContainsAny(
            contextText,
            "summary artifact",
            "run summary",
            "normalized run",
            "normalized record",
            "repair context",
            "prior run",
            "prior phase",
            "baseline",
            "artifact",
            "artifacts",
            "existing project",
            "current workspace",
            "workspace build",
            "build target",
            "verification result",
            "result artifact",
            "file touch",
            ".sln",
            ".csproj",
            ".cs",
            "maintenance loop",
            "patch repair",
            "feature update");
    }

    private static bool LooksLikeMaintenanceFeatureUpdateContext(string? contextText)
    {
        if (string.IsNullOrWhiteSpace(contextText))
            return false;

        var hasFeatureScopeTitle = ContainsAny(
            contextText,
            "allowed feature classes for this project",
            "allowed feature classes",
            "allowed feature class");
        var hasFlowTitle = ContainsAny(
            contextText,
            "required flow",
            "feature update flow",
            "patch repair flow",
            "bounded feature update flow");
        if (!hasFeatureScopeTitle && !hasFlowTitle)
            return false;

        if (hasFeatureScopeTitle)
        {
            return ContainsAny(
                contextText,
                "dashboard",
                "findings",
                "history",
                "settings",
                "posture check",
                "remediation guidance",
                "notification rule",
                "feature update",
                "baseline",
                "bounded",
                "existing project",
                "maintenance loop");
        }

        return ContainsAny(
            contextText,
            "identify feature target area",
            "identify bounded files/projects to edit",
            "generate feature update plan",
            "preview patch/update artifacts",
            "apply patch/update",
            "rerun build/tests/app verification",
            "persist summary and normalized run data",
            "feature update",
            "patch repair",
            "baseline",
            "bounded");
    }

    private static bool LooksLikeMaintenanceVerificationDisciplineContext(string? contextText)
    {
        if (string.IsNullOrWhiteSpace(contextText))
            return false;

        var hasVerificationTitle = ContainsAny(
            contextText,
            "required checkpoints",
            "required checkpoint",
            "evidence of success",
            "proof of completion",
            "validation evidence",
            "acceptance evidence");
        var hasVerificationRule = ContainsAny(contextText, "required rule")
            && ContainsAny(
                contextText,
                "post-mutation verification",
                "post mutation verification",
                "patch/update pass",
                "patch update pass",
                "feature or repair is considered successful",
                "verification passes");
        if (!hasVerificationTitle && !hasVerificationRule)
            return false;

        return ContainsAny(
            contextText,
            "post-mutation verification discipline",
            "post mutation verification discipline",
            "after every patch/update pass",
            "after every patch update pass",
            "rerun and verify",
            "post-mutation verification passes",
            "post mutation verification passes",
            "mutation followed by build/launch/verify data",
            "truthful terminal summary",
            "solution still builds",
            "desktop app still launches",
            "shell still loads",
            "key sections still render",
            "storage still initializes safely",
            "check runner still executes deterministically",
            "findings/history still persist and render",
            "findings history still persist and render",
            "warnings are tracked");
    }

    private static bool LooksLikeMaintenanceDataDisciplineContext(string? contextText)
    {
        if (string.IsNullOrWhiteSpace(contextText))
            return false;

        var hasDataTitle = ContainsAny(
            contextText,
            "required behavior",
            "required behaviors",
            "required outputs",
            "required output");
        if (!hasDataTitle)
            return false;

        return ContainsAny(
            contextText,
            "file-touch tracking",
            "file touch tracking",
            "repeated touches",
            "skip with proof",
            "never skip repair",
            "terminal run summary",
            "normalized run record",
            "file-touch rollup",
            "file touch rollup",
            "skip rollup",
            "patch/update artifacts",
            "patch update artifacts",
            "warning metrics",
            "corpus-ready export",
            "corpus ready export",
            "embedder-ready index/export",
            "embedder ready index export");
    }

    private static bool LooksLikeRulesSpecificationContext(string? contextText)
    {
        if (string.IsNullOrWhiteSpace(contextText))
            return false;

        var hasRulesTitle = ContainsAny(
            contextText,
            "required rules",
            "required rule",
            "rule specifications",
            "rule specification",
            "policy contracts",
            "guidance rules");
        if (!hasRulesTitle)
            return false;

        return ContainsAny(
            contextText,
            "rule",
            "rules",
            "policy",
            "spec",
            "specification",
            "specifications",
            "contract",
            "contracts",
            "record",
            "records",
            "guidance",
            "core",
            "src\\",
            "/src/",
            ".cs",
            "write_file");
    }

    private static bool LooksLikeRemediationTypeContext(string? contextText)
    {
        if (string.IsNullOrWhiteSpace(contextText))
            return false;

        var hasRemediationTitle = ContainsAny(
            contextText,
            "required remediation types",
            "required remediation type",
            "remediation types",
            "remediation models",
            "remediation contracts");
        if (!hasRemediationTitle)
            return false;

        return ContainsAny(
            contextText,
            "type",
            "types",
            "model",
            "models",
            "contract",
            "contracts",
            "record",
            "records",
            "core",
            "src\\",
            "/src/",
            ".cs",
            "write_file",
            "repair",
            "guidance");
    }

    private static bool LooksLikeFuturePlaceholderSpecificationContext(string? contextText)
    {
        if (string.IsNullOrWhiteSpace(contextText))
            return false;

        var hasPlaceholderTitle = ContainsAny(
            contextText,
            "required future placeholders only",
            "required future placeholder only",
            "future placeholders only",
            "future placeholder only",
            "template-only future work",
            "template only future work",
            "stub-only future work",
            "stub only future work",
            "deferred-spec-only work",
            "deferred spec only work");
        if (!hasPlaceholderTitle)
            return false;

        return ContainsAny(
            contextText,
            "placeholder",
            "placeholders",
            "template",
            "templates",
            "stub",
            "stubs",
            "deferred",
            "future",
            "spec",
            "specification",
            "specifications",
            "contract",
            "contracts",
            "model",
            "models",
            "record",
            "records",
            "guidance",
            "rules",
            "remediation",
            "notification",
            "core",
            "src\\",
            "/src/",
            ".cs",
            "write_file");
    }

    private static bool LooksLikeEvidenceValidationContext(string? contextText)
    {
        if (string.IsNullOrWhiteSpace(contextText))
            return false;

        var hasEvidenceLanguage = ContainsAny(
            contextText,
            "evidence of success",
            "proof of completion",
            "validation evidence",
            "acceptance evidence",
            "success evidence",
            "proof of success",
            "required result artifacts",
            "required result artifact",
            "result artifacts",
            "result artifact",
            "required output artifacts",
            "required output artifact",
            "output artifacts",
            "output artifact",
            "generated outputs",
            "generated output",
            "required deliverables",
            "required deliverable",
            "completion artifacts",
            "completion artifact",
            "result package",
            "result packages",
            "result proof",
            "result proofs",
            "proof artifact",
            "proof artifacts",
            "capture proof",
            "capture proof artifact",
            "capture proof artifacts");
        if (!hasEvidenceLanguage)
        {
            hasEvidenceLanguage = ContainsAny(
                contextText,
                "acceptance",
                "acceptance gate",
                "final acceptance",
                "startup verification",
                "startup verify",
                "launch verification",
                "launch verify",
                "success criteria",
                "success proof",
                "completion proof")
                && ContainsAny(
                    contextText,
                    "build",
                    "builds successfully",
                "artifact",
                "result",
                "results",
                "output",
                "outputs",
                "deliverable",
                "deliverables",
                "completion",
                "package",
                "proof",
                ".csproj",
                ".sln",
                    ".xaml",
                    ".cs",
                    "workspace",
                    "solution",
                    "project");
        }
        if (!hasEvidenceLanguage)
        {
            hasEvidenceLanguage = ContainsAny(contextText, "validate", "validation", "verify", "verification")
                && ContainsAny(
                    contextText,
                    "build",
                    "builds successfully",
                    "artifact",
                    "result",
                    "results",
                    "output",
                    "outputs",
                    "deliverable",
                    "deliverables",
                    "completion",
                    "package",
                    "proof",
                    ".csproj",
                    ".sln",
                    ".xaml",
                    ".cs",
                    "workspace",
                    "solution",
                    "project");
        }
        if (!hasEvidenceLanguage)
            return false;

        return ContainsAny(
            contextText,
            "validate",
            "validation",
            "verify",
            "verification",
            "acceptance",
            "startup verification",
            "startup verify",
            "build",
            "builds successfully",
            "artifact",
            "result",
            "results",
            "output",
            "outputs",
            "deliverable",
            "deliverables",
            "completion",
            "package",
            "evidence",
            "exists",
            ".csproj",
            ".sln",
            ".xaml",
            ".cs",
            "workspace",
            "solution",
            "project",
            "shellviewmodel",
            "dashboardpage",
            "findingspage",
            "historypage",
            "settingspage",
            "proof artifact",
            "proof artifacts",
            "capture proof",
            "capture proof artifact",
            "capture proof artifacts",
            "builds successfully");
    }

    private static string InferDotnetProjectOperation(string hint)
    {
        var identity = FileIdentityService.Identify(hint);
        if (LooksLikePlainSiblingProjectScaffold(hint))
            return "create_project";

        if (string.Equals(identity.Role, "tests", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(hint, "tests", ".tests"))
            return "create_test_project";

        if (identity.Role is "core" or "contracts" or "models" or "repository"
            || ContainsAny(hint, ".core", "contracts", "models", "repository", "domain"))
            return "create_core_library";

        return "create_project";
    }

    private static string InferMakeDirOperation(string path)
    {
        var identity = FileIdentityService.Identify(path);
        if (string.Equals(identity.Role, "state", StringComparison.OrdinalIgnoreCase))
            return "make_state_dir";
        if (string.Equals(identity.Role, "storage", StringComparison.OrdinalIgnoreCase))
            return "make_storage_dir";
        if (string.Equals(identity.Role, "contracts", StringComparison.OrdinalIgnoreCase))
            return "make_contracts_dir";
        if (string.Equals(identity.Role, "models", StringComparison.OrdinalIgnoreCase))
            return "make_models_dir";
        if (ContainsAny(path, "include"))
            return "make_include_dir";
        if (ContainsAny(path, "src"))
            return "make_src_dir";

        return "make_dir";
    }

    private static string InferNativeSourceOperation(string path)
    {
        if (ContainsAny(path, "appwindow.cpp"))
            return "write_app_window_source";
        if (ContainsAny(path, "main.cpp"))
            return "write_main_cpp";
        if (ContainsAny(path, "settingsstore.cpp"))
            return "write_storage_source";

        return "write_cpp_source";
    }

    private static string InferNativeHeaderOperation(string path)
    {
        if (ContainsAny(path, "appwindow.h"))
            return "write_app_window_header";
        if (ContainsAny(path, "navigationitem.h"))
            return "write_navigation_header";
        if (ContainsAny(path, "appstate.h"))
            return "write_app_state_header";
        if (ContainsAny(path, "settingsstore.h"))
            return "write_storage_header";
        if (ContainsAny(path, "settingspanel.h"))
            return "write_settings_panel";
        if (ContainsAny(path, "findingspanel.h"))
            return "write_findings_panel";
        if (ContainsAny(path, "historypanel.h"))
            return "write_history_panel";
        if (ContainsAny(path, "dashboardpanel.h"))
            return "write_dashboard_panel";
        if (ContainsAny(path, "checkdefinition.h"))
            return "write_contract_header";
        if (ContainsAny(path, "findingrecord.h"))
            return "write_domain_model_header";

        return "write_cpp_header";
    }

    private static string InferWriteFileOperation(string hint)
    {
        var identity = FileIdentityService.Identify(hint);
        if (ContainsAny(hint, "repository", "snapshotrepository", "settingsrepository"))
            return ContainsAny(hint, "interface", "contract") ? "write_repository_contract" : "write_repository_impl";
        if (ContainsAny(hint, "check registry"))
            return "write_check_registry";
        if (ContainsAny(hint, "snapshot builder"))
            return "write_snapshot_builder";
        if (ContainsAny(hint, "findings normalizer", "normalize findings"))
            return "write_findings_normalizer";
        if (identity.Role == "storage")
            return ContainsAny(hint, "interface", "contract") ? "write_storage_contract" : "write_storage_impl";
        if (identity.Role is "contracts" or "models" or "core")
            return ContainsAny(hint, "contract", "interface") ? "write_contract_file" : "write_domain_model_file";
        if (identity.Role == "state")
            return "write_app_state";
        if (identity.Role is "views" or "ui")
            return ContainsAny(hint, "viewmodel") ? "write_shell_viewmodel" : "write_page";
        if (ContainsAny(hint, "contract", "interface"))
            return "write_contract_file";
        if (ContainsAny(hint, "model", "record"))
            return "write_domain_model_file";

        return "write_file";
    }

    private static string InferPhraseFamily(TaskboardWorkItemRunStateRecord item, string toolName)
    {
        if (!IsMissingOrUnknown(item.WorkFamily))
        {
            return item.WorkFamily switch
            {
                "solution_scaffold" => "solution_scaffold",
                "ui_shell_sections" => "ui_shell_sections",
                "ui_wiring" => "add_navigation_app_state",
                "app_state_wiring" => "add_navigation_app_state",
                "viewmodel_scaffold" => "ui_shell_sections",
                "storage_bootstrap" => "setup_storage_layer",
                "core_domain_models_contracts" => "core_domain_models_contracts",
                "repository_scaffold" => "repository_scaffold",
                "check_runner" => "check_runner",
                "findings_pipeline" => "findings_pipeline",
                "build_verify" => "build_verify",
                "build_repair" => "build_failure_repair",
                "native_project_bootstrap" => "native_project_bootstrap",
                _ => ""
            };
        }

        return toolName switch
        {
            "create_dotnet_solution" or "create_dotnet_project" or "add_project_to_solution" => "solution_scaffold",
            "create_dotnet_page_view" or "create_dotnet_viewmodel" => "ui_shell_sections",
            "register_navigation" => ContainsAny(item.Title, "shell registration", "navigation") ? "add_navigation_app_state" : "ui_shell_sections",
            "initialize_sqlite_storage_boundary" or "register_di_service" => "setup_storage_layer",
            "show_artifacts" or "show_memory" => "maintenance_context",
            "dotnet_build" or "dotnet_test" => "build_verify",
            "plan_repair" or "preview_patch_draft" or "apply_patch_draft" or "verify_patch_draft" => "build_failure_repair",
            "create_cmake_project" => "native_project_bootstrap",
            "cmake_configure" or "cmake_build" or "ctest_run" => "build_verify",
            _ => ""
        };
    }

    private static string InferTemplateId(TaskboardWorkItemRunStateRecord item)
    {
        var stack = NormalizeValue(item.TargetStack);
        var phrase = NormalizeValue(item.PhraseFamily);
        var operation = NormalizeValue(item.OperationKind);

        if (string.Equals(stack, "dotnet_desktop", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(operation, "run_test_project", StringComparison.OrdinalIgnoreCase))
                return "workspace.test_verify.v1";

            return phrase switch
            {
                "solution_scaffold" => "dotnet.solution_scaffold.v1",
                "ui_shell_sections" => "dotnet.shell_page_set_scaffold.v1",
                "add_navigation_app_state" => "dotnet.navigation_wireup.v1",
                "setup_storage_layer" => "dotnet.sqlite_storage_bootstrap.v1",
                "core_domain_models_contracts" => "dotnet.domain_contracts_scaffold.v1",
                "repository_scaffold" => "dotnet.repository_scaffold.v1",
                "maintenance_context" => "artifact_inspection_single_step",
                "check_runner" => "dotnet.check_runner_scaffold.v1",
                "findings_pipeline" => "dotnet.findings_pipeline_bootstrap.v1",
                "build_verify" => "workspace.build_verify.v1",
                "build_failure_repair" or "solution_graph_repair" => "repair_execution_chain",
                _ => operation switch
                {
                    "write_shell_registration" => "dotnet.shell_registration_wireup.v1",
                    "write_app_state" => "dotnet.navigation_wireup.v1",
                    "write_shell_viewmodel" => "dotnet.page_and_viewmodel_scaffold.v1",
                    "inspect_solution_wiring" or "inspect_project_reference_graph" or "repair_project_attachment" or "repair_generated_build_targets" => "repair_execution_chain",
                    _ => ""
                }
            };
        }

        if (string.Equals(stack, "native_cpp_desktop", StringComparison.OrdinalIgnoreCase))
        {
            return phrase switch
            {
                "native_project_bootstrap" or "cmake_bootstrap" or "solution_scaffold" => "cmake.project_bootstrap.v1",
                "ui_shell_sections" => "cpp.win32_shell_page_set.v1",
                "maintenance_context" => "artifact_inspection_single_step",
                "build_verify" => "workspace.native_build_verify.v1",
                "build_failure_repair" or "solution_graph_repair" => "repair_execution_chain",
                "repository_scaffold" or "setup_storage_layer" or "findings_pipeline" => "cpp.library_scaffold.v1",
                _ => ""
            };
        }

        return "";
    }

    private static string InferWorkFamily(TaskboardWorkItemRunStateRecord item)
    {
        var operation = NormalizeValue(item.OperationKind);
        if (!string.IsNullOrWhiteSpace(operation))
        {
            return operation switch
            {
                "create_solution" or "create_project" or "add_project_to_solution" => "solution_scaffold",
                "create_core_library" or "attach_core_library" or "add_domain_reference" or "make_contracts_dir" or "make_models_dir" or "write_contract_file" or "write_domain_model_file" => "core_domain_models_contracts",
                "shell_section_create" or "shell_behavior_extend" or "shell_layout_update" => "ui_shell_sections",
                "write_page" or "write_dashboard_panel" or "write_findings_panel" or "write_history_panel" or "write_settings_panel" or "write_app_window_header" or "write_app_window_source" or "write_main_cpp" => "ui_shell_sections",
                "write_navigation_item" or "write_shell_registration" or "write_navigation_header" => "ui_wiring",
                "write_app_state" or "write_app_state_header" => "app_state_wiring",
                "write_shell_viewmodel" => "viewmodel_scaffold",
                "write_storage_contract" or "write_storage_impl" or "write_storage_header" or "write_storage_source" => "storage_bootstrap",
                "inspect_context_artifacts" => "maintenance_context",
                "write_repository_contract" or "write_repository_impl" or "write_contract_file" or "write_domain_model_file" or "write_contract_header" or "write_domain_model_header" => "repository_scaffold",
                "write_check_registry" or "write_snapshot_builder" or "write_findings_normalizer" => "findings_pipeline",
                "build_solution" or "configure_cmake" or "build_native_workspace" => "build_verify",
                "run_test_project" => "check_runner",
                "inspect_solution_wiring" or "inspect_project_reference_graph" or "repair_project_attachment" or "repair_generated_build_targets" => "build_repair",
                "make_src_dir" or "make_include_dir" or "write_cmake_lists" => "native_project_bootstrap",
                _ => ""
            };
        }

        var phrase = NormalizeValue(item.PhraseFamily);
        return phrase switch
        {
            "solution_scaffold" or "project_scaffold" => "solution_scaffold",
            "ui_shell_sections" or "build_first_ui_shell" => "ui_shell_sections",
            "add_navigation_app_state" => "app_state_wiring",
            "setup_storage_layer" => "storage_bootstrap",
            "core_domain_models_contracts" => "core_domain_models_contracts",
            "repository_scaffold" => "repository_scaffold",
            "maintenance_context" => "maintenance_context",
            "check_runner" => "check_runner",
            "findings_pipeline" => "findings_pipeline",
            "build_verify" => "build_verify",
            "build_failure_repair" or "solution_graph_repair" => "build_repair",
            "native_project_bootstrap" or "cmake_bootstrap" => "native_project_bootstrap",
            _ => ""
        };
    }

    private static string ReadArgument(ToolRequest request, string key)
    {
        return request.Arguments.TryGetValue(key, out var value)
            ? value ?? ""
            : "";
    }

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNativePath(string? path)
    {
        return ContainsAny(path, ".cpp", ".h", "cmakelists.txt", "\\include\\", "/include/", "\\src\\", "/src/");
    }

    private static bool IsDotnetPath(string? path)
    {
        return ContainsAny(path, ".cs", ".xaml", ".csproj", ".sln", "\\views\\", "/views/", "\\viewmodels\\", "/viewmodels/");
    }

    private static string NormalizeValue(string? value)
    {
        return (value ?? "").Trim();
    }

    private static string FirstMeaningful(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!IsMissingOrUnknown(value))
                return value!.Trim();
        }

        return "";
    }

    private static bool IsMissingOrUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), "unknown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "(none)", StringComparison.OrdinalIgnoreCase);
    }

    private static void NormalizeDomainContractsRouting(TaskboardWorkItemRunStateRecord item)
    {
        if (!LooksLikeDomainContractsRouting(item))
            return;

        var operation = NormalizeValue(item.OperationKind);
        if (operation == "create_project")
            item.OperationKind = "create_core_library";
        else if (operation == "add_project_to_solution")
            item.OperationKind = "attach_core_library";
        else if (operation == "add_project_reference")
            item.OperationKind = "add_domain_reference";

        item.WorkFamily = "core_domain_models_contracts";
        item.PhraseFamily = "core_domain_models_contracts";

        if (string.Equals(NormalizeValue(item.TargetStack), "native_cpp_desktop", StringComparison.OrdinalIgnoreCase))
        {
            item.TemplateId = "cpp.library_scaffold.v1";
        }
        else
        {
            item.TargetStack = FirstMeaningful(item.TargetStack, "dotnet_desktop");
            item.TemplateId = "dotnet.domain_contracts_scaffold.v1";
        }

        item.TemplateCandidateIds = item.TemplateCandidateIds
            .Where(id => !string.Equals(FirstMeaningful(id), "dotnet.repository_scaffold.v1", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!item.TemplateCandidateIds.Any(id => string.Equals(FirstMeaningful(id), item.TemplateId, StringComparison.OrdinalIgnoreCase)))
            item.TemplateCandidateIds.Add(item.TemplateId);
    }

    private static bool LooksLikeDomainContractsRouting(TaskboardWorkItemRunStateRecord item)
    {
        var operation = NormalizeValue(item.OperationKind);
        if (operation is "create_core_library" or "attach_core_library" or "add_domain_reference" or "make_contracts_dir" or "make_models_dir" or "write_contract_file" or "write_domain_model_file")
            return true;

        if (string.Equals(NormalizeValue(item.PhraseFamily), "core_domain_models_contracts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeValue(item.TemplateId), "dotnet.domain_contracts_scaffold.v1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var text = NormalizeValue(string.Join(" ",
            item.Title,
            item.Summary,
            item.PromptText,
            item.ExpectedArtifact,
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "path"),
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "project_path"),
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "reference_path"),
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "output_path")));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (LooksLikePlainSiblingProjectScaffold(text) || LooksLikePlainSiblingProjectReference(text))
            return false;

        if (ContainsAny(text, "repository", "snapshot", "sqlite", "storage", "settingsstore", "isnapshotrepository"))
            return false;

        return ContainsAny(text, ".core", "core", "contracts", "models", "checkdefinition", "findingrecord");
    }

    private static void NormalizePlainSiblingProjectRouting(TaskboardWorkItemRunStateRecord item)
    {
        var text = NormalizeValue(string.Join(" ",
            item.Title,
            item.Summary,
            item.PromptText,
            item.ExpectedArtifact,
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "path"),
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "project_path"),
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "reference_path"),
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "solution_path"),
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "output_path"),
            ReadArgument(item.DirectToolRequest ?? new ToolRequest(), "project_name")));
        if (string.IsNullOrWhiteSpace(text))
            return;

        var siblingScaffold = LooksLikePlainSiblingProjectScaffold(text);
        var siblingReference = LooksLikePlainSiblingProjectReference(text);
        if (!siblingScaffold && !siblingReference)
            return;

        var operation = NormalizeValue(item.OperationKind);
        if (operation == "create_core_library")
            item.OperationKind = "create_project";
        else if (operation == "attach_core_library")
            item.OperationKind = "add_project_to_solution";
        else if (operation == "add_domain_reference")
            item.OperationKind = "add_project_reference";

        item.WorkFamily = "solution_scaffold";
        item.PhraseFamily = "solution_scaffold";

        if (!string.Equals(NormalizeValue(item.TargetStack), "native_cpp_desktop", StringComparison.OrdinalIgnoreCase))
            item.TargetStack = FirstMeaningful(item.TargetStack, "dotnet_desktop");

        var preferredTemplateId = NormalizeValue(item.OperationKind) is "add_project_to_solution" or "add_project_reference"
            || item.DirectToolRequest?.ToolName is "add_project_to_solution" or "add_dotnet_project_reference"
                ? "dotnet.project_attach.v1"
                : string.Equals(NormalizeValue(item.TargetStack), "native_cpp_desktop", StringComparison.OrdinalIgnoreCase)
                    ? "cmake.project_bootstrap.v1"
                    : "dotnet.solution_scaffold.v1";
        item.TemplateId = preferredTemplateId;
        item.TemplateCandidateIds = item.TemplateCandidateIds
            .Where(id => !string.Equals(NormalizeValue(id), "dotnet.domain_contracts_scaffold.v1", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(NormalizeValue(id), "dotnet.repository_scaffold.v1", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!item.TemplateCandidateIds.Any(id => string.Equals(NormalizeValue(id), preferredTemplateId, StringComparison.OrdinalIgnoreCase)))
            item.TemplateCandidateIds.Add(preferredTemplateId);
    }

    private static void NormalizeTestSupportWriteRouting(TaskboardWorkItemRunStateRecord item)
    {
        var operation = NormalizeValue(item.OperationKind);
        if (!string.Equals(operation, "write_check_registry", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(operation, "write_snapshot_builder", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(operation, "write_findings_normalizer", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        item.WorkFamily = "findings_pipeline";

        if (IsMissingOrUnknown(item.PhraseFamily)
            || string.Equals(NormalizeValue(item.PhraseFamily), "check_runner", StringComparison.OrdinalIgnoreCase))
        {
            item.PhraseFamily = "findings_pipeline";
        }

        if (IsMissingOrUnknown(item.TemplateId)
            || string.Equals(NormalizeValue(item.TemplateId), "dotnet.check_runner_scaffold.v1", StringComparison.OrdinalIgnoreCase))
        {
            item.TemplateId = "dotnet.findings_pipeline_bootstrap.v1";
        }

        if (item.TemplateCandidateIds.Count > 0)
        {
            item.TemplateCandidateIds = item.TemplateCandidateIds
                .Where(id => !string.Equals(NormalizeValue(id), "dotnet.check_runner_scaffold.v1", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (!item.TemplateCandidateIds.Any(id => string.Equals(NormalizeValue(id), "dotnet.findings_pipeline_bootstrap.v1", StringComparison.OrdinalIgnoreCase)))
        {
            item.TemplateCandidateIds.Add("dotnet.findings_pipeline_bootstrap.v1");
        }
    }
}
