using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardExecutionLaneResolutionService
{
    public const string ResolverContractVersion = "execution_lane_resolution.v3";

    private static readonly Dictionary<string, string[]> RequiredArgumentsByToolName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["create_dotnet_solution"] = ["solution_name"],
        ["create_dotnet_project"] = ["template", "project_name", "output_path"],
        ["add_project_to_solution"] = ["solution_path", "project_path"],
        ["add_dotnet_project_reference"] = ["project_path", "reference_path"],
        ["create_dotnet_page_view"] = ["path", "content"],
        ["create_dotnet_viewmodel"] = ["path", "content"],
        ["register_navigation"] = ["path", "content"],
        ["register_di_service"] = ["path", "content"],
        ["initialize_sqlite_storage_boundary"] = ["path", "content"],
        ["create_cmake_project"] = ["path", "content"],
        ["create_cpp_source_file"] = ["path", "content"],
        ["create_cpp_header_file"] = ["path", "content"],
        ["create_c_source_file"] = ["path", "content"],
        ["create_c_header_file"] = ["path", "content"],
        ["make_dir"] = ["path"],
        ["create_file"] = ["path", "content"],
        ["write_file"] = ["path", "content"],
        ["append_file"] = ["path", "content"],
        ["replace_in_file"] = ["path", "old_text", "new_text"],
        ["save_output"] = ["path", "content"],
        ["dotnet_build"] = ["project"],
        ["dotnet_test"] = ["project"],
        ["plan_repair"] = ["scope", "path"],
        ["cmake_configure"] = ["source_dir", "build_dir"],
        ["cmake_build"] = ["build_dir"],
        ["ctest_run"] = ["build_dir"],
        ["make_build"] = ["directory"],
        ["ninja_build"] = ["directory"],
        ["run_build_script"] = ["path"]
    };

    private static readonly Dictionary<string, string> DotnetOperationToolMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["create_solution"] = "create_dotnet_solution",
        ["create_project"] = "create_dotnet_project",
        ["create_test_project"] = "create_dotnet_project",
        ["create_core_library"] = "create_dotnet_project",
        ["add_project_to_solution"] = "add_project_to_solution",
        ["attach_test_project"] = "add_project_to_solution",
        ["add_domain_reference"] = "add_dotnet_project_reference",
        ["add_project_reference"] = "add_dotnet_project_reference",
        ["write_shell_layout"] = "create_dotnet_page_view",
        ["write_page"] = "create_dotnet_page_view",
        ["write_navigation_item"] = "register_navigation",
        ["write_shell_registration"] = "register_navigation",
        ["write_app_state"] = "create_dotnet_viewmodel",
        ["write_shell_viewmodel"] = "create_dotnet_viewmodel",
        ["make_state_dir"] = "make_dir",
        ["make_storage_dir"] = "make_dir",
        ["make_contracts_dir"] = "make_dir",
        ["make_models_dir"] = "make_dir",
        ["inspect_context_artifacts"] = "show_artifacts",
        ["write_storage_contract"] = "initialize_sqlite_storage_boundary",
        ["write_storage_impl"] = "register_di_service",
        ["write_repository_contract"] = "write_file",
        ["write_repository_impl"] = "write_file",
        ["write_contract_file"] = "write_file",
        ["write_domain_model_file"] = "write_file",
        ["write_check_registry"] = "write_file",
        ["write_snapshot_builder"] = "write_file",
        ["write_findings_normalizer"] = "write_file",
        ["build_solution"] = "dotnet_build",
        ["run_test_project"] = "dotnet_test",
        ["inspect_solution_wiring"] = "plan_repair",
        ["inspect_project_reference_graph"] = "plan_repair",
        ["repair_project_attachment"] = "plan_repair",
        ["repair_generated_build_targets"] = "plan_repair"
    };

    private static readonly Dictionary<string, string> NativeOperationToolMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["make_src_dir"] = "make_dir",
        ["make_include_dir"] = "make_dir",
        ["make_state_dir"] = "make_dir",
        ["make_storage_dir"] = "make_dir",
        ["make_contracts_dir"] = "make_dir",
        ["make_models_dir"] = "make_dir",
        ["inspect_context_artifacts"] = "show_artifacts",
        ["write_cmake_lists"] = "create_cmake_project",
        ["write_app_window_header"] = "create_cpp_header_file",
        ["write_app_window_source"] = "create_cpp_source_file",
        ["write_main_cpp"] = "create_cpp_source_file",
        ["write_navigation_header"] = "create_cpp_header_file",
        ["write_app_state_header"] = "create_cpp_header_file",
        ["write_storage_header"] = "create_cpp_header_file",
        ["write_storage_source"] = "create_cpp_source_file",
        ["write_settings_panel"] = "create_cpp_header_file",
        ["write_findings_panel"] = "create_cpp_header_file",
        ["write_history_panel"] = "create_cpp_header_file",
        ["write_dashboard_panel"] = "create_cpp_header_file",
        ["write_contract_header"] = "create_cpp_header_file",
        ["write_domain_model_header"] = "create_cpp_header_file",
        ["configure_cmake"] = "cmake_configure",
        ["build_native_workspace"] = "cmake_build"
    };

    private static readonly Dictionary<string, string> DotnetPhraseFamilyChainMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["solution_scaffold"] = "dotnet.solution_scaffold.v1",
        ["project_scaffold"] = "dotnet.solution_scaffold.v1",
        ["ui_shell_sections"] = "dotnet.shell_page_set_scaffold.v1",
        ["build_first_ui_shell"] = "dotnet.desktop_shell_scaffold.v1",
        ["add_navigation_app_state"] = "dotnet.navigation_wireup.v1",
        ["setup_storage_layer"] = "dotnet.sqlite_storage_bootstrap.v1",
        ["core_domain_models_contracts"] = "dotnet.domain_contracts_scaffold.v1",
        ["repository_scaffold"] = "dotnet.repository_scaffold.v1",
        ["maintenance_context"] = "artifact_inspection_single_step",
        ["add_settings_page"] = "dotnet.page_and_viewmodel_scaffold.v1",
        ["add_history_log_view"] = "dotnet.page_and_viewmodel_scaffold.v1",
        ["wire_dashboard"] = "dotnet.page_and_viewmodel_scaffold.v1",
        ["check_runner"] = "dotnet.check_runner_scaffold.v1",
        ["findings_pipeline"] = "dotnet.findings_pipeline_bootstrap.v1",
        ["build_verify"] = "workspace.build_verify.v1",
        ["build_failure_repair"] = "repair_execution_chain",
        ["solution_graph_repair"] = "repair_execution_chain"
    };

    private static readonly Dictionary<string, string> NativePhraseFamilyChainMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["solution_scaffold"] = "cmake.project_bootstrap.v1",
        ["project_scaffold"] = "cmake.project_bootstrap.v1",
        ["native_project_bootstrap"] = "cmake.project_bootstrap.v1",
        ["cmake_bootstrap"] = "cmake.project_bootstrap.v1",
        ["ui_shell_sections"] = "cpp.win32_shell_page_set.v1",
        ["build_first_ui_shell"] = "cpp.win32_shell_scaffold.v1",
        ["add_navigation_app_state"] = "cpp.win32_shell_scaffold.v1",
        ["setup_storage_layer"] = "cpp.library_scaffold.v1",
        ["core_domain_models_contracts"] = "cpp.library_scaffold.v1",
        ["repository_scaffold"] = "cpp.library_scaffold.v1",
        ["maintenance_context"] = "artifact_inspection_single_step",
        ["add_settings_page"] = "cmake.target_attach.v1",
        ["add_history_log_view"] = "cmake.target_attach.v1",
        ["wire_dashboard"] = "cmake.target_attach.v1",
        ["findings_pipeline"] = "cpp.library_scaffold.v1",
        ["build_verify"] = "workspace.native_build_verify.v1",
        ["build_failure_repair"] = "repair_execution_chain",
        ["solution_graph_repair"] = "repair_execution_chain"
    };

    private readonly TaskboardBuilderOperationResolutionService _builderOperationResolutionService = new();
    private readonly BuilderRequestClassifier _builderRequestClassifier = new();
    private readonly CSharpExecutionCoverageService _cSharpExecutionCoverageService = new();
    private readonly FileIdentityService _fileIdentityService = new();
    private readonly ResponseModeSelectionService _responseModeSelectionService = new();
    private readonly ToolChainTemplateRegistry _toolChainTemplateRegistry = new();
    private readonly ToolRegistryService _toolRegistryService = new();
    private readonly UserInputResolutionService _userInputResolutionService = new();
    private readonly WorkspaceBuildIndexService _workspaceBuildIndexService = new();
    private readonly TaskboardWorkFamilyResolutionService _workFamilyResolutionService = new();

    public TaskboardExecutionLaneResolution Resolve(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle,
        string activeTargetRelativePath)
    {
        var prompt = NormalizePromptText(FirstNonEmpty(workItem.PromptText, workItem.Title));
        var workFamilyResolution = _workFamilyResolutionService.Resolve(workItem);
        var isBuilderLane = IsBuilderLaneWorkItem(workItem);
        var requestKind = _builderRequestClassifier.Classify(prompt);
        var effectiveRequestKind = isBuilderLane && requestKind == BuilderRequestKind.NormalQuestion
            ? BuilderRequestKind.BuildRequest
            : requestKind;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return BuildBlockedResolution(
                workspaceRoot,
                workItem,
                prompt,
                effectiveRequestKind,
                TaskboardExecutionEligibilityKind.BlockedUnsafe,
                TaskboardExecutionLaneBlockerCode.EmptyPrompt,
                "Taskboard auto-run blocked: work item has no deterministic prompt text.",
                workFamilyResolution: workFamilyResolution);
        }

        if (workItem.DirectToolRequest is not null)
        {
            return ResolveFromToolRequest(
                workspaceRoot,
                workItem,
                prompt,
                effectiveRequestKind,
                DetermineEligibility(workItem.DirectToolRequest.ToolName),
                workItem.DirectToolRequest.Clone(),
                FirstNonEmpty(workItem.DirectToolRequest.Reason, $"Resolved taskboard work item `{workItem.Title}` from a direct tool request."),
                "direct_tool_request",
                workFamilyResolution);
        }

        var builderResolution = _builderOperationResolutionService.Resolve(workspaceRoot, workItem, planTitle);
        if (builderResolution.Matched)
        {
            if (builderResolution.Eligibility is TaskboardExecutionEligibilityKind.ManualOnlyElevated
                or TaskboardExecutionEligibilityKind.ManualOnlySystemMutation
                or TaskboardExecutionEligibilityKind.ManualOnlyAmbiguous)
            {
                return BuildManualOnlyResolution(
                    workspaceRoot,
                    workItem,
                    prompt,
                    effectiveRequestKind,
                    builderResolution.Eligibility,
                    builderResolution.Reason,
                    builderResolution.ResolvedTargetPath,
                    workFamilyResolution,
                    BuildBuilderResolution(builderResolution));
            }

            if (builderResolution.Eligibility == TaskboardExecutionEligibilityKind.BlockedUnsafe)
            {
                return BuildBlockedResolution(
                    workspaceRoot,
                    workItem,
                    prompt,
                    effectiveRequestKind,
                    builderResolution.Eligibility,
                    TaskboardExecutionLaneBlockerCode.UnsafeBlocked,
                    builderResolution.Reason,
                    builderResolution.ResolvedTargetPath,
                    workFamilyResolution: workFamilyResolution,
                    evidenceRequest: BuildBuilderResolution(builderResolution));
            }

            return ResolveFromToolRequest(
                workspaceRoot,
                workItem,
                prompt,
                effectiveRequestKind,
                builderResolution.Eligibility,
                BuildBuilderResolution(builderResolution),
                builderResolution.Reason,
                "builder_operation_resolution",
                workFamilyResolution);
        }

        var resolvedIntent = _userInputResolutionService.Resolve(prompt, effectiveRequestKind, activeTargetRelativePath, workspaceRoot);
        if (resolvedIntent is not null)
        {
            return ResolveFromToolRequest(
                workspaceRoot,
                workItem,
                prompt,
                effectiveRequestKind,
                DetermineEligibility(resolvedIntent.ToolRequest.ToolName),
                resolvedIntent.ToolRequest.Clone(),
                resolvedIntent.ResolutionReason,
                "user_input_resolution",
                workFamilyResolution);
        }

        var catalogCandidates = BuildCatalogCandidates(workItem);
        var viableCandidateCount = catalogCandidates.Count(candidate => candidate.IsViable);
        var hasViableToolCandidate = catalogCandidates.Any(candidate =>
            candidate.IsViable && candidate.LaneKind == TaskboardExecutionLaneKind.ToolLane);
        var preferredChainOverUnboundTool = TryResolvePreferredChainOverUnboundTool(
            workspaceRoot,
            workItem,
            prompt,
            effectiveRequestKind,
            catalogCandidates,
            workFamilyResolution);
        if (preferredChainOverUnboundTool is not null)
            return preferredChainOverUnboundTool;

        var unsupportedCSharpCoverage = TryResolveUnsupportedCSharpCoverage(
            workspaceRoot,
            workItem,
            prompt,
            effectiveRequestKind,
            catalogCandidates,
            workFamilyResolution);
        if (unsupportedCSharpCoverage is not null)
            return unsupportedCSharpCoverage;

        if (viableCandidateCount > 1
            && !string.IsNullOrWhiteSpace(workItem.OperationKind)
            && !hasViableToolCandidate)
        {
            var blockerCode = ResolveCoverageBlockerCode(workFamilyResolution, workItem.TargetStack, TaskboardExecutionLaneBlockerCode.MissingToolLaneForOperationKind);
            return BuildBlockedResolution(
                workspaceRoot,
                workItem,
                prompt,
                effectiveRequestKind,
                TaskboardExecutionEligibilityKind.BlockedUnsafe,
                blockerCode,
                BuildCoverageBlockerMessage(workItem, workFamilyResolution, workItem.TargetStack, blockerCode),
                catalogCandidates: catalogCandidates,
                workFamilyResolution: workFamilyResolution);
        }

        if (catalogCandidates.Count > 1 && viableCandidateCount > 1)
        {
            return BuildBlockedResolution(
                workspaceRoot,
                workItem,
                prompt,
                effectiveRequestKind,
                TaskboardExecutionEligibilityKind.BlockedUnsafe,
                TaskboardExecutionLaneBlockerCode.AmbiguousLaneCandidates,
                $"Taskboard auto-run blocked: `{workItem.Title}` matched multiple execution lanes and no deterministic winner could be chosen for work_family={FirstNonEmpty(NormalizeValue(workFamilyResolution.FamilyId), "unknown")}.",
                catalogCandidates: catalogCandidates,
                workFamilyResolution: workFamilyResolution);
        }

        if (catalogCandidates.Count > 0)
        {
            var viableCandidate = catalogCandidates.FirstOrDefault(candidate => candidate.IsViable);
            if (viableCandidate is not null && !string.IsNullOrWhiteSpace(viableCandidate.ToolId))
            {
                var blockerCode = string.IsNullOrWhiteSpace(workItem.TemplateId)
                    ? TaskboardExecutionLaneBlockerCode.MissingRequiredArgumentForLane
                    : TaskboardExecutionLaneBlockerCode.MissingTemplateSelection;
                var blockerMessage = blockerCode == TaskboardExecutionLaneBlockerCode.MissingTemplateSelection
                    ? $"Taskboard auto-run blocked: template `{workItem.TemplateId}` identified a lane candidate for `{workItem.Title}`, but no bound tool arguments were produced."
                    : $"Taskboard auto-run blocked: `{workItem.Title}` identified a deterministic lane candidate, but required lane arguments were not bound.";
                return BuildBlockedResolution(
                    workspaceRoot,
                    workItem,
                    prompt,
                    effectiveRequestKind,
                    TaskboardExecutionEligibilityKind.BlockedUnsafe,
                    blockerCode,
                    blockerMessage,
                    catalogCandidates: catalogCandidates,
                    workFamilyResolution: workFamilyResolution);
            }
        }

        if (isBuilderLane)
        {
            var blockerCode = !string.IsNullOrWhiteSpace(workItem.OperationKind)
                ? TaskboardExecutionLaneBlockerCode.MissingToolLaneForOperationKind
                : !string.IsNullOrWhiteSpace(workItem.TemplateId)
                    ? TaskboardExecutionLaneBlockerCode.MissingTemplateSelection
                    : !string.IsNullOrWhiteSpace(workItem.PhraseFamily)
                        ? TaskboardExecutionLaneBlockerCode.MissingChainLaneForPhraseFamily
                        : TaskboardExecutionLaneBlockerCode.NoLaneCandidates;
            var resolvedBlockerCode = ResolveCoverageBlockerCode(workFamilyResolution, workItem.TargetStack, blockerCode);
            var blockerMessage = BuildCoverageBlockerMessage(workItem, workFamilyResolution, workItem.TargetStack, resolvedBlockerCode);
            return BuildBlockedResolution(
                workspaceRoot,
                workItem,
                prompt,
                effectiveRequestKind,
                TaskboardExecutionEligibilityKind.BlockedUnsafe,
                resolvedBlockerCode,
                blockerMessage,
                catalogCandidates: catalogCandidates,
                workFamilyResolution: workFamilyResolution);
        }

        var finalBlockerCode = ResolveCoverageBlockerCode(workFamilyResolution, workItem.TargetStack, TaskboardExecutionLaneBlockerCode.NoLaneCandidates);
        return BuildBlockedResolution(
            workspaceRoot,
            workItem,
            prompt,
            effectiveRequestKind,
            TaskboardExecutionEligibilityKind.BlockedUnsafe,
            finalBlockerCode,
            BuildCoverageBlockerMessage(workItem, workFamilyResolution, workItem.TargetStack, finalBlockerCode),
            catalogCandidates: catalogCandidates,
            workFamilyResolution: workFamilyResolution);
    }

    private TaskboardExecutionLaneResolution ResolveFromToolRequest(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string prompt,
        BuilderRequestKind requestKind,
        TaskboardExecutionEligibilityKind eligibility,
        ToolRequest request,
        string resolutionReason,
        string selectionPath,
        TaskboardWorkFamilyResolution? workFamilyResolution)
    {
        var toolName = NormalizeValue(request.ToolName);
        var resolvedTargetPath = ExtractTargetPath(request);
        var candidates = BuildCatalogCandidates(workItem);
        var chainCandidates = ResolveChainTemplateCandidates(workItem, toolName);
        candidates = MergeCandidates(candidates, chainCandidates);
        candidates.Insert(0, new TaskboardExecutionLaneCandidate
        {
            CandidateId = BuildCandidateId(TaskboardExecutionLaneKind.ToolLane, toolName, ""),
            LaneKind = TaskboardExecutionLaneKind.ToolLane,
            ToolId = toolName,
            Source = selectionPath,
            Reason = resolutionReason,
            IsViable = true
        });

        if (!_toolRegistryService.HasTool(toolName))
        {
            return BuildBlockedResolution(
                workspaceRoot,
                workItem,
                prompt,
                requestKind,
                TaskboardExecutionEligibilityKind.BlockedUnsafe,
                TaskboardExecutionLaneBlockerCode.UnknownToolLaneTarget,
                $"Taskboard auto-run blocked: `{workItem.Title}` resolved to unknown tool `{request.ToolName}`.",
                resolvedTargetPath,
                catalogCandidates: candidates,
                workFamilyResolution: workFamilyResolution,
                evidenceRequest: request);
        }

        if (!IsToolSupportedForStack(toolName, NormalizeValue(workItem.TargetStack)))
        {
            return BuildBlockedResolution(
                workspaceRoot,
                workItem,
                prompt,
                requestKind,
                TaskboardExecutionEligibilityKind.BlockedUnsafe,
                TaskboardExecutionLaneBlockerCode.UnsupportedStackLaneMapping,
                $"Taskboard auto-run blocked: stack `{FirstNonEmpty(NormalizeValue(workItem.TargetStack), "unknown")}` does not support tool lane `{toolName}` for operation_kind={FirstNonEmpty(NormalizeValue(workItem.OperationKind), "unknown")}.",
                resolvedTargetPath,
                catalogCandidates: candidates,
                workFamilyResolution: workFamilyResolution,
                evidenceRequest: request);
        }

        var selectedChainCandidate = chainCandidates.FirstOrDefault(candidate => candidate.IsViable);
        var selectedChainTemplate = selectedChainCandidate?.ChainTemplateId ?? "";
        if (string.Equals(toolName, "dotnet_test", StringComparison.OrdinalIgnoreCase))
        {
            var requestedProject = request.TryGetArgument("project", out var projectArgument) ? projectArgument : "";
            var activeTarget = request.TryGetArgument("active_target", out var activeTargetArgument) ? activeTargetArgument : "";
            var testResolution = _workspaceBuildIndexService.ResolveForTesting(workspaceRoot, requestedProject, activeTarget);
            var alternativeTargets = testResolution.Candidates
                .Select(candidate => candidate.RelativePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();

            if (testResolution.Success && testResolution.Item is not null)
            {
                resolvedTargetPath = testResolution.Item.RelativePath;
                request.Arguments["project"] = resolvedTargetPath;
                request.Arguments["test_target_resolution_kind"] = "resolved_target_present";
                request.Arguments["test_target_resolution_summary"] = testResolution.Message;
                if (alternativeTargets.Count > 0)
                    request.Arguments["test_target_discovered_alternatives"] = string.Join(" | ", alternativeTargets);
            }
            else if (ShouldDeferMissingTestTargetToPrerequisites(workItem, requestedProject, testResolution))
            {
                selectedChainTemplate = "dotnet.check_runner_scaffold.v1";
                resolutionReason = $"Direct test routing deferred: requested test target `{DisplayValue(testResolution.NormalizedTarget)}` is not yet present/discoverable, so check-runner prerequisites must run before `dotnet_test`. {BuildWorkspaceResolutionReason(testResolution)}";
                request.Arguments["test_target_resolution_kind"] = FirstNonEmpty(testResolution.ReasonCode, "missing_test_target");
                request.Arguments["test_target_resolution_summary"] = resolutionReason;
                request.Arguments["test_target_requested_path"] = NormalizeValueForArgument(testResolution.NormalizedTarget);
                if (alternativeTargets.Count > 0)
                    request.Arguments["test_target_discovered_alternatives"] = string.Join(" | ", alternativeTargets);

                candidates = MergeCandidates(
                    candidates,
                    [
                        new TaskboardExecutionLaneCandidate
                        {
                            CandidateId = BuildCandidateId(TaskboardExecutionLaneKind.ChainLane, "", "dotnet.check_runner_scaffold.v1"),
                            LaneKind = TaskboardExecutionLaneKind.ChainLane,
                            ChainTemplateId = "dotnet.check_runner_scaffold.v1",
                            Source = "missing_test_target_prerequisite",
                            Reason = resolutionReason,
                            IsViable = _toolChainTemplateRegistry.HasTemplate("dotnet.check_runner_scaffold.v1")
                        }
                    ]);
            }
            else
            {
                return BuildBlockedResolution(
                    workspaceRoot,
                    workItem,
                    prompt,
                    requestKind,
                    TaskboardExecutionEligibilityKind.BlockedUnsafe,
                    TaskboardExecutionLaneBlockerCode.UnresolvedWorkspaceTargetForLane,
                    $"Taskboard auto-run blocked: direct test routing succeeded, but the requested test project target was not yet present/discoverable. {BuildWorkspaceResolutionReason(testResolution)}",
                    resolvedTargetPath,
                    blockerDetail: FirstNonEmpty(testResolution.ReasonCode, testResolution.FailureKind, "unresolved_test_target"),
                    catalogCandidates: candidates,
                    workFamilyResolution: workFamilyResolution,
                    evidenceRequest: request);
            }
        }

        var toolCoverage = _cSharpExecutionCoverageService.EvaluateAutoRunToolCandidate(workItem, toolName);
        if (toolCoverage.Relevant && !toolCoverage.IsRunnable)
        {
            return BuildBlockedResolution(
                workspaceRoot,
                workItem,
                prompt,
                requestKind,
                TaskboardExecutionEligibilityKind.BlockedUnsafe,
                TaskboardExecutionLaneBlockerCode.UnsupportedRuntimeCoverage,
                $"Taskboard auto-run blocked: unsupported C# runtime coverage for tool `{toolName}`. {toolCoverage.Summary}",
                resolvedTargetPath,
                blockerDetail: toolCoverage.ReasonCode,
                catalogCandidates: candidates,
                workFamilyResolution: workFamilyResolution,
                evidenceRequest: request);
        }

        var chainCoverage = _cSharpExecutionCoverageService.EvaluateAutoRunChainCandidate(workItem, selectedChainTemplate);
        if (chainCoverage.Relevant && !chainCoverage.IsRunnable)
        {
            var coverageMessage = BuildChainCoverageBlockMessage(selectedChainTemplate, chainCoverage, request);
            return BuildBlockedResolution(
                workspaceRoot,
                workItem,
                prompt,
                requestKind,
                TaskboardExecutionEligibilityKind.BlockedUnsafe,
                TaskboardExecutionLaneBlockerCode.UnsupportedRuntimeCoverage,
                coverageMessage,
                resolvedTargetPath,
                blockerDetail: chainCoverage.ReasonCode,
                catalogCandidates: candidates,
                workFamilyResolution: workFamilyResolution,
                evidenceRequest: request);
        }

        if (TryFindMissingRequiredArgument(toolName, request, out var missingArgument))
        {
            return BuildBlockedResolution(
                workspaceRoot,
                workItem,
                prompt,
                requestKind,
                TaskboardExecutionEligibilityKind.BlockedUnsafe,
                TaskboardExecutionLaneBlockerCode.MissingRequiredArgumentForLane,
                $"Taskboard auto-run blocked: tool lane `{toolName}` requires argument `{missingArgument}`, but the current work item did not provide it.",
                resolvedTargetPath,
                blockerDetail: missingArgument,
                catalogCandidates: candidates,
                workFamilyResolution: workFamilyResolution,
                evidenceRequest: request);
        }

        if (RequiresWorkspaceTarget(toolName) && string.IsNullOrWhiteSpace(resolvedTargetPath))
        {
            return BuildBlockedResolution(
                workspaceRoot,
                workItem,
                prompt,
                requestKind,
                TaskboardExecutionEligibilityKind.BlockedUnsafe,
                TaskboardExecutionLaneBlockerCode.UnresolvedWorkspaceTargetForLane,
                $"Taskboard auto-run blocked: workspace evidence is insufficient to bind a lane target for operation_kind={FirstNonEmpty(NormalizeValue(workItem.OperationKind), "unknown")}.",
                resolvedTargetPath,
                catalogCandidates: candidates,
                workFamilyResolution: workFamilyResolution,
                evidenceRequest: request);
        }

        var responseMode = string.IsNullOrWhiteSpace(selectedChainTemplate)
            ? _responseModeSelectionService.Select(
                prompt,
                requestKind,
                new ResolvedUserIntent
                {
                    ToolRequest = request,
                    ResolutionReason = resolutionReason
                }).Mode
            : ResponseMode.ChainRequired;
        if (responseMode is not ResponseMode.ToolRequired and not ResponseMode.ChainRequired)
        {
            return BuildBlockedResolution(
                workspaceRoot,
                workItem,
                prompt,
                requestKind,
                TaskboardExecutionEligibilityKind.BlockedUnsafe,
                TaskboardExecutionLaneBlockerCode.InvalidResponseModeForLane,
                $"Taskboard auto-run blocked: lane `{toolName}` resolved to response mode `{responseMode}`, which is not executable.",
                resolvedTargetPath,
                catalogCandidates: candidates,
                workFamilyResolution: workFamilyResolution,
                evidenceRequest: request);
        }

        var resolvedIdentity = string.IsNullOrWhiteSpace(resolvedTargetPath)
            ? new FileIdentityRecord()
            : _fileIdentityService.Identify(resolvedTargetPath);
        return new TaskboardExecutionLaneResolution
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            SourceWorkItemId = workItem.WorkItemId,
            SourceWorkItemTitle = workItem.Title,
            OperationKind = NormalizeValue(workItem.OperationKind),
            TargetStack = NormalizeValue(workItem.TargetStack),
            WorkFamily = NormalizeValue(workFamilyResolution?.FamilyId ?? ""),
            WorkFamilySource = NormalizeValue(workFamilyResolution?.Source.ToString() ?? ""),
            WorkFamilyCandidates = workFamilyResolution?.CandidateFamilies is null ? [] : [.. workFamilyResolution.CandidateFamilies],
            PhraseFamily = NormalizeValue(workItem.PhraseFamily),
            TemplateId = NormalizeValue(workItem.TemplateId),
            TemplateCandidateIds = [.. workItem.TemplateCandidateIds],
            PromptText = prompt,
            LaneKind = string.IsNullOrWhiteSpace(selectedChainTemplate)
                ? TaskboardExecutionLaneKind.ToolLane
                : TaskboardExecutionLaneKind.ChainLane,
            SelectedToolId = toolName,
            SelectedChainTemplateId = selectedChainTemplate,
            BoundedArguments = new Dictionary<string, string>(request.Arguments, StringComparer.OrdinalIgnoreCase),
            ResolutionReason = resolutionReason,
            ResolvedTargetPath = resolvedTargetPath,
            SelectionPath = string.IsNullOrWhiteSpace(selectedChainTemplate)
                ? selectionPath
                : $"{selectionPath}->chain_lane",
            Eligibility = eligibility,
            RequestKind = requestKind,
            ResponseMode = responseMode,
            CanonicalOperationKind = request.TryGetArgument("canonical_operation_kind", out var canonicalOperationKind) ? canonicalOperationKind : "",
            CanonicalTargetPath = request.TryGetArgument("canonical_target_path", out var canonicalTargetPath) ? canonicalTargetPath : "",
            CanonicalizationTrace = request.TryGetArgument("canonicalization_trace", out var canonicalizationTrace) ? canonicalizationTrace : "",
            ResolvedTargetIdentity = resolvedIdentity,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            Candidates = candidates,
            Evidence = BuildEvidence(workItem, request, resolvedTargetPath, selectedChainTemplate, workFamilyResolution)
        };
    }

    private static TaskboardExecutionLaneResolution BuildManualOnlyResolution(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string prompt,
        BuilderRequestKind requestKind,
        TaskboardExecutionEligibilityKind eligibility,
        string message,
        string resolvedTargetPath = "",
        TaskboardWorkFamilyResolution? workFamilyResolution = null,
        ToolRequest? evidenceRequest = null)
    {
        var resolvedIdentity = string.IsNullOrWhiteSpace(resolvedTargetPath)
            ? new FileIdentityRecord()
            : new FileIdentityService().Identify(resolvedTargetPath);
        return new TaskboardExecutionLaneResolution
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            SourceWorkItemId = workItem.WorkItemId,
            SourceWorkItemTitle = workItem.Title,
            OperationKind = NormalizeValue(workItem.OperationKind),
            TargetStack = NormalizeValue(workItem.TargetStack),
            WorkFamily = NormalizeValue(workFamilyResolution?.FamilyId ?? ""),
            WorkFamilySource = NormalizeValue(workFamilyResolution?.Source.ToString() ?? ""),
            WorkFamilyCandidates = workFamilyResolution?.CandidateFamilies is null ? [] : [.. workFamilyResolution.CandidateFamilies],
            PhraseFamily = NormalizeValue(workItem.PhraseFamily),
            TemplateId = NormalizeValue(workItem.TemplateId),
            TemplateCandidateIds = [.. workItem.TemplateCandidateIds],
            PromptText = prompt,
            LaneKind = TaskboardExecutionLaneKind.ManualOnlyLane,
            ResolutionReason = message,
            ResolvedTargetPath = resolvedTargetPath,
            SelectionPath = "manual_only_boundary",
            Eligibility = eligibility,
            RequestKind = requestKind,
            ResponseMode = ResponseMode.None,
            CanonicalOperationKind = evidenceRequest is not null && evidenceRequest.TryGetArgument("canonical_operation_kind", out var canonicalOperationKind) ? canonicalOperationKind : "",
            CanonicalTargetPath = evidenceRequest is not null && evidenceRequest.TryGetArgument("canonical_target_path", out var canonicalTargetPath) ? canonicalTargetPath : "",
            CanonicalizationTrace = evidenceRequest is not null && evidenceRequest.TryGetArgument("canonicalization_trace", out var canonicalizationTrace) ? canonicalizationTrace : "",
            ResolvedTargetIdentity = resolvedIdentity,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            Blocker = new TaskboardExecutionLaneBlocker
            {
                Code = TaskboardExecutionLaneBlockerCode.ManualOnlyBoundary,
                Message = message
            },
            Evidence = BuildEvidence(workItem, evidenceRequest, resolvedTargetPath, "", workFamilyResolution)
        };
    }

    private static TaskboardExecutionLaneResolution BuildBlockedResolution(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string prompt,
        BuilderRequestKind requestKind,
        TaskboardExecutionEligibilityKind eligibility,
        TaskboardExecutionLaneBlockerCode blockerCode,
        string message,
        string resolvedTargetPath = "",
        string blockerDetail = "",
        List<TaskboardExecutionLaneCandidate>? catalogCandidates = null,
        TaskboardWorkFamilyResolution? workFamilyResolution = null,
        ToolRequest? evidenceRequest = null)
    {
        var resolvedIdentity = string.IsNullOrWhiteSpace(resolvedTargetPath)
            ? new FileIdentityRecord()
            : new FileIdentityService().Identify(resolvedTargetPath);
        return new TaskboardExecutionLaneResolution
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            SourceWorkItemId = workItem.WorkItemId,
            SourceWorkItemTitle = workItem.Title,
            OperationKind = NormalizeValue(workItem.OperationKind),
            TargetStack = NormalizeValue(workItem.TargetStack),
            WorkFamily = NormalizeValue(workFamilyResolution?.FamilyId ?? ""),
            WorkFamilySource = NormalizeValue(workFamilyResolution?.Source.ToString() ?? ""),
            WorkFamilyCandidates = workFamilyResolution?.CandidateFamilies is null ? [] : [.. workFamilyResolution.CandidateFamilies],
            PhraseFamily = NormalizeValue(workItem.PhraseFamily),
            TemplateId = NormalizeValue(workItem.TemplateId),
            TemplateCandidateIds = [.. workItem.TemplateCandidateIds],
            PromptText = prompt,
            LaneKind = TaskboardExecutionLaneKind.BlockedLane,
            ResolutionReason = message,
            ResolvedTargetPath = resolvedTargetPath,
            SelectionPath = "blocked_lane",
            Eligibility = eligibility,
            RequestKind = requestKind,
            ResponseMode = ResponseMode.None,
            CanonicalOperationKind = evidenceRequest is not null && evidenceRequest.TryGetArgument("canonical_operation_kind", out var canonicalOperationKind) ? canonicalOperationKind : "",
            CanonicalTargetPath = evidenceRequest is not null && evidenceRequest.TryGetArgument("canonical_target_path", out var canonicalTargetPath) ? canonicalTargetPath : "",
            CanonicalizationTrace = evidenceRequest is not null && evidenceRequest.TryGetArgument("canonicalization_trace", out var canonicalizationTrace) ? canonicalizationTrace : "",
            ResolvedTargetIdentity = resolvedIdentity,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            Candidates = catalogCandidates ?? [],
            Blocker = new TaskboardExecutionLaneBlocker
            {
                Code = blockerCode,
                Message = message,
                Detail = blockerDetail
            },
            Evidence = BuildEvidence(workItem, evidenceRequest, resolvedTargetPath, "", workFamilyResolution)
        };
    }

    private static TaskboardExecutionLaneResolution? TryResolvePreferredChainOverUnboundTool(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string prompt,
        BuilderRequestKind requestKind,
        List<TaskboardExecutionLaneCandidate> catalogCandidates,
        TaskboardWorkFamilyResolution workFamilyResolution)
    {
        if (string.IsNullOrWhiteSpace(workItem.TemplateId)
            && workItem.TemplateCandidateIds.Count == 0
            && string.IsNullOrWhiteSpace(workItem.PhraseFamily))
        {
            return null;
        }

        var viableChainCandidate = catalogCandidates.FirstOrDefault(candidate =>
            candidate.IsViable
            && candidate.LaneKind == TaskboardExecutionLaneKind.ChainLane
            && !string.IsNullOrWhiteSpace(candidate.ChainTemplateId));
        var viableToolCandidate = catalogCandidates.FirstOrDefault(candidate =>
            candidate.IsViable
            && candidate.LaneKind == TaskboardExecutionLaneKind.ToolLane
            && !string.IsNullOrWhiteSpace(candidate.ToolId));
        if (viableChainCandidate is null || viableToolCandidate is null)
            return null;

        var probeRequest = new ToolRequest
        {
            ToolName = viableToolCandidate.ToolId
        };
        if (!TryFindMissingRequiredArgument(viableToolCandidate.ToolId, probeRequest, out var missingArgument))
            return null;

        return new TaskboardExecutionLaneResolution
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            SourceWorkItemId = workItem.WorkItemId,
            SourceWorkItemTitle = workItem.Title,
            OperationKind = NormalizeValue(workItem.OperationKind),
            TargetStack = NormalizeValue(workItem.TargetStack),
            WorkFamily = NormalizeValue(workFamilyResolution.FamilyId),
            WorkFamilySource = NormalizeValue(workFamilyResolution.Source.ToString()),
            WorkFamilyCandidates = [.. workFamilyResolution.CandidateFamilies],
            PhraseFamily = NormalizeValue(workItem.PhraseFamily),
            TemplateId = NormalizeValue(workItem.TemplateId),
            TemplateCandidateIds = [.. workItem.TemplateCandidateIds],
            PromptText = prompt,
            LaneKind = TaskboardExecutionLaneKind.ChainLane,
            SelectedToolId = viableToolCandidate.ToolId,
            SelectedChainTemplateId = viableChainCandidate.ChainTemplateId,
            ResolutionReason = $"Taskboard auto-run selected chain `{viableChainCandidate.ChainTemplateId}` for `{workItem.Title}` because the competing tool lane `{viableToolCandidate.ToolId}` remained unbound (missing `{missingArgument}`).",
            ResolvedTargetPath = "",
            SelectionPath = $"{FirstNonEmpty(viableChainCandidate.Source, "preferred_chain")}->chain_over_unbound_tool",
            Eligibility = DetermineEligibility(viableToolCandidate.ToolId),
            RequestKind = requestKind,
            ResponseMode = ResponseMode.ChainRequired,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            Candidates = catalogCandidates,
            Evidence = BuildEvidence(workItem, probeRequest, "", viableChainCandidate.ChainTemplateId ?? "", workFamilyResolution)
        };
    }

    private static string AppendCoverageSummary(string reason, CSharpExecutionCoverageEvaluation coverage)
    {
        if (!coverage.Relevant || string.IsNullOrWhiteSpace(coverage.Summary))
            return reason;

        return $"{reason} coverage={coverage.Status.ToString().ToLowerInvariant()} {coverage.Summary}";
    }

    private static string BuildChainCoverageBlockMessage(
        string selectedChainTemplate,
        CSharpExecutionCoverageEvaluation coverage,
        ToolRequest request)
    {
        if (string.Equals(selectedChainTemplate, "dotnet.check_runner_scaffold.v1", StringComparison.OrdinalIgnoreCase)
            && request.TryGetArgument("test_target_resolution_kind", out var resolutionKind)
            && !string.IsNullOrWhiteSpace(resolutionKind))
        {
            var requestedTarget = request.TryGetArgument("test_target_requested_path", out var requestedPath)
                ? requestedPath
                : "";
            var rerouteSummary = request.TryGetArgument("test_target_resolution_summary", out var summary)
                ? summary
                : "The prerequisite check-runner chain was selected because the requested test target was not yet present/discoverable.";
            return $"Taskboard auto-run blocked: selected prerequisite chain `{selectedChainTemplate}` for missing-test-target continuation, but current C# runtime coverage does not declare that chain as executable. requested_target={FirstNonEmpty(requestedTarget, "(none)")} resolution_kind={resolutionKind}. {rerouteSummary} {coverage.Summary}";
        }

        return $"Taskboard auto-run blocked: unsupported C# runtime coverage for chain `{selectedChainTemplate}`. {coverage.Summary}";
    }

    private static TaskboardExecutionLaneResolution? TryResolveUnsupportedCSharpCoverage(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string prompt,
        BuilderRequestKind requestKind,
        List<TaskboardExecutionLaneCandidate> catalogCandidates,
        TaskboardWorkFamilyResolution workFamilyResolution)
    {
        if (catalogCandidates.Any(candidate => candidate.IsViable))
            return null;

        var unsupportedCandidates = catalogCandidates
            .Where(candidate =>
                !candidate.IsViable
                && (!string.IsNullOrWhiteSpace(candidate.CoverageStatus)
                    || !string.IsNullOrWhiteSpace(candidate.CoverageSummary)))
            .ToList();
        if (unsupportedCandidates.Count == 0)
            return null;

        var summary = string.Join(" | ", unsupportedCandidates
            .Take(3)
            .Select(candidate =>
            {
                var target = FirstNonEmpty(candidate.ChainTemplateId, candidate.ToolId, candidate.CandidateId);
                var coverageStatus = FirstNonEmpty(candidate.CoverageStatus, "unsupported");
                return $"{target}[{coverageStatus}] {FirstNonEmpty(candidate.CoverageSummary, candidate.Reason)}";
            }));
        return BuildBlockedResolution(
            workspaceRoot,
            workItem,
            prompt,
            requestKind,
            TaskboardExecutionEligibilityKind.BlockedUnsafe,
            TaskboardExecutionLaneBlockerCode.UnsupportedRuntimeCoverage,
            $"Taskboard auto-run blocked: unsupported or vocabulary-only C# runtime coverage remains for `{workItem.Title}`. {summary}",
            blockerDetail: summary,
            catalogCandidates: catalogCandidates,
            workFamilyResolution: workFamilyResolution);
    }

    private static ToolRequest BuildBuilderResolution(TaskboardBuilderOperationResolutionResult resolution)
    {
        var request = new ToolRequest
        {
            ToolName = resolution.ToolName,
            Reason = resolution.Reason
        };

        foreach (var argument in resolution.Arguments)
            request.Arguments[argument.Key] = argument.Value;

        if (!string.IsNullOrWhiteSpace(resolution.CanonicalOperationKind))
            request.Arguments["canonical_operation_kind"] = resolution.CanonicalOperationKind;
        if (!string.IsNullOrWhiteSpace(resolution.CanonicalTargetPath))
            request.Arguments["canonical_target_path"] = resolution.CanonicalTargetPath;
        if (!string.IsNullOrWhiteSpace(resolution.CanonicalizationTrace))
            request.Arguments["canonicalization_trace"] = resolution.CanonicalizationTrace;

        return request;
    }

    private List<TaskboardExecutionLaneCandidate> BuildCatalogCandidates(TaskboardRunWorkItem workItem)
    {
        var candidates = new List<TaskboardExecutionLaneCandidate>();
        var operationKind = NormalizeValue(workItem.OperationKind);
        var targetStack = NormalizeValue(workItem.TargetStack);

        if (!string.IsNullOrWhiteSpace(operationKind)
            && TryResolveToolForOperation(targetStack, operationKind, out var toolId))
        {
            var coverage = _cSharpExecutionCoverageService.EvaluateAutoRunToolCandidate(workItem, toolId);
            candidates.Add(new TaskboardExecutionLaneCandidate
            {
                CandidateId = BuildCandidateId(TaskboardExecutionLaneKind.ToolLane, toolId, ""),
                LaneKind = TaskboardExecutionLaneKind.ToolLane,
                ToolId = toolId,
                Source = "operation_catalog",
                Reason = AppendCoverageSummary(
                    $"Operation kind `{operationKind}` maps to tool `{toolId}` for stack `{FirstNonEmpty(targetStack, "unknown")}`.",
                    coverage),
                IsViable = _toolRegistryService.HasTool(toolId)
                    && (!coverage.Relevant || coverage.IsRunnable),
                CoverageStatus = coverage.Status == CSharpExecutionCoverageStatus.Unknown
                    ? ""
                    : coverage.Status.ToString().ToLowerInvariant(),
                CoverageSummary = coverage.Summary
            });
        }

        return MergeCandidates(candidates, ResolveChainTemplateCandidates(workItem, ""));
    }

    private List<TaskboardExecutionLaneCandidate> ResolveChainTemplateCandidates(TaskboardRunWorkItem workItem, string toolName)
    {
        var candidates = new List<TaskboardExecutionLaneCandidate>();
        void AddCandidate(string templateId, string source, string reason)
        {
            if (string.IsNullOrWhiteSpace(templateId))
                return;

            if (candidates.Any(candidate =>
                    string.Equals(candidate.ChainTemplateId, templateId, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var coverage = _cSharpExecutionCoverageService.EvaluateAutoRunChainCandidate(workItem, templateId);
            var candidateReason = AppendCoverageSummary(reason, coverage);
            var isViable = _toolChainTemplateRegistry.HasTemplate(templateId)
                && (!coverage.Relevant || coverage.IsRunnable);
            if (isViable && !string.IsNullOrWhiteSpace(toolName))
            {
                var template = _toolChainTemplateRegistry.ResolveTemplateForName(templateId);
                var templateFit = _toolChainTemplateRegistry.ValidateStep(
                    template,
                    new ToolRequest { ToolName = toolName },
                    null);
                if (!templateFit.Allowed)
                {
                    isViable = false;
                    candidateReason = $"{candidateReason} template_fit={templateFit.BlockerCode.ToString().ToLowerInvariant()} {templateFit.Message}";
                }
            }

            candidates.Add(new TaskboardExecutionLaneCandidate
            {
                CandidateId = BuildCandidateId(TaskboardExecutionLaneKind.ChainLane, "", templateId),
                LaneKind = TaskboardExecutionLaneKind.ChainLane,
                ChainTemplateId = templateId,
                Source = source,
                Reason = candidateReason,
                IsViable = isViable,
                CoverageStatus = coverage.Status == CSharpExecutionCoverageStatus.Unknown
                    ? ""
                    : coverage.Status.ToString().ToLowerInvariant(),
                CoverageSummary = coverage.Summary
            });
        }

        var preferredTemplate = ResolvePreferredChainTemplate(workItem, toolName);
        AddCandidate(
            preferredTemplate,
            "preferred_template",
            $"Operation-specific lane preference resolved chain `{preferredTemplate}`.");

        AddCandidate(
            workItem.TemplateId,
            "template_selection",
            $"Template selection resolved chain `{workItem.TemplateId}`.");

        foreach (var templateId in workItem.TemplateCandidateIds.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            AddCandidate(
                templateId,
                "template_candidate",
                $"Template candidate `{templateId}` remained viable for lane selection.");
        }

        if (candidates.Count == 0)
        {
            var fromPhraseFamily = ResolvePhraseFamilyTemplateCandidates(
                NormalizeValue(workItem.TargetStack),
                NormalizeValue(workItem.PhraseFamily));
            foreach (var templateId in fromPhraseFamily)
            {
                AddCandidate(
                    templateId,
                    "phrase_family_catalog",
                    $"Phrase-family mapping resolved chain `{templateId}`.");
            }
        }

        return candidates;
    }

    private static List<TaskboardExecutionLaneCandidate> MergeCandidates(
        IEnumerable<TaskboardExecutionLaneCandidate> left,
        IEnumerable<TaskboardExecutionLaneCandidate> right)
    {
        return left
            .Concat(right)
            .GroupBy(candidate => candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool TryResolveToolForOperation(string targetStack, string operationKind, out string toolId)
    {
        if (DotnetOperationToolMap.TryGetValue(operationKind, out var dotnetToolId)
            && (string.IsNullOrWhiteSpace(targetStack)
                || string.Equals(targetStack, "dotnet_desktop", StringComparison.OrdinalIgnoreCase)
                || string.Equals(targetStack, "unknown", StringComparison.OrdinalIgnoreCase)))
        {
            toolId = dotnetToolId ?? "";
            return true;
        }

        if (NativeOperationToolMap.TryGetValue(operationKind, out var nativeToolId)
            && (string.IsNullOrWhiteSpace(targetStack)
                || string.Equals(targetStack, "native_cpp_desktop", StringComparison.OrdinalIgnoreCase)
                || string.Equals(targetStack, "unknown", StringComparison.OrdinalIgnoreCase)))
        {
            toolId = nativeToolId ?? "";
            return true;
        }

        toolId = "";
        return false;
    }

    private static List<string> ResolvePhraseFamilyTemplateCandidates(string targetStack, string phraseFamily)
    {
        if (string.IsNullOrWhiteSpace(phraseFamily))
            return [];

        var candidates = new List<string>();
        if (string.IsNullOrWhiteSpace(targetStack)
            || string.Equals(targetStack, "dotnet_desktop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetStack, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            if (DotnetPhraseFamilyChainMap.TryGetValue(phraseFamily, out var dotnetTemplate))
                candidates.Add(dotnetTemplate);
        }

        if (string.IsNullOrWhiteSpace(targetStack)
            || string.Equals(targetStack, "native_cpp_desktop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetStack, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            if (NativePhraseFamilyChainMap.TryGetValue(phraseFamily, out var nativeTemplate))
                candidates.Add(nativeTemplate);
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildCandidateId(TaskboardExecutionLaneKind laneKind, string toolId, string chainTemplateId)
    {
        var target = laneKind == TaskboardExecutionLaneKind.ChainLane ? chainTemplateId : toolId;
        return $"{laneKind.ToString().ToLowerInvariant()}:{NormalizeValue(target)}";
    }

    private static List<TaskboardExecutionLaneEvidence> BuildEvidence(
        TaskboardRunWorkItem workItem,
        ToolRequest? request,
        string resolvedTargetPath,
        string selectedChainTemplate,
        TaskboardWorkFamilyResolution? workFamilyResolution)
    {
        var evidence = new List<TaskboardExecutionLaneEvidence>();
        var targetIdentity = string.IsNullOrWhiteSpace(resolvedTargetPath)
            ? new FileIdentityRecord()
            : new FileIdentityService().Identify(resolvedTargetPath);
        if (!string.IsNullOrWhiteSpace(workItem.OperationKind))
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "operation_kind",
                Value = workItem.OperationKind,
                Detail = "Resolved operation kind for lane selection."
            });
        }

        if (!string.IsNullOrWhiteSpace(workItem.TargetStack))
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "stack_family",
                Value = workItem.TargetStack,
                Detail = "Resolved stack family for lane selection."
            });
        }

        if (!string.IsNullOrWhiteSpace(workItem.PhraseFamily))
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "phrase_family",
                Value = workItem.PhraseFamily,
                Detail = "Phrase-family classification used for lane selection."
            });
        }

        if (!string.IsNullOrWhiteSpace(workFamilyResolution?.FamilyId))
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "work_family",
                Value = workFamilyResolution.FamilyId,
                Detail = $"Resolved work family from {NormalizeValue(workFamilyResolution.Source.ToString())}."
            });
        }

        if (!string.IsNullOrWhiteSpace(workItem.TemplateId))
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "template_id",
                Value = workItem.TemplateId,
                Detail = "Template selection used for lane selection."
            });
        }

        if (!string.IsNullOrWhiteSpace(resolvedTargetPath))
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "target_path",
                Value = resolvedTargetPath,
                Detail = "Workspace-relative target path bound for the selected lane."
            });
        }

        if (request is not null
            && request.TryGetArgument("canonical_operation_kind", out var canonicalOperationKind)
            && !string.IsNullOrWhiteSpace(canonicalOperationKind))
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "canonical_operation_kind",
                Value = canonicalOperationKind,
                Detail = request.TryGetArgument("canonicalization_trace", out var trace)
                    ? trace
                    : "Deterministic command canonicalization used before lane selection."
            });
        }

        if (request is not null
            && request.TryGetArgument("canonical_target_path", out var canonicalTargetPath)
            && !string.IsNullOrWhiteSpace(canonicalTargetPath))
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "canonical_target_path",
                Value = canonicalTargetPath,
                Detail = "Normalized target path produced by deterministic command canonicalization."
            });
        }

        if (!string.IsNullOrWhiteSpace(targetIdentity.FileType))
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "target_file_type",
                Value = targetIdentity.FileType,
                Detail = targetIdentity.IdentityTrace
            });
        }

        if (!string.IsNullOrWhiteSpace(targetIdentity.Role))
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "target_role",
                Value = targetIdentity.Role,
                Detail = targetIdentity.IdentityTrace
            });
        }

        if (request is not null)
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "selected_tool",
                Value = request.ToolName,
                Detail = string.IsNullOrWhiteSpace(selectedChainTemplate)
                    ? "Resolved direct tool lane."
                    : $"Resolved tool-backed chain lane via `{selectedChainTemplate}`."
            });
        }

        if (request is not null
            && request.TryGetArgument("test_target_resolution_kind", out var testTargetResolutionKind)
            && !string.IsNullOrWhiteSpace(testTargetResolutionKind))
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "test_target_resolution_kind",
                Value = testTargetResolutionKind,
                Detail = request.TryGetArgument("test_target_resolution_summary", out var summary) ? summary : "Recorded direct-test target resolution state."
            });
        }

        if (request is not null
            && request.TryGetArgument("test_target_discovered_alternatives", out var testTargetAlternatives)
            && !string.IsNullOrWhiteSpace(testTargetAlternatives))
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "test_target_alternatives",
                Value = testTargetAlternatives,
                Detail = "Discovered local alternatives preserved while resolving the direct test target."
            });
        }

        if (!string.IsNullOrWhiteSpace(selectedChainTemplate))
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "selected_chain",
                Value = selectedChainTemplate,
                Detail = "Resolved named chain lane."
            });
        }

        if (!string.IsNullOrWhiteSpace(workItem.ExpectedArtifact))
        {
            evidence.Add(new TaskboardExecutionLaneEvidence
            {
                Code = "expected_artifact",
                Value = workItem.ExpectedArtifact,
                Detail = FirstNonEmpty(workItem.ValidationHint, "Expected artifact recorded for deterministic validation.")
            });
        }

        return evidence;
    }

    private static TaskboardExecutionLaneBlockerCode ResolveCoverageBlockerCode(
        TaskboardWorkFamilyResolution workFamilyResolution,
        string targetStack,
        TaskboardExecutionLaneBlockerCode genericCode)
    {
        if (genericCode is not TaskboardExecutionLaneBlockerCode.NoLaneCandidates
            and not TaskboardExecutionLaneBlockerCode.MissingToolLaneForOperationKind
            and not TaskboardExecutionLaneBlockerCode.MissingChainLaneForPhraseFamily
            and not TaskboardExecutionLaneBlockerCode.MissingTemplateSelection)
        {
            return genericCode;
        }

        if (string.Equals(NormalizeValue(targetStack), "native_cpp_desktop", StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeValue(workFamilyResolution.FamilyId), "native_project_bootstrap", StringComparison.OrdinalIgnoreCase))
        {
            return TaskboardExecutionLaneBlockerCode.MissingNativeLaneMapping;
        }

        return NormalizeValue(workFamilyResolution.FamilyId) switch
        {
            "ui_shell_sections" => TaskboardExecutionLaneBlockerCode.MissingGroupedShellLane,
            "ui_wiring" => TaskboardExecutionLaneBlockerCode.MissingUiWiringLane,
            "app_state_wiring" => TaskboardExecutionLaneBlockerCode.MissingAppStateLane,
            "viewmodel_scaffold" => TaskboardExecutionLaneBlockerCode.MissingViewmodelScaffoldLane,
            "storage_bootstrap" => TaskboardExecutionLaneBlockerCode.MissingStorageBootstrapLane,
            "repository_scaffold" => TaskboardExecutionLaneBlockerCode.MissingRepositoryScaffoldLane,
            "check_runner" or "findings_pipeline" => TaskboardExecutionLaneBlockerCode.MissingCheckRunnerLane,
            "build_verify" => TaskboardExecutionLaneBlockerCode.MissingBuildVerifyLane,
            "build_repair" => TaskboardExecutionLaneBlockerCode.MissingBuildRepairLane,
            _ => genericCode
        };
    }

    private static string BuildCoverageBlockerMessage(
        TaskboardRunWorkItem workItem,
        TaskboardWorkFamilyResolution workFamilyResolution,
        string targetStack,
        TaskboardExecutionLaneBlockerCode blockerCode)
    {
        var operation = FirstNonEmpty(NormalizeValue(workItem.OperationKind), "unknown");
        var phraseFamily = FirstNonEmpty(NormalizeValue(workItem.PhraseFamily), "unknown");
        var stack = FirstNonEmpty(NormalizeValue(targetStack), "unknown");
        var workFamily = FirstNonEmpty(NormalizeValue(workFamilyResolution.FamilyId), "unknown");

        return blockerCode switch
        {
            TaskboardExecutionLaneBlockerCode.UnsupportedRuntimeCoverage => $"Taskboard auto-run blocked: C# runtime coverage is incomplete or intentionally non-runnable for work_family={workFamily} phrase_family={phraseFamily} operation_kind={operation} stack={stack}.",
            TaskboardExecutionLaneBlockerCode.MissingGroupedShellLane => $"Taskboard auto-run blocked: missing grouped-shell lane coverage for work_family={workFamily} phrase_family={phraseFamily} operation_kind={operation} stack={stack}.",
            TaskboardExecutionLaneBlockerCode.MissingUiWiringLane => $"Taskboard auto-run blocked: missing UI wiring lane coverage for work_family={workFamily} phrase_family={phraseFamily} operation_kind={operation} stack={stack}.",
            TaskboardExecutionLaneBlockerCode.MissingAppStateLane => $"Taskboard auto-run blocked: missing app-state lane coverage for work_family={workFamily} phrase_family={phraseFamily} operation_kind={operation} stack={stack}.",
            TaskboardExecutionLaneBlockerCode.MissingViewmodelScaffoldLane => $"Taskboard auto-run blocked: missing viewmodel scaffold lane coverage for work_family={workFamily} phrase_family={phraseFamily} operation_kind={operation} stack={stack}.",
            TaskboardExecutionLaneBlockerCode.MissingStorageBootstrapLane => $"Taskboard auto-run blocked: missing storage bootstrap lane coverage for work_family={workFamily} phrase_family={phraseFamily} operation_kind={operation} stack={stack}.",
            TaskboardExecutionLaneBlockerCode.MissingRepositoryScaffoldLane => $"Taskboard auto-run blocked: missing repository scaffold lane coverage for work_family={workFamily} phrase_family={phraseFamily} operation_kind={operation} stack={stack}.",
            TaskboardExecutionLaneBlockerCode.MissingCheckRunnerLane => $"Taskboard auto-run blocked: missing check-runner lane coverage for work_family={workFamily} phrase_family={phraseFamily} operation_kind={operation} stack={stack}.",
            TaskboardExecutionLaneBlockerCode.MissingBuildVerifyLane => $"Taskboard auto-run blocked: missing build-verify lane coverage for work_family={workFamily} phrase_family={phraseFamily} operation_kind={operation} stack={stack}.",
            TaskboardExecutionLaneBlockerCode.MissingBuildRepairLane => $"Taskboard auto-run blocked: missing build-repair lane coverage for work_family={workFamily} phrase_family={phraseFamily} operation_kind={operation} stack={stack}.",
            TaskboardExecutionLaneBlockerCode.MissingNativeLaneMapping => $"Taskboard auto-run blocked: missing native lane mapping for work_family={workFamily} phrase_family={phraseFamily} operation_kind={operation} stack={stack}.",
            TaskboardExecutionLaneBlockerCode.MissingTemplateSelection => $"Taskboard auto-run blocked: template `{FirstNonEmpty(NormalizeValue(workItem.TemplateId), "unknown")}` did not yield a deterministic lane for work_family={workFamily}.",
            TaskboardExecutionLaneBlockerCode.MissingChainLaneForPhraseFamily => $"Taskboard auto-run blocked: no chain lane exists yet for phrase_family={phraseFamily} work_family={workFamily} on stack={stack}.",
            TaskboardExecutionLaneBlockerCode.MissingToolLaneForOperationKind => $"Taskboard auto-run blocked: no tool lane exists yet for operation_kind={operation} work_family={workFamily} on stack={stack}.",
            _ => $"Taskboard auto-run blocked: no deterministic execution lane could be resolved for work_family={workFamily} phrase_family={phraseFamily} operation_kind={operation} stack={stack}."
        };
    }

    private static string ResolvePreferredChainTemplate(TaskboardRunWorkItem workItem, string toolName)
    {
        var operationKind = NormalizeValue(workItem.OperationKind);
        var targetStack = NormalizeValue(workItem.TargetStack);
        var phraseFamily = NormalizeValue(workItem.PhraseFamily);
        if ((string.Equals(operationKind, "view_shell_scaffold", StringComparison.OrdinalIgnoreCase)
                || string.Equals(operationKind, "write_shell_layout", StringComparison.OrdinalIgnoreCase))
            && string.Equals(targetStack, "dotnet_desktop", StringComparison.OrdinalIgnoreCase))
        {
            return "dotnet.desktop_shell_scaffold.v1";
        }

        if (string.Equals(targetStack, "dotnet_desktop", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(phraseFamily, "core_domain_models_contracts", StringComparison.OrdinalIgnoreCase)
                && toolName is "create_dotnet_project" or "add_project_to_solution" or "add_dotnet_project_reference" or "make_dir" or "write_file" or "dotnet_build" or "dotnet_test")
            {
                return "dotnet.domain_contracts_scaffold.v1";
            }

            if (string.Equals(phraseFamily, "repository_scaffold", StringComparison.OrdinalIgnoreCase)
                && toolName is "create_dotnet_project" or "add_project_to_solution" or "add_dotnet_project_reference" or "make_dir" or "write_file" or "dotnet_build" or "dotnet_test")
            {
                return "dotnet.repository_scaffold.v1";
            }
        }

        if (string.Equals(toolName, "dotnet_test", StringComparison.OrdinalIgnoreCase))
            return "workspace.test_verify.v1";

        if (string.Equals(toolName, "dotnet_build", StringComparison.OrdinalIgnoreCase))
            return "workspace.build_verify.v1";

        if (string.Equals(toolName, "make_dir", StringComparison.OrdinalIgnoreCase))
            return "";

        if (toolName is "show_artifacts" or "show_memory")
            return "artifact_inspection_single_step";

        if (toolName is "plan_repair" or "preview_patch_draft" or "apply_patch_draft" or "verify_patch_draft")
            return FirstNonEmpty(NormalizeValue(workItem.TemplateId), "repair_execution_chain");

        if (toolName is "cmake_configure" or "cmake_build" or "ctest_run" or "make_build" or "ninja_build" or "run_build_script")
        {
            return string.Equals(targetStack, "native_cpp_desktop", StringComparison.OrdinalIgnoreCase)
                ? "workspace.native_build_verify.v1"
                : "workspace.build_verify.v1";
        }

        if ((toolName is "add_project_to_solution" or "add_dotnet_project_reference")
            && string.Equals(targetStack, "dotnet_desktop", StringComparison.OrdinalIgnoreCase))
        {
            return "dotnet.project_attach.v1";
        }

        if (toolName is "register_navigation"
            && string.Equals(targetStack, "dotnet_desktop", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(NormalizeValue(workItem.PhraseFamily), "ui_shell_sections", StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeValue(workItem.OperationKind), "write_shell_registration", StringComparison.OrdinalIgnoreCase))
            {
                return "dotnet.shell_registration_wireup.v1";
            }

            return "dotnet.navigation_wireup.v1";
        }

        if (toolName is "create_dotnet_viewmodel"
            && string.Equals(targetStack, "dotnet_desktop", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(NormalizeValue(workItem.OperationKind), "write_shell_viewmodel", StringComparison.OrdinalIgnoreCase))
                return "dotnet.shell_registration_wireup.v1";

            if (string.Equals(NormalizeValue(workItem.OperationKind), "write_app_state", StringComparison.OrdinalIgnoreCase))
                return "dotnet.navigation_wireup.v1";
        }

        if ((toolName is "initialize_sqlite_storage_boundary" or "register_di_service")
            && string.Equals(targetStack, "dotnet_desktop", StringComparison.OrdinalIgnoreCase)
            && string.Equals(phraseFamily, "setup_storage_layer", StringComparison.OrdinalIgnoreCase))
        {
            return "dotnet.sqlite_storage_bootstrap.v1";
        }

        if (toolName is "write_file" && string.Equals(targetStack, "dotnet_desktop", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(NormalizeValue(workItem.OperationKind), "write_repository_contract", StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeValue(workItem.OperationKind), "write_repository_impl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(phraseFamily, "core_domain_models_contracts", StringComparison.OrdinalIgnoreCase)
                || string.Equals(phraseFamily, "repository_scaffold", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(phraseFamily, "core_domain_models_contracts", StringComparison.OrdinalIgnoreCase)
                    ? "dotnet.domain_contracts_scaffold.v1"
                    : "dotnet.repository_scaffold.v1";
            }

            if (string.Equals(NormalizeValue(workItem.OperationKind), "write_check_registry", StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeValue(workItem.OperationKind), "write_snapshot_builder", StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeValue(workItem.OperationKind), "write_findings_normalizer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(phraseFamily, "check_runner", StringComparison.OrdinalIgnoreCase)
                || string.Equals(phraseFamily, "findings_pipeline", StringComparison.OrdinalIgnoreCase))
            {
                return "dotnet.findings_pipeline_bootstrap.v1";
            }
        }

        if (toolName is "create_cmake_project" && string.Equals(targetStack, "native_cpp_desktop", StringComparison.OrdinalIgnoreCase))
            return "cmake.project_bootstrap.v1";

        return "";
    }

    private static bool IsToolSupportedForStack(string toolName, string targetStack)
    {
        if (string.IsNullOrWhiteSpace(targetStack) || string.Equals(targetStack, "unknown", StringComparison.OrdinalIgnoreCase))
            return true;

        if (toolName is "write_file" or "create_file" or "append_file" or "replace_in_file" or "make_dir" or "save_output" or "show_artifacts" or "show_memory" or "plan_repair" or "preview_patch_draft" or "apply_patch_draft" or "verify_patch_draft")
            return true;

        return targetStack switch
        {
            "dotnet_desktop" => toolName is "create_dotnet_solution"
                or "create_dotnet_project"
                or "add_project_to_solution"
                or "add_dotnet_project_reference"
                or "create_dotnet_page_view"
                or "create_dotnet_viewmodel"
                or "register_navigation"
                or "register_di_service"
                or "initialize_sqlite_storage_boundary"
                or "dotnet_build"
                or "dotnet_test"
                or "make_dir"
                or "write_file",
            "native_cpp_desktop" => toolName is "create_cmake_project"
                or "create_cpp_source_file"
                or "create_cpp_header_file"
                or "create_c_source_file"
                or "create_c_header_file"
                or "cmake_configure"
                or "cmake_build"
                or "ctest_run"
                or "make_build"
                or "ninja_build"
                or "run_build_script"
                or "plan_repair"
                or "preview_patch_draft"
                or "apply_patch_draft"
                or "verify_patch_draft"
                or "make_dir"
                or "write_file",
            _ => true
        };
    }

    private static bool RequiresWorkspaceTarget(string toolName)
    {
        return toolName is "write_file"
            or "create_file"
            or "append_file"
            or "replace_in_file"
            or "make_dir"
            or "save_output"
            or "dotnet_build"
            or "dotnet_test"
            or "add_project_to_solution"
            or "add_dotnet_project_reference"
            or "create_dotnet_page_view"
            or "create_dotnet_viewmodel"
            or "register_navigation"
            or "register_di_service"
            or "initialize_sqlite_storage_boundary"
            or "create_cmake_project"
            or "create_cpp_source_file"
            or "create_cpp_header_file"
            or "create_c_source_file"
            or "create_c_header_file"
            or "cmake_configure"
            or "cmake_build"
            or "ctest_run"
            or "make_build"
            or "ninja_build"
            or "run_build_script";
    }

    private static bool TryFindMissingRequiredArgument(string toolName, ToolRequest request, out string missingArgument)
    {
        if (RequiredArgumentsByToolName.TryGetValue(toolName, out var requiredArguments))
        {
            foreach (var requiredArgument in requiredArguments)
            {
                if (!request.TryGetArgument(requiredArgument, out _))
                {
                    missingArgument = requiredArgument;
                    return true;
                }
            }
        }

        missingArgument = "";
        return false;
    }

    private static TaskboardExecutionEligibilityKind DetermineEligibility(string toolName)
    {
        return NormalizeValue(toolName) switch
        {
            "create_file" or "write_file" or "append_file" or "replace_in_file" or "make_dir" or "save_output" or "show_artifacts" or "show_memory" or "create_dotnet_page_view" or "create_dotnet_viewmodel" or "register_navigation" or "register_di_service" or "initialize_sqlite_storage_boundary" or "create_cpp_source_file" or "create_cpp_header_file" or "create_c_source_file" or "create_c_header_file" => TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
            "dotnet_test" or "ctest_run" => TaskboardExecutionEligibilityKind.WorkspaceTestSafe,
            "create_dotnet_solution" or "create_dotnet_project" or "add_project_to_solution" or "add_dotnet_project_reference" or "dotnet_build" or "create_cmake_project" or "cmake_configure" or "cmake_build" or "make_build" or "ninja_build" or "run_build_script" => TaskboardExecutionEligibilityKind.WorkspaceBuildSafe,
            "plan_repair" or "preview_patch_draft" or "apply_patch_draft" or "verify_patch_draft" => TaskboardExecutionEligibilityKind.WorkspaceEditSafe,
            _ => TaskboardExecutionEligibilityKind.WorkspaceEditSafe
        };
    }

    private static string ExtractTargetPath(ToolRequest request)
    {
        if (request.TryGetArgument("path", out var path))
            return path;
        if (request.TryGetArgument("project", out var project))
            return project;
        if (request.TryGetArgument("solution_path", out var solutionPath))
            return solutionPath;
        if (request.TryGetArgument("project_path", out var projectPath))
            return projectPath;
        if (request.TryGetArgument("output_path", out var outputPath))
            return outputPath;
        if (request.TryGetArgument("build_dir", out var buildDir))
            return buildDir;
        if (request.TryGetArgument("directory", out var directory))
            return directory;
        if (request.TryGetArgument("source_dir", out var sourceDir))
            return sourceDir;

        return "";
    }

    private static bool IsBuilderLaneWorkItem(TaskboardRunWorkItem workItem)
    {
        return workItem.IsDecomposedItem
            || !string.IsNullOrWhiteSpace(workItem.OperationKind)
            || !string.IsNullOrWhiteSpace(workItem.TargetStack)
            || !string.IsNullOrWhiteSpace(workItem.PhraseFamily)
            || !string.IsNullOrWhiteSpace(workItem.TemplateId)
            || workItem.TemplateCandidateIds.Count > 0;
    }

    private static string NormalizeValue(string value)
    {
        return (value ?? "").Trim().ToLowerInvariant();
    }

    private static string NormalizePromptText(string value)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.StartsWith("- ", StringComparison.Ordinal))
            normalized = normalized[2..].TrimStart();
        else if (normalized.StartsWith("* ", StringComparison.Ordinal))
            normalized = normalized[2..].TrimStart();

        var digitPrefixLength = 0;
        while (digitPrefixLength < normalized.Length && char.IsDigit(normalized[digitPrefixLength]))
            digitPrefixLength++;

        if (digitPrefixLength > 0
            && digitPrefixLength < normalized.Length
            && (normalized[digitPrefixLength] == '.' || normalized[digitPrefixLength] == ')'))
        {
            normalized = normalized[(digitPrefixLength + 1)..].TrimStart();
        }

        return normalized;
    }

    private static bool ShouldDeferMissingTestTargetToPrerequisites(
        TaskboardRunWorkItem workItem,
        string requestedProject,
        WorkspaceBuildResolution resolution)
    {
        if (resolution.Success
            || !resolution.PrerequisiteRequired
            || string.IsNullOrWhiteSpace(requestedProject)
            || !requestedProject.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var operationKind = NormalizeValue(workItem.OperationKind);
        var phraseFamily = NormalizeValue(workItem.PhraseFamily);
        var templateId = NormalizeValue(workItem.TemplateId);
        return string.Equals(operationKind, "run_test_project", StringComparison.OrdinalIgnoreCase)
            || string.Equals(phraseFamily, "check_runner", StringComparison.OrdinalIgnoreCase)
            || string.Equals(templateId, "dotnet.check_runner_scaffold.v1", StringComparison.OrdinalIgnoreCase)
            || workItem.TemplateCandidateIds.Any(id => string.Equals(NormalizeValue(id), "dotnet.check_runner_scaffold.v1", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildWorkspaceResolutionReason(WorkspaceBuildResolution resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution.Message))
            return "Workspace target resolution did not yield a runnable test project.";

        return resolution.Message
            .Replace(Environment.NewLine, " ")
            .Trim();
    }

    private static string NormalizeValueForArgument(string value)
    {
        return (value ?? "").Trim();
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
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
