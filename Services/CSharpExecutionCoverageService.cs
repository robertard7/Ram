using RAM.Models;

namespace RAM.Services;

public sealed class CSharpExecutionCoverageService
{
    private readonly ToolRegistryService _toolRegistryService = new();
    private readonly ToolChainTemplateRegistry _toolChainTemplateRegistry = new();
    private CSharpExecutionCoverageAudit? _cachedAudit;

    public CSharpExecutionCoverageAudit BuildAudit()
    {
        return _cachedAudit ??= BuildAuditCore();
    }

    public CSharpExecutionCoverageEvaluation EvaluateAutoRunToolCandidate(TaskboardRunWorkItem workItem, string toolId)
    {
        if (!IsDotnetStack(workItem.TargetStack) || string.IsNullOrWhiteSpace(toolId))
            return new CSharpExecutionCoverageEvaluation();

        var normalizedToolId = Normalize(toolId);
        if (normalizedToolId == "show_artifacts" && !IsExplicitArtifactInspection(workItem))
        {
            return new CSharpExecutionCoverageEvaluation
            {
                Relevant = true,
                IsRunnable = false,
                CoverageId = "dotnet.generic_maintenance_context_artifact_chain",
                Status = CSharpExecutionCoverageStatus.VocabularyOnly,
                ReasonCode = "generic_artifact_inspection_blocked",
                Summary = "Generic C# maintenance headings are no longer auto-runnable artifact inspection. Only explicit inspect_context_artifacts work may execute `show_artifacts`."
            };
        }

        var record = BuildAudit().Records.FirstOrDefault(item =>
            string.Equals(item.ToolId, normalizedToolId, StringComparison.OrdinalIgnoreCase)
            && item.AutoRunReachable);
        if (record is null)
        {
            return new CSharpExecutionCoverageEvaluation
            {
                Relevant = true,
                IsRunnable = false,
                CoverageId = $"dotnet.tool.{normalizedToolId}",
                Status = CSharpExecutionCoverageStatus.Unsupported,
                ReasonCode = "unsupported_csharp_tool_lane",
                Summary = $"C# auto-run does not declare `{normalizedToolId}` as part of the minimum executable taskboard surface."
            };
        }

        return new CSharpExecutionCoverageEvaluation
        {
            Relevant = true,
            IsRunnable = record.IsRunnable,
            CoverageId = record.CoverageId,
            Status = record.Status,
            ReasonCode = record.IsRunnable ? "fully_wired" : "unsupported_csharp_tool_lane",
            Summary = record.Summary
        };
    }

    public CSharpExecutionCoverageEvaluation EvaluateAutoRunChainCandidate(TaskboardRunWorkItem workItem, string templateId)
    {
        if (!IsDotnetStack(workItem.TargetStack) || string.IsNullOrWhiteSpace(templateId))
            return new CSharpExecutionCoverageEvaluation();

        var normalizedTemplateId = Normalize(templateId);
        if (normalizedTemplateId == "artifact_inspection_single_step" && !IsExplicitArtifactInspection(workItem))
        {
            return new CSharpExecutionCoverageEvaluation
            {
                Relevant = true,
                IsRunnable = false,
                CoverageId = "dotnet.generic_maintenance_context_artifact_chain",
                Status = CSharpExecutionCoverageStatus.VocabularyOnly,
                ReasonCode = "generic_artifact_inspection_blocked",
                Summary = "Generic C# maintenance headings are no longer allowed to fall through to `artifact_inspection_single_step`. Fold them into the parent executable flow or resolve an explicit inspect_context_artifacts step."
            };
        }

        var record = BuildAudit().Records.FirstOrDefault(item =>
            string.Equals(item.ChainTemplateId, normalizedTemplateId, StringComparison.OrdinalIgnoreCase)
            && item.AutoRunReachable);
        if (record is null)
        {
            return new CSharpExecutionCoverageEvaluation
            {
                Relevant = true,
                IsRunnable = false,
                CoverageId = $"dotnet.chain.{normalizedTemplateId}",
                Status = CSharpExecutionCoverageStatus.Unsupported,
                ReasonCode = "unsupported_csharp_chain_lane",
                Summary = $"C# auto-run does not declare chain `{normalizedTemplateId}` as part of the minimum executable taskboard surface."
            };
        }

        return new CSharpExecutionCoverageEvaluation
        {
            Relevant = true,
            IsRunnable = record.IsRunnable,
            CoverageId = record.CoverageId,
            Status = record.Status,
            ReasonCode = record.IsRunnable ? "fully_wired" : "unsupported_csharp_chain_lane",
            Summary = record.Summary
        };
    }

    public string BuildRuntimeCoverageBanner(TaskboardPlanRunStateRecord? runState)
    {
        if (!LooksLikeDotnetRuntime(runState))
            return "";

        var audit = BuildAudit();
        var currentTool = FirstNonEmpty(
            runState?.LastObservedToolName,
            runState?.LastPlannedToolName,
            runState?.LastExecutionGoalResolution?.Goal.SelectedToolId);
        var currentChain = FirstNonEmpty(
            runState?.LastObservedChainTemplateId,
            runState?.LastPlannedChainTemplateId,
            runState?.LastExecutionGoalResolution?.Goal.SelectedChainTemplateId);
        var currentSummary = BuildCurrentCoverageSummary(runState, currentTool, currentChain);
        return $"C# runtime: minimum_complete_set={(audit.MinimumCompleteSetReady ? "ready" : "incomplete")} fully_wired={audit.FullyWiredCount} multi_step_ready={audit.FullyWiredMultiStepCount} one_step_by_design={audit.OneStepByDesignCount} partial={audit.PartiallyWiredCount} vocabulary_only={audit.VocabularyOnlyCount} unsupported={audit.UnsupportedCount} current={currentSummary}";
    }

    public string BuildRuntimeChainDepthBanner(TaskboardPlanRunStateRecord? runState)
    {
        if (!LooksLikeDotnetRuntime(runState))
            return "";

        if (runState is null)
            return "Chain depth: (none)";

        var currentChain = FirstNonEmpty(
            runState.LastObservedChainTemplateId,
            runState.LastPlannedChainTemplateId,
            runState.LastExecutionGoalResolution?.Goal.SelectedChainTemplateId);
        if (string.IsNullOrWhiteSpace(currentChain))
            return "Chain depth: (none)";

        var record = BuildAudit().Records.FirstOrDefault(item =>
            string.Equals(item.ChainTemplateId, currentChain, StringComparison.OrdinalIgnoreCase));
        var calls = runState.ExecutedToolCalls
            .Where(call => string.Equals(call.ChainTemplateId, currentChain, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var intendedSteps = record?.IntendedStepToolIds ?? [];
        var plannedSteps = intendedSteps.Count == 0 ? "(none)" : string.Join(">", intendedSteps);
        var executedSteps = calls
            .Where(call => string.Equals(call.Stage, "completed", StringComparison.OrdinalIgnoreCase))
            .Select(call => call.ToolName)
            .Where(tool => !string.IsNullOrWhiteSpace(tool))
            .ToList();
        var blockedSteps = calls
            .Where(call => string.Equals(call.Stage, "blocked", StringComparison.OrdinalIgnoreCase))
            .Select(call => call.ToolName)
            .Where(tool => !string.IsNullOrWhiteSpace(tool))
            .ToList();
        var failedSteps = calls
            .Where(call => string.Equals(call.Stage, "failed", StringComparison.OrdinalIgnoreCase))
            .Select(call => call.ToolName)
            .Where(tool => !string.IsNullOrWhiteSpace(tool))
            .ToList();
        var collapse = BuildCollapseReason(record, calls);
        var dispatchedCount = calls.Count(call => string.Equals(call.Stage, "dispatched", StringComparison.OrdinalIgnoreCase));
        var executedSummary = executedSteps.Count == 0 ? "(none)" : string.Join(">", executedSteps);
        var blockedSummary = blockedSteps.Count == 0 ? "(none)" : string.Join(">", blockedSteps);
        var failedSummary = failedSteps.Count == 0 ? "(none)" : string.Join(">", failedSteps);
        return $"Chain depth: template={currentChain} design={DescribeExecutionShape(record?.ExecutionShape ?? CSharpExecutionShapeKind.Unknown)} intended_steps={plannedSteps} dispatched={dispatchedCount} completed={executedSteps.Count} blocked={blockedSteps.Count} failed={failedSteps.Count} executed={executedSummary} blocked_steps={blockedSummary} failed_steps={failedSummary} collapse={collapse}";
    }

    private CSharpExecutionCoverageAudit BuildAuditCore()
    {
        var toolRecords = new List<CSharpExecutionCoverageRecord>
        {
            BuildToolRecord(
                "dotnet.create_solution",
                "create_dotnet_solution",
                "create_solution",
                "solution_scaffold",
                "solution_scaffold",
                true,
                true,
                ["scaffold", "build"]),
            BuildToolRecord(
                "dotnet.create_project",
                "create_dotnet_project",
                "create_project",
                "project_scaffold",
                "project_scaffold",
                true,
                true,
                ["scaffold", "build"]),
            BuildToolRecord(
                "dotnet.attach_project_to_solution",
                "add_project_to_solution",
                "add_project_to_solution",
                "solution_scaffold",
                "solution_scaffold",
                true,
                true,
                ["scaffold", "build"]),
            BuildToolRecord(
                "dotnet.add_project_reference",
                "add_dotnet_project_reference",
                "add_project_reference",
                "repository_scaffold",
                "solution_scaffold",
                true,
                true,
                ["scaffold", "repair"]),
            BuildToolRecord(
                "dotnet.create_page_view",
                "create_dotnet_page_view",
                "write_page",
                "ui_shell_sections",
                "ui_shell_sections",
                true,
                true,
                ["scaffold", "build"]),
            BuildToolRecord(
                "dotnet.create_viewmodel",
                "create_dotnet_viewmodel",
                "write_app_state",
                "add_navigation_app_state",
                "app_state_wiring",
                true,
                true,
                ["scaffold", "build"]),
            BuildToolRecord(
                "dotnet.register_navigation",
                "register_navigation",
                "write_navigation_item",
                "add_navigation_app_state",
                "app_state_wiring",
                true,
                true,
                ["scaffold", "build"]),
            BuildToolRecord(
                "dotnet.initialize_sqlite_storage_boundary",
                "initialize_sqlite_storage_boundary",
                "write_storage_contract",
                "setup_storage_layer",
                "storage_bootstrap",
                true,
                true,
                ["scaffold", "build", "maintenance"]),
            BuildToolRecord(
                "dotnet.register_di_service",
                "register_di_service",
                "write_storage_impl",
                "setup_storage_layer",
                "storage_bootstrap",
                true,
                true,
                ["scaffold", "build", "maintenance"]),
            BuildToolRecord(
                "dotnet.create_directory",
                "make_dir",
                "make_models_dir",
                "repository_scaffold",
                "solution_scaffold",
                false,
                true,
                ["scaffold", "repair"]),
            BuildToolRecord(
                "dotnet.write_file",
                "write_file",
                "write_repository_contract",
                "repository_scaffold",
                "repository_scaffold",
                true,
                true,
                ["scaffold", "repair"]),
            BuildToolRecord(
                "dotnet.build_solution",
                "dotnet_build",
                "build_solution",
                "build_verify",
                "build_verify",
                true,
                true,
                ["build", "verify", "maintenance"]),
            BuildToolRecord(
                "dotnet.test_project",
                "dotnet_test",
                "run_test_project",
                "build_verify",
                "build_verify",
                true,
                true,
                ["build", "verify", "maintenance"]),
            BuildToolRecord(
                "dotnet.inspect_failure_context",
                "open_failure_context",
                "inspect_failure_context",
                "build_failure_repair",
                "build_repair",
                false,
                false,
                ["repair", "inspection"]),
            BuildToolRecord(
                "dotnet.plan_repair",
                "plan_repair",
                "inspect_solution_wiring",
                "solution_graph_repair",
                "build_repair",
                true,
                true,
                ["repair", "maintenance"]),
            BuildToolRecord(
                "dotnet.preview_patch",
                "preview_patch_draft",
                "",
                "build_failure_repair",
                "build_repair",
                true,
                true,
                ["repair", "maintenance"]),
            BuildToolRecord(
                "dotnet.apply_patch",
                "apply_patch_draft",
                "",
                "build_failure_repair",
                "build_repair",
                true,
                true,
                ["repair", "maintenance"]),
            BuildToolRecord(
                "dotnet.verify_patch",
                "verify_patch_draft",
                "",
                "build_failure_repair",
                "build_repair",
                true,
                true,
                ["repair", "verify", "maintenance"]),
            BuildToolRecord(
                "dotnet.explicit_artifact_inspection_tool",
                "show_artifacts",
                "inspect_context_artifacts",
                "maintenance_context",
                "maintenance_context",
                false,
                true,
                ["maintenance", "inspection"]),
            BuildToolRecord(
                "dotnet.inspect_solution_wiring",
                "inspect_solution_wiring",
                "inspect_solution_wiring",
                "solution_graph_repair",
                "build_repair",
                false,
                false,
                ["repair", "inspection"])
        };

        var records = new List<CSharpExecutionCoverageRecord>(toolRecords)
        {
            BuildChainRecord(
                "dotnet.solution_scaffold_chain",
                "dotnet.solution_scaffold.v1",
                "solution_scaffold",
                "solution_scaffold",
                false,
                false,
                ["scaffold", "build"],
                ["create_dotnet_solution", "create_dotnet_project", "add_project_to_solution"],
                runtimeControllerWired: false,
                toolRecords: toolRecords),
            BuildChainRecord(
                "dotnet.project_attach_chain",
                "dotnet.project_attach.v1",
                "repository_scaffold",
                "solution_scaffold",
                true,
                true,
                ["scaffold", "repair"],
                ["create_dotnet_project", "add_project_to_solution", "add_dotnet_project_reference"],
                runtimeControllerWired: true,
                toolRecords: toolRecords,
                requireOrderedSequence: true),
            BuildChainRecord(
                "dotnet.shell_page_set_chain",
                "dotnet.shell_page_set_scaffold.v1",
                "ui_shell_sections",
                "ui_shell_sections",
                false,
                false,
                ["scaffold"],
                ["create_dotnet_page_view", "register_navigation", "create_dotnet_viewmodel", "dotnet_build"],
                runtimeControllerWired: false,
                toolRecords: toolRecords,
                requireOrderedSequence: true),
            BuildChainRecord(
                "dotnet.desktop_shell_chain",
                "dotnet.desktop_shell_scaffold.v1",
                "build_first_ui_shell",
                "ui_shell_sections",
                false,
                false,
                ["scaffold"],
                ["create_dotnet_page_view", "create_dotnet_viewmodel"],
                runtimeControllerWired: false,
                toolRecords: toolRecords),
            BuildChainRecord(
                "dotnet.page_viewmodel_chain",
                "dotnet.page_and_viewmodel_scaffold.v1",
                "add_settings_page",
                "ui_shell_sections",
                false,
                false,
                ["scaffold"],
                ["create_dotnet_page_view", "create_dotnet_viewmodel"],
                runtimeControllerWired: false,
                toolRecords: toolRecords),
            BuildChainRecord(
                "dotnet.navigation_chain",
                "dotnet.navigation_wireup.v1",
                "add_navigation_app_state",
                "app_state_wiring",
                true,
                true,
                ["scaffold"],
                ["make_dir", "register_navigation", "create_dotnet_viewmodel"],
                runtimeControllerWired: true,
                toolRecords: toolRecords,
                requireOrderedSequence: true),
            BuildChainRecord(
                "dotnet.shell_registration_chain",
                "dotnet.shell_registration_wireup.v1",
                "ui_shell_sections",
                "app_state_wiring",
                true,
                true,
                ["scaffold"],
                ["make_dir", "register_navigation", "create_dotnet_viewmodel"],
                runtimeControllerWired: true,
                toolRecords: toolRecords,
                requireOrderedSequence: true),
            BuildChainRecord(
                "dotnet.sqlite_storage_chain",
                "dotnet.sqlite_storage_bootstrap.v1",
                "setup_storage_layer",
                "storage_bootstrap",
                true,
                true,
                ["scaffold", "build", "maintenance"],
                ["make_dir", "initialize_sqlite_storage_boundary", "register_di_service"],
                runtimeControllerWired: true,
                toolRecords: toolRecords,
                requireOrderedSequence: true),
            BuildChainRecord(
                "dotnet.domain_contracts_chain",
                "dotnet.domain_contracts_scaffold.v1",
                "core_domain_models_contracts",
                "repository_scaffold",
                true,
                true,
                ["scaffold"],
                ["create_dotnet_project", "add_project_to_solution", "add_dotnet_project_reference", "make_dir", "make_dir", "write_file", "write_file", "dotnet_build", "dotnet_test"],
                runtimeControllerWired: true,
                toolRecords: toolRecords,
                requireOrderedSequence: true),
            BuildChainRecord(
                "dotnet.repository_chain",
                "dotnet.repository_scaffold.v1",
                "repository_scaffold",
                "repository_scaffold",
                true,
                true,
                ["scaffold", "build", "maintenance"],
                ["make_dir", "write_file", "write_file", "dotnet_build", "dotnet_test"],
                runtimeControllerWired: true,
                toolRecords: toolRecords,
                requireOrderedSequence: true),
            BuildChainRecord(
                "dotnet.check_runner_chain",
                "dotnet.check_runner_scaffold.v1",
                "check_runner",
                "check_runner",
                true,
                true,
                ["scaffold", "verify", "maintenance"],
                ["create_dotnet_project", "add_project_to_solution", "dotnet_test"],
                runtimeControllerWired: true,
                toolRecords: toolRecords,
                requireOrderedSequence: true),
            BuildChainRecord(
                "dotnet.findings_pipeline_chain",
                "dotnet.findings_pipeline_bootstrap.v1",
                "findings_pipeline",
                "check_runner",
                false,
                false,
                ["scaffold", "verify"],
                ["create_dotnet_project", "add_project_to_solution", "write_file", "dotnet_test"],
                runtimeControllerWired: false,
                toolRecords: toolRecords,
                requireOrderedSequence: true),
            BuildChainRecord(
                "dotnet.build_verify_chain",
                "workspace.build_verify.v1",
                "build_verify",
                "build_verify",
                true,
                true,
                ["build", "verify", "maintenance"],
                ["dotnet_build", "dotnet_test"],
                runtimeControllerWired: true,
                toolRecords: toolRecords,
                requireOrderedSequence: true),
            BuildChainRecord(
                "dotnet.test_verify_chain",
                "workspace.test_verify.v1",
                "build_verify",
                "build_verify",
                true,
                true,
                ["verify", "maintenance"],
                ["dotnet_test"],
                runtimeControllerWired: true,
                toolRecords: toolRecords),
            BuildChainRecord(
                "dotnet.repair_preview_chain",
                "repair_preview_chain",
                "build_failure_repair",
                "build_repair",
                false,
                true,
                ["repair", "maintenance"],
                ["plan_repair", "preview_patch_draft"],
                runtimeControllerWired: true,
                toolRecords: toolRecords,
                requireOrderedSequence: true),
            BuildChainRecord(
                "dotnet.repair_execution_chain",
                "repair_execution_chain",
                "solution_graph_repair",
                "build_repair",
                true,
                true,
                ["repair", "verify", "maintenance"],
                ["plan_repair", "preview_patch_draft", "apply_patch_draft", "verify_patch_draft"],
                runtimeControllerWired: true,
                toolRecords: toolRecords,
                requireOrderedSequence: true),
            BuildChainRecord(
                "dotnet.explicit_artifact_inspection_chain",
                "artifact_inspection_single_step",
                "maintenance_context",
                "maintenance_context",
                false,
                true,
                ["maintenance", "inspection"],
                ["show_artifacts"],
                runtimeControllerWired: true,
                toolRecords: toolRecords),
            new CSharpExecutionCoverageRecord
            {
                CoverageId = "dotnet.generic_maintenance_context_artifact_chain",
                CoverageKind = "chain",
                ChainTemplateId = "artifact_inspection_single_step",
                PhraseFamily = "maintenance_context",
                LaneFamily = "maintenance_context",
                ReachablePhases = ["maintenance"],
                AutoRunReachable = false,
                MinimumCompleteSetMember = false,
                Status = CSharpExecutionCoverageStatus.VocabularyOnly,
                ExecutionShape = CSharpExecutionShapeKind.OneStepByDesign,
                IsRunnable = false,
                RuntimeControllerWired = true,
                IntendedStepCount = 1,
                IntendedStepToolIds = ["show_artifacts"],
                Summary = "Generic maintenance_context headings are intentionally non-runnable in the unified C# runtime. Only explicit inspect_context_artifacts work may use artifact inspection as the selected chain."
            },
            new CSharpExecutionCoverageRecord
            {
                CoverageId = "dotnet.file_touch_truth",
                CoverageKind = "truth",
                LaneFamily = "execution_truth",
                ReachablePhases = ["scaffold", "build", "repair", "maintenance"],
                AutoRunReachable = true,
                MinimumCompleteSetMember = true,
                Status = CSharpExecutionCoverageStatus.FullyWired,
                ExecutionShape = CSharpExecutionShapeKind.TruthOnly,
                IsRunnable = true,
                Summary = "File-touch and mutation proof are persisted through run state, projection, summaries, and normalized run data."
            },
            new CSharpExecutionCoverageRecord
            {
                CoverageId = "dotnet.contradiction_prevention_truth",
                CoverageKind = "truth",
                LaneFamily = "execution_truth",
                ReachablePhases = ["scaffold", "build", "repair", "maintenance"],
                AutoRunReachable = true,
                MinimumCompleteSetMember = true,
                Status = CSharpExecutionCoverageStatus.FullyWired,
                ExecutionShape = CSharpExecutionShapeKind.TruthOnly,
                IsRunnable = true,
                Summary = "Same-run contradiction prevention blocks self-reference reintroduction and contradictory repair drift before execution."
            },
            new CSharpExecutionCoverageRecord
            {
                CoverageId = "dotnet.completion_proof_truth",
                CoverageKind = "truth",
                LaneFamily = "execution_truth",
                ReachablePhases = ["scaffold", "build", "repair", "maintenance"],
                AutoRunReachable = true,
                MinimumCompleteSetMember = true,
                Status = CSharpExecutionCoverageStatus.FullyWired,
                ExecutionShape = CSharpExecutionShapeKind.TruthOnly,
                IsRunnable = true,
                Summary = "Completion proof prefers actionable repair/build verification over weaker inspection-only success and exposes the chosen proof source in summary/debug output."
            }
        };

        var audit = new CSharpExecutionCoverageAudit
        {
            AuditId = Guid.NewGuid().ToString("N"),
            Records = records,
            MinimumCompleteSetReady = records
                .Where(item => item.MinimumCompleteSetMember)
                .All(item => item.Status == CSharpExecutionCoverageStatus.FullyWired),
            FullyWiredCount = records.Count(item => item.Status == CSharpExecutionCoverageStatus.FullyWired),
            FullyWiredMultiStepCount = records.Count(item =>
                item.Status == CSharpExecutionCoverageStatus.FullyWired
                && item.ExecutionShape == CSharpExecutionShapeKind.MultiStep),
            OneStepByDesignCount = records.Count(item =>
                item.Status == CSharpExecutionCoverageStatus.FullyWired
                && item.ExecutionShape == CSharpExecutionShapeKind.OneStepByDesign),
            PartiallyWiredCount = records.Count(item => item.Status == CSharpExecutionCoverageStatus.PartiallyWired),
            VocabularyOnlyCount = records.Count(item => item.Status == CSharpExecutionCoverageStatus.VocabularyOnly),
            UnsupportedCount = records.Count(item => item.Status == CSharpExecutionCoverageStatus.Unsupported)
        };
        audit.Summary = $"C# minimum runtime: {(audit.MinimumCompleteSetReady ? "ready" : "incomplete")} minimum_entries={records.Count(item => item.MinimumCompleteSetMember)} fully_wired={audit.FullyWiredCount} multi_step_ready={audit.FullyWiredMultiStepCount} one_step_by_design={audit.OneStepByDesignCount} partial={audit.PartiallyWiredCount} vocabulary_only={audit.VocabularyOnlyCount} unsupported={audit.UnsupportedCount}.";
        return audit;
    }

    private CSharpExecutionCoverageRecord BuildToolRecord(
        string coverageId,
        string toolId,
        string operationKind,
        string phraseFamily,
        string laneFamily,
        bool minimumCompleteSetMember,
        bool autoRunReachable,
        IReadOnlyList<string> reachablePhases)
    {
        var missingPieces = new List<string>();
        if (!_toolRegistryService.HasTool(toolId))
            missingPieces.Add($"tool:{toolId}");

        var status = missingPieces.Count == 0
            ? autoRunReachable
                ? CSharpExecutionCoverageStatus.FullyWired
                : CSharpExecutionCoverageStatus.PartiallyWired
            : CSharpExecutionCoverageStatus.Unsupported;
        var summary = status switch
        {
            CSharpExecutionCoverageStatus.FullyWired => $"Tool `{toolId}` is wired end-to-end for the unified C# runtime.",
            CSharpExecutionCoverageStatus.PartiallyWired => $"Tool `{toolId}` is tool-backed but direct/manual only; it is not part of the minimum auto-run C# lane set.",
            _ => $"Tool `{toolId}` is missing required C# execution wiring: {string.Join(", ", missingPieces)}."
        };

        return new CSharpExecutionCoverageRecord
        {
            CoverageId = coverageId,
            CoverageKind = "tool",
            ToolId = toolId,
            OperationKind = operationKind,
            PhraseFamily = phraseFamily,
            LaneFamily = laneFamily,
            ReachablePhases = [.. reachablePhases],
            AutoRunReachable = autoRunReachable,
            MinimumCompleteSetMember = minimumCompleteSetMember,
            Status = status,
            ExecutionShape = CSharpExecutionShapeKind.OneStepByDesign,
            IsRunnable = status == CSharpExecutionCoverageStatus.FullyWired,
            RuntimeControllerWired = true,
            IntendedStepCount = 1,
            IntendedStepToolIds = string.IsNullOrWhiteSpace(toolId) ? [] : [toolId],
            MissingPieces = missingPieces,
            Summary = summary
        };
    }

    private CSharpExecutionCoverageRecord BuildChainRecord(
        string coverageId,
        string templateId,
        string phraseFamily,
        string laneFamily,
        bool minimumCompleteSetMember,
        bool autoRunReachable,
        IReadOnlyList<string> reachablePhases,
        IReadOnlyList<string> expectedTools,
        bool runtimeControllerWired,
        IReadOnlyList<CSharpExecutionCoverageRecord> toolRecords,
        bool requireOrderedSequence = false)
    {
        var missingPieces = new List<string>();
        var executionShape = expectedTools.Count <= 1
            ? CSharpExecutionShapeKind.OneStepByDesign
            : CSharpExecutionShapeKind.MultiStep;

        if (!_toolChainTemplateRegistry.HasTemplate(templateId))
        {
            missingPieces.Add($"template:{templateId}");
        }
        else
        {
            var template = _toolChainTemplateRegistry.ResolveTemplateForName(templateId);
            foreach (var toolId in expectedTools)
            {
                var toolCoverage = toolRecords.FirstOrDefault(record =>
                    string.Equals(record.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
                if (toolCoverage is null)
                {
                    missingPieces.Add($"tool_coverage:{toolId}");
                }
                else if (!toolCoverage.IsRunnable)
                {
                    missingPieces.Add($"step_runtime:{toolId}:{toolCoverage.Status.ToString().ToLowerInvariant()}");
                }

                if (!_toolRegistryService.HasTool(toolId))
                    missingPieces.Add($"tool:{toolId}");
                else if (!template.StartingTools.Contains(toolId)
                         && !template.StepGraph.StepDefinitions.Any(step => string.Equals(step.ToolId, toolId, StringComparison.OrdinalIgnoreCase)))
                    missingPieces.Add($"step:{toolId}");
            }

            if (expectedTools.Count > 0
                && requireOrderedSequence
                && !_toolChainTemplateRegistry.IsStartingToolAllowed(template, expectedTools[0]))
                missingPieces.Add($"start:{expectedTools[0]}");

            for (var index = 1; requireOrderedSequence && index < expectedTools.Count; index++)
            {
                if (!_toolChainTemplateRegistry.IsTransitionAllowed(template, expectedTools[index - 1], expectedTools[index]))
                    missingPieces.Add($"transition:{expectedTools[index - 1]}->{expectedTools[index]}");
            }
        }

        if (!runtimeControllerWired)
            missingPieces.Add("runtime_chain_followup_wiring");

        var templateMissing = missingPieces.Any(piece => piece.StartsWith("template:", StringComparison.OrdinalIgnoreCase));
        var status = missingPieces.Count == 0
            ? autoRunReachable
                ? CSharpExecutionCoverageStatus.FullyWired
                : CSharpExecutionCoverageStatus.PartiallyWired
            : templateMissing
                ? CSharpExecutionCoverageStatus.Unsupported
                : minimumCompleteSetMember
                    ? CSharpExecutionCoverageStatus.PartiallyWired
                    : CSharpExecutionCoverageStatus.Unsupported;
        var summary = status switch
        {
            CSharpExecutionCoverageStatus.FullyWired => $"Chain `{templateId}` is wired end-to-end for the unified C# runtime as a {DescribeExecutionShape(executionShape)} chain.",
            CSharpExecutionCoverageStatus.PartiallyWired => $"Chain `{templateId}` exists but is not fully runnable in the unified C# runtime yet: {string.Join(", ", missingPieces)}.",
            _ => $"Chain `{templateId}` is missing required C# execution wiring: {string.Join(", ", missingPieces)}."
        };

        return new CSharpExecutionCoverageRecord
        {
            CoverageId = coverageId,
            CoverageKind = "chain",
            ChainTemplateId = templateId,
            PhraseFamily = phraseFamily,
            LaneFamily = laneFamily,
            ReachablePhases = [.. reachablePhases],
            AutoRunReachable = autoRunReachable,
            MinimumCompleteSetMember = minimumCompleteSetMember,
            Status = status,
            ExecutionShape = executionShape,
            IsRunnable = status == CSharpExecutionCoverageStatus.FullyWired,
            RuntimeControllerWired = runtimeControllerWired,
            IntendedStepCount = expectedTools.Count,
            IntendedStepToolIds = [.. expectedTools],
            MissingPieces = missingPieces,
            Summary = summary
        };
    }

    private string BuildCurrentCoverageSummary(TaskboardPlanRunStateRecord? runState, string currentTool, string currentChain)
    {
        if (runState is null)
            return "(none)";

        if (!string.IsNullOrWhiteSpace(runState.LastExecutionGoalResolution?.LaneResolution?.Blocker?.Message)
            && runState.LastExecutionGoalResolution.LaneResolution.Blocker.Code == TaskboardExecutionLaneBlockerCode.UnsupportedRuntimeCoverage)
        {
            return $"blocked:{runState.LastExecutionGoalResolution.LaneResolution.Blocker.Code.ToString().ToLowerInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(currentChain))
        {
            var record = BuildAudit().Records.FirstOrDefault(item =>
                string.Equals(item.ChainTemplateId, currentChain, StringComparison.OrdinalIgnoreCase)
                && item.AutoRunReachable);
            if (record is not null)
                return $"{currentChain}:{record.Status.ToString().ToLowerInvariant()}:{DescribeExecutionShape(record.ExecutionShape)}";
        }

        if (!string.IsNullOrWhiteSpace(currentTool))
        {
            var record = BuildAudit().Records.FirstOrDefault(item =>
                string.Equals(item.ToolId, currentTool, StringComparison.OrdinalIgnoreCase)
                && (item.AutoRunReachable || item.Status == CSharpExecutionCoverageStatus.PartiallyWired));
            if (record is not null)
                return $"{currentTool}:{record.Status.ToString().ToLowerInvariant()}:{DescribeExecutionShape(record.ExecutionShape)}";
        }

        return "(none)";
    }

    private static string BuildCollapseReason(
        CSharpExecutionCoverageRecord? record,
        IReadOnlyList<TaskboardExecutedToolCallRecord> calls)
    {
        if (record is null)
            return "unknown_template";

        if (record.ExecutionShape == CSharpExecutionShapeKind.OneStepByDesign)
            return "one_step_by_design";

        if (record.ExecutionShape != CSharpExecutionShapeKind.MultiStep)
            return "n/a";

        var expected = Math.Max(record.IntendedStepCount, record.IntendedStepToolIds.Count);
        var completed = calls.Count(call => string.Equals(call.Stage, "completed", StringComparison.OrdinalIgnoreCase));
        if (expected == 0)
            return "n/a";
        if (completed >= expected)
            return "none";

        var blocked = calls.LastOrDefault(call => string.Equals(call.Stage, "blocked", StringComparison.OrdinalIgnoreCase));
        if (blocked is not null)
        {
            var blockedSummary = FirstNonEmpty(blocked.Summary);
            if (blockedSummary.Contains("Controlled chain blocked:", StringComparison.OrdinalIgnoreCase))
                return $"wiring_gap:{FirstNonEmpty(blocked.ToolName, "(none)")}:{SummarizeCollapseDetail(blockedSummary)}";

            return $"blocked:{FirstNonEmpty(blocked.ToolName, "(none)")}:{SummarizeCollapseDetail(blockedSummary)}";
        }

        var failed = calls.LastOrDefault(call => string.Equals(call.Stage, "failed", StringComparison.OrdinalIgnoreCase));
        if (failed is not null)
            return $"failed:{FirstNonEmpty(failed.ToolName, "(none)")}:{SummarizeCollapseDetail(failed.Summary)}";

        if (completed == 0)
            return "planned_only";

        return $"ended_early_after_{completed}_of_{expected}";
    }

    private static string SummarizeCollapseDetail(string? value)
    {
        var line = FirstNonEmpty(value).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        line = line.Trim();
        if (line.Length > 120)
            line = line[..120] + "...";
        return string.IsNullOrWhiteSpace(line) ? "(none)" : line;
    }

    private static string DescribeExecutionShape(CSharpExecutionShapeKind executionShape)
    {
        return executionShape switch
        {
            CSharpExecutionShapeKind.MultiStep => "multi_step",
            CSharpExecutionShapeKind.OneStepByDesign => "one_step_by_design",
            CSharpExecutionShapeKind.TruthOnly => "truth_only",
            _ => "unknown"
        };
    }

    private static bool IsDotnetStack(string? targetStack)
    {
        return string.IsNullOrWhiteSpace(targetStack)
            || string.Equals(targetStack.Trim(), "dotnet_desktop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetStack.Trim(), "unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDotnetRuntime(TaskboardPlanRunStateRecord? runState)
    {
        if (runState is null)
            return false;

        return string.Equals(runState.LastResolvedBuildProfile?.StackFamily.ToString(), "DotnetDesktop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(FirstNonEmpty(runState.LastExecutionGoalResolution?.TargetStack, runState.LastCompletedStackFamily, runState.LastBlockerStackFamily), "dotnet_desktop", StringComparison.OrdinalIgnoreCase)
            || runState.RecentObservedToolNames.Any(tool => tool.StartsWith("dotnet_", StringComparison.OrdinalIgnoreCase))
            || runState.RecentObservedToolNames.Any(tool => tool is "plan_repair" or "preview_patch_draft" or "apply_patch_draft" or "verify_patch_draft");
    }

    private static bool IsExplicitArtifactInspection(TaskboardRunWorkItem workItem)
    {
        return string.Equals(Normalize(workItem.OperationKind), "inspect_context_artifacts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Normalize(workItem.DirectToolRequest?.ToolName), "show_artifacts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Normalize(workItem.DirectToolRequest?.ToolName), "show_memory", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant();
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
