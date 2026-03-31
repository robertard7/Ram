using System.IO;
using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class ToolChainControllerService
{
    private readonly ActionableSuggestionService _actionableSuggestionService = new();
    private readonly ArtifactClassificationService _artifactClassificationService = new();
    private static readonly DotnetScaffoldSurfaceService DotnetScaffoldSurfaceService = new();
    private readonly DotnetProjectReferencePolicyService _dotnetProjectReferencePolicyService = new();
    private readonly ToolChainTemplateRegistry _registry = new();

    public ToolChainRecord StartChain(
        string workspaceRoot,
        string userPrompt,
        ToolRequest initialRequest,
        bool allowModelSummary,
        ResponseMode responseMode)
    {
        var template = !string.IsNullOrWhiteSpace(initialRequest.PreferredChainTemplateName)
            ? _registry.ResolveTemplateForName(initialRequest.PreferredChainTemplateName)
            : _registry.ResolveTemplate(initialRequest.ToolName);
        return new ToolChainRecord
        {
            ChainId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            InitiatingUserPrompt = userPrompt ?? "",
            ResponseMode = responseMode,
            ChainType = template.ChainType,
            ChainGoal = BuildGoal(userPrompt ?? "", initialRequest.ToolName),
            SelectedTemplateName = template.Name,
            CurrentStatus = ToolChainStatus.Planned,
            StopReason = ToolChainStopReason.Unknown,
            ModelSummaryRequested = allowModelSummary && template.ModelSummaryAllowed
        };
    }

    public ToolChainTemplate GetTemplate(ToolChainRecord chain)
    {
        return _registry.ResolveTemplateForName(chain.SelectedTemplateName);
    }

    public bool IsStepAllowed(ToolChainRecord chain, ToolRequest request, ToolRequest? previousRequest)
    {
        return ValidateNextStep(chain, request, previousRequest).Allowed;
    }

    public ChainTemplateValidationResult ValidateNextStep(ToolChainRecord chain, ToolRequest request, ToolRequest? previousRequest)
    {
        var template = GetTemplate(chain);
        return _registry.ValidateStep(template, request, previousRequest);
    }

    public ToolRequest? GetNextStepRequest(
        ToolChainRecord chain,
        ToolRequest initialRequest,
        ToolRequest? previousRequest,
        ToolResult? previousResult,
        string workspaceRoot,
        RamDbService ramDbService,
        out ToolChainStopReason stopReason,
        out string stopSummary)
    {
        var template = GetTemplate(chain);
        stopReason = ToolChainStopReason.Unknown;
        stopSummary = "";

        if (chain.Steps.Count > template.MaxStepCount)
        {
            stopReason = ToolChainStopReason.ChainLimitReached;
            stopSummary = $"Controlled chain stopped after reaching the {template.MaxStepCount}-step limit for template `{template.Name}`.";
            return null;
        }

        if (chain.Steps.Count == template.MaxStepCount)
        {
            stopReason = ToolChainStopReason.GoalCompleted;
            stopSummary = $"Controlled chain reached the final allowed step for template `{template.Name}`.";
            return null;
        }

        if (previousResult is not null && !previousResult.Success)
        {
            stopReason = MapFailureStopReason(previousResult);
            stopSummary = FirstNonEmpty(previousResult.Summary, previousResult.ErrorMessage, "The controlled tool chain stopped after a failed step.");
            return null;
        }

        switch (template.Name)
        {
            case "repair_preview_chain":
                return GetRepairPreviewStep(chain, initialRequest, previousRequest, workspaceRoot, ramDbService, out stopReason, out stopSummary);
            case "repair_execution_chain":
                return GetRepairExecutionStep(chain, initialRequest, previousRequest, previousResult, workspaceRoot, ramDbService, out stopReason, out stopSummary);
            case "workspace.build_verify.v1":
                return GetWorkspaceBuildVerifyStep(chain, initialRequest, previousRequest, previousResult, workspaceRoot, out stopReason, out stopSummary);
            case "dotnet.navigation_wireup.v1":
                return GetDotnetNavigationWireupStep(chain, initialRequest, previousRequest, workspaceRoot, out stopReason, out stopSummary);
            case "dotnet.shell_registration_wireup.v1":
                return GetDotnetShellRegistrationWireupStep(chain, initialRequest, previousRequest, workspaceRoot, out stopReason, out stopSummary);
            case "dotnet.project_attach.v1":
                return GetDotnetProjectAttachStep(chain, initialRequest, previousRequest, previousResult, workspaceRoot, out stopReason, out stopSummary);
            case "dotnet.sqlite_storage_bootstrap.v1":
                return GetDotnetSqliteStorageBootstrapStep(chain, initialRequest, previousRequest, out stopReason, out stopSummary);
            case "dotnet.domain_contracts_scaffold.v1":
                return GetDotnetDomainContractsScaffoldStep(chain, initialRequest, previousRequest, previousResult, workspaceRoot, out stopReason, out stopSummary);
            case "dotnet.repository_scaffold.v1":
                return GetDotnetRepositoryScaffoldStep(chain, initialRequest, previousRequest, previousResult, workspaceRoot, out stopReason, out stopSummary);
            case "dotnet.check_runner_scaffold.v1":
                return GetDotnetCheckRunnerScaffoldStep(chain, initialRequest, previousRequest, previousResult, workspaceRoot, out stopReason, out stopSummary);
            case "build_profile_chain":
                return GetBuildProfileStep(chain, initialRequest, previousRequest, out stopReason, out stopSummary);

            default:
                return GetSingleStepRequest(chain, initialRequest, out stopReason, out stopSummary);
        }
    }

    public void RecordExecutedStep(
        ToolChainRecord chain,
        ToolRequest request,
        ToolResult result,
        bool executionAttempted,
        string executionBlockedReason,
        bool mutationObserved,
        IEnumerable<string>? touchedFilePaths,
        IEnumerable<string>? linkedArtifactPaths,
        ChainTemplateValidationResult? validation = null)
    {
        chain.Steps.Add(new ToolChainStepRecord
        {
            ChainId = chain.ChainId,
            StepIndex = chain.Steps.Count + 1,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            TemplateName = chain.SelectedTemplateName,
            ChainStepId = validation?.AttemptedStepId ?? NormalizeToolName(request.ToolName),
            PreviousChainStepId = validation?.LastAcceptedStepId ?? chain.LastAcceptedChainStepId,
            AllowedNextStepIds = validation?.AllowedNextStepIds is null ? [] : [.. validation.AllowedNextStepIds],
            ChainValidationBlockerCode = validation?.BlockerCode.ToString().ToLowerInvariant() ?? "",
            ChainMismatchOrigin = validation?.MismatchOrigin.ToString().ToLowerInvariant() ?? "",
            ToolName = request.ToolName,
            ToolArgumentsSummary = SummarizeArguments(request),
            AllowedByPolicy = true,
            ResultClassification = FirstNonEmpty(result.OutcomeType, result.Success ? "success" : "execution_failure"),
            ResultSummary = FirstNonEmpty(result.Summary, result.ErrorMessage, result.Output),
            ExecutionAttempted = executionAttempted,
            ExecutionBlockedReason = executionBlockedReason,
            MutationObserved = mutationObserved,
            TouchedFilePaths = touchedFilePaths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [],
            LinkedArtifactPaths = linkedArtifactPaths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [],
            StructuredDataJson = result.StructuredDataJson ?? ""
        });

        chain.LastAcceptedChainStepId = validation?.AttemptedStepId ?? NormalizeToolName(request.ToolName);
        chain.LastAttemptedChainStepId = validation?.AttemptedStepId ?? NormalizeToolName(request.ToolName);
        chain.LastAllowedNextStepIds = validation?.AllowedNextStepIds is null ? [] : [.. validation.AllowedNextStepIds];
        chain.LastChainValidationBlockerCode = "";
        chain.LastChainMismatchOrigin = "";
        chain.LastChainValidationSummary = "";
        chain.CurrentStatus = result.Success ? ToolChainStatus.Running : ToolChainStatus.Failed;
    }

    public void RecordBlockedStep(ToolChainRecord chain, ToolRequest request, ToolChainStopReason stopReason, string summary, ChainTemplateValidationResult? validation = null)
    {
        chain.Steps.Add(new ToolChainStepRecord
        {
            ChainId = chain.ChainId,
            StepIndex = chain.Steps.Count + 1,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            TemplateName = chain.SelectedTemplateName,
            ChainStepId = validation?.AttemptedStepId ?? NormalizeToolName(request.ToolName),
            PreviousChainStepId = validation?.LastAcceptedStepId ?? chain.LastAcceptedChainStepId,
            AllowedNextStepIds = validation?.AllowedNextStepIds is null ? [] : [.. validation.AllowedNextStepIds],
            ChainValidationBlockerCode = validation?.BlockerCode.ToString().ToLowerInvariant() ?? "",
            ChainMismatchOrigin = validation?.MismatchOrigin.ToString().ToLowerInvariant() ?? "",
            ToolName = request.ToolName,
            ToolArgumentsSummary = SummarizeArguments(request),
            AllowedByPolicy = false,
            ResultClassification = "policy_blocked_next_step",
            ResultSummary = summary,
            ExecutionAttempted = false,
            ExecutionBlockedReason = summary
        });

        chain.LastAttemptedChainStepId = validation?.AttemptedStepId ?? NormalizeToolName(request.ToolName);
        chain.LastAllowedNextStepIds = validation?.AllowedNextStepIds is null ? [] : [.. validation.AllowedNextStepIds];
        chain.LastChainValidationBlockerCode = validation?.BlockerCode.ToString().ToLowerInvariant() ?? "";
        chain.LastChainMismatchOrigin = validation?.MismatchOrigin.ToString().ToLowerInvariant() ?? "";
        chain.LastChainValidationSummary = FirstNonEmpty(validation?.Message, summary);
        chain.CurrentStatus = ToolChainStatus.Blocked;
        chain.StopReason = stopReason;
        chain.FinalOutcomeSummary = summary;
        chain.CompletedUtc = DateTime.UtcNow.ToString("O");
    }

    public void FinalizeChain(
        ToolChainRecord chain,
        ToolChainStopReason requestedStopReason,
        ToolResult? lastResult,
        string workspaceRoot,
        RamDbService ramDbService)
    {
        var finalStopReason = NormalizeStopReason(chain, requestedStopReason, lastResult, workspaceRoot, ramDbService);
        chain.StopReason = finalStopReason;
        chain.CurrentStatus = finalStopReason switch
        {
            ToolChainStopReason.ManualOnly
                or ToolChainStopReason.SafetyBlocked
                or ToolChainStopReason.ScopeBlocked
                or ToolChainStopReason.PolicyBlockedNextStep
                or ToolChainStopReason.NoFurtherStepAllowed
                or ToolChainStopReason.InvalidModelStep
                or ToolChainStopReason.ChainLimitReached => ToolChainStatus.Blocked,
            ToolChainStopReason.ToolFailed => ToolChainStatus.Failed,
            _ => ToolChainStatus.Completed
        };
        chain.FinalOutcomeSummary = BuildFinalOutcomeSummary(chain, finalStopReason, lastResult, workspaceRoot, ramDbService);
        chain.ActionableSuggestions = _actionableSuggestionService.BuildSuggestions(workspaceRoot, ramDbService, chain);
        chain.SuggestedNextAction = chain.ActionableSuggestions
            .OrderBy(suggestion => ReadinessOrder(suggestion.Readiness))
            .ThenBy(suggestion => suggestion.Priority)
            .Select(suggestion => BuildSuggestionPromptSummary(suggestion))
            .FirstOrDefault(summary => !string.IsNullOrWhiteSpace(summary))
            ?? BuildSuggestedNextAction(chain, finalStopReason, workspaceRoot, ramDbService);
        chain.CompletedUtc = DateTime.UtcNow.ToString("O");
    }

    public ToolChainSummaryInput BuildSummaryInput(ToolChainRecord chain)
    {
        return new ToolChainSummaryInput
        {
            ChainId = chain.ChainId,
            ResponseMode = chain.ResponseMode,
            ChainType = chain.ChainType,
            TemplateName = chain.SelectedTemplateName,
            UserGoal = chain.ChainGoal,
            InitiatingUserPrompt = chain.InitiatingUserPrompt,
            Status = chain.CurrentStatus,
            StopReason = chain.StopReason,
            FinalOutcomeSummary = chain.FinalOutcomeSummary,
            SuggestedNextAction = chain.SuggestedNextAction,
            ExecutionOccurred = chain.Steps.Any(step => step.ExecutionAttempted),
            ExecutionBlocked = chain.Steps.Any(step => !step.AllowedByPolicy || !string.IsNullOrWhiteSpace(step.ExecutionBlockedReason)),
            ActionableSuggestions = [.. chain.ActionableSuggestions],
            Steps = [.. chain.Steps]
        };
    }

    public ArtifactRecord SaveChainArtifact(RamDbService ramDbService, string workspaceRoot, ToolChainRecord chain)
    {
        return SaveArtifact(
            ramDbService,
            workspaceRoot,
            "tool_chain_record",
            $"Tool chain: {chain.ChainGoal}",
            $".ram/tool-chains/{chain.ChainId}.json",
            JsonSerializer.Serialize(chain, new JsonSerializerOptions { WriteIndented = true }),
            chain.FinalOutcomeSummary);
    }

    public ArtifactRecord SaveSummaryArtifact(RamDbService ramDbService, string workspaceRoot, ToolChainRecord chain, string summary)
    {
        return SaveArtifact(
            ramDbService,
            workspaceRoot,
            "tool_chain_summary",
            $"Tool chain summary: {chain.ChainGoal}",
            $".ram/tool-chains/{chain.ChainId}-summary.txt",
            summary,
            summary);
    }

    public ArtifactRecord SaveChainContractArtifact(RamDbService ramDbService, string workspaceRoot, ToolChainRecord chain)
    {
        var template = GetTemplate(chain);
        return SaveArtifact(
            ramDbService,
            workspaceRoot,
            "taskboard_chain_contract",
            $"Chain contract: {template.Name}",
            $".ram/tool-chains/{chain.ChainId}-contract.json",
            JsonSerializer.Serialize(new
            {
                chain_id = chain.ChainId,
                template_name = template.Name,
                step_graph = template.StepGraph
            }, new JsonSerializerOptions { WriteIndented = true }),
            $"template={template.Name} start_steps={template.StepGraph.StartStepIds.Count}");
    }

    public ArtifactRecord? SaveChainRejectionArtifact(RamDbService ramDbService, string workspaceRoot, ToolChainRecord chain)
    {
        if (chain.StopReason != ToolChainStopReason.PolicyBlockedNextStep
            || string.IsNullOrWhiteSpace(chain.LastAttemptedChainStepId))
        {
            return null;
        }

        return SaveArtifact(
            ramDbService,
            workspaceRoot,
            "taskboard_chain_rejection",
            $"Chain rejection: {chain.SelectedTemplateName}",
            $".ram/tool-chains/{chain.ChainId}-rejection.json",
            JsonSerializer.Serialize(new
            {
                chain_id = chain.ChainId,
                template_name = chain.SelectedTemplateName,
                last_accepted_step_id = chain.LastAcceptedChainStepId,
                attempted_step_id = chain.LastAttemptedChainStepId,
                allowed_next_step_ids = chain.LastAllowedNextStepIds,
                blocker_code = chain.LastChainValidationBlockerCode,
                mismatch_origin = chain.LastChainMismatchOrigin,
                summary = chain.LastChainValidationSummary
            }, new JsonSerializerOptions { WriteIndented = true }),
            FirstNonEmpty(chain.LastChainValidationSummary, chain.FinalOutcomeSummary));
    }

    private ToolRequest? GetSingleStepRequest(
        ToolChainRecord chain,
        ToolRequest initialRequest,
        out ToolChainStopReason stopReason,
        out string stopSummary)
    {
        if (chain.Steps.Count == 0)
        {
            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return initialRequest.Clone();
        }

        stopReason = ToolChainStopReason.GoalCompleted;
        stopSummary = "The selected chain template allows no further tool steps.";
        return null;
    }

    private ToolRequest? GetRepairPreviewStep(
        ToolChainRecord chain,
        ToolRequest initialRequest,
        ToolRequest? previousRequest,
        string workspaceRoot,
        RamDbService ramDbService,
        out ToolChainStopReason stopReason,
        out string stopSummary)
    {
        if (chain.Steps.Count == 0)
        {
            if (NeedsRepairProposalPreparation(workspaceRoot, ramDbService))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return BuildPreparationRequest(
                    "plan_repair",
                    "Controlled repair chain preparation before previewing a patch.",
                    initialRequest);
            }

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return initialRequest.Clone();
        }

        if (string.Equals(previousRequest?.ToolName, "plan_repair", StringComparison.OrdinalIgnoreCase))
        {
            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return initialRequest.Clone();
        }

        stopReason = ToolChainStopReason.GoalCompleted;
        stopSummary = "Repair preview chain completed the allowed steps.";
        return null;
    }

    private ToolRequest? GetRepairExecutionStep(
        ToolChainRecord chain,
        ToolRequest initialRequest,
        ToolRequest? previousRequest,
        ToolResult? previousResult,
        string workspaceRoot,
        RamDbService ramDbService,
        out ToolChainStopReason stopReason,
        out string stopSummary)
    {
        if (chain.Steps.Count == 0)
        {
            if (NeedsRepairProposalPreparation(workspaceRoot, ramDbService))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return BuildPreparationRequest(
                    "plan_repair",
                    "Controlled repair chain preparation before planning and applying a bounded fix.",
                    initialRequest);
            }

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return initialRequest.Clone();
        }

        if (string.Equals(previousRequest?.ToolName, "plan_repair", StringComparison.OrdinalIgnoreCase))
        {
            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return BuildRepairFollowUpRequest(
                "preview_patch_draft",
                "Controlled repair chain preview after generating the repair proposal.",
                initialRequest,
                previousResult);
        }

        if (string.Equals(previousRequest?.ToolName, "preview_patch_draft", StringComparison.OrdinalIgnoreCase))
        {
            if (previousResult?.Success == true
                && string.Equals(previousResult.OutcomeType, "patch_draft_ready", StringComparison.OrdinalIgnoreCase))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return BuildRepairFollowUpRequest(
                    "apply_patch_draft",
                    "Controlled repair chain applying the locally safe patch draft.",
                    initialRequest,
                    previousResult);
            }

            stopReason = ToolChainStopReason.NoFurtherStepAllowed;
            stopSummary = "Repair execution chain stopped after preview because no locally applicable patch draft was available.";
            return null;
        }

        if (string.Equals(previousRequest?.ToolName, "apply_patch_draft", StringComparison.OrdinalIgnoreCase))
        {
            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return BuildRepairFollowUpRequest(
                "verify_patch_draft",
                "Controlled repair chain rerunning bounded verification after the patch apply.",
                initialRequest,
                previousResult);
        }

        stopReason = ToolChainStopReason.GoalCompleted;
        stopSummary = "Repair execution chain completed the allowed steps.";
        return null;
    }

    private ToolRequest? GetBuildProfileStep(
        ToolChainRecord chain,
        ToolRequest initialRequest,
        ToolRequest? previousRequest,
        out ToolChainStopReason stopReason,
        out string stopSummary)
    {
        if (chain.Steps.Count == 0)
        {
            if (string.Equals(initialRequest.ToolName, "list_build_profiles", StringComparison.OrdinalIgnoreCase))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return BuildPreparationRequest(
                    "detect_build_system",
                    "Controlled build chain preparation before listing build profiles.");
            }

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return initialRequest.Clone();
        }

        if (string.Equals(previousRequest?.ToolName, "detect_build_system", StringComparison.OrdinalIgnoreCase))
        {
            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return BuildPreparationRequest(
                "list_build_profiles",
                "Controlled build chain follow-up after detecting the workspace build system.");
        }

        stopReason = ToolChainStopReason.GoalCompleted;
        stopSummary = "Build profile inspection chain completed the allowed steps.";
        return null;
    }

    private ToolRequest? GetDotnetNavigationWireupStep(
        ToolChainRecord chain,
        ToolRequest initialRequest,
        ToolRequest? previousRequest,
        string workspaceRoot,
        out ToolChainStopReason stopReason,
        out string stopSummary)
    {
        return GetDotnetStateWireupStep(
            chain,
            initialRequest,
            previousRequest,
            workspaceRoot,
            shellRegistration: false,
            out stopReason,
            out stopSummary);
    }

    private ToolRequest? GetDotnetShellRegistrationWireupStep(
        ToolChainRecord chain,
        ToolRequest initialRequest,
        ToolRequest? previousRequest,
        string workspaceRoot,
        out ToolChainStopReason stopReason,
        out string stopSummary)
    {
        return GetDotnetStateWireupStep(
            chain,
            initialRequest,
            previousRequest,
            workspaceRoot,
            shellRegistration: true,
            out stopReason,
            out stopSummary);
    }

    private ToolRequest? GetDotnetStateWireupStep(
        ToolChainRecord chain,
        ToolRequest initialRequest,
        ToolRequest? previousRequest,
        string workspaceRoot,
        bool shellRegistration,
        out ToolChainStopReason stopReason,
        out string stopSummary)
    {
        if (chain.Steps.Count == 0)
        {
            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return initialRequest.Clone();
        }

        if (string.Equals(previousRequest?.ToolName, "make_dir", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveDotnetStateContext(previousRequest!, out var stateDirectory, out var projectName))
            {
                stopReason = ToolChainStopReason.NoFurtherStepAllowed;
                stopSummary = "Navigation wireup chain stopped after `make_dir` because the state directory or project name could not be inferred.";
                return null;
            }

            var fileName = shellRegistration ? "ShellNavigationRegistry.cs" : "NavigationItem.cs";
            var content = shellRegistration
                ? BuildShellNavigationRegistryCs(projectName)
                : BuildNavigationItemCs(projectName);

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return BuildNamedWorkspaceWriteRequest(
                "register_navigation",
                shellRegistration
                    ? "Controlled shell-registration wireup after creating the grouped-shell state directory."
                    : "Controlled navigation wireup after creating the navigation/state directory.",
                initialRequest,
                CombineRelativePath(stateDirectory, fileName),
                content);
        }

        if (string.Equals(previousRequest?.ToolName, "register_navigation", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveDotnetStateContext(previousRequest!, out var stateDirectory, out var projectName))
            {
                stopReason = ToolChainStopReason.NoFurtherStepAllowed;
                stopSummary = "Navigation wireup chain stopped after `register_navigation` because the follow-up state/viewmodel target could not be inferred.";
                return null;
            }

            var appStatePath = CombineRelativePath(stateDirectory, "AppState.cs");
            var appStateExists = WorkspaceRelativeFileExists(workspaceRoot, appStatePath);
            var fileName = shellRegistration && appStateExists
                ? "ShellViewModel.cs"
                : "AppState.cs";
            var content = string.Equals(fileName, "ShellViewModel.cs", StringComparison.OrdinalIgnoreCase)
                ? BuildShellViewModelCs(projectName)
                : BuildAppStateCs(projectName);
            var dependencyArguments = string.Equals(fileName, "ShellViewModel.cs", StringComparison.OrdinalIgnoreCase)
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["dependency_prerequisites"] = "AppState",
                    ["dependency_ordering_status"] = appStateExists ? "prerequisite_already_satisfied" : "reordered_prerequisite_first",
                    ["dependency_ordering_summary"] = appStateExists
                        ? "ShellViewModel generation proceeded because AppState already existed in the same surface."
                        : "ShellViewModel generation was deferred until AppState could be written first."
                }
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["dependency_prerequisites"] = shellRegistration
                        ? "NavigationItem,ShellNavigationRegistry"
                        : "NavigationItem",
                    ["dependency_ordering_status"] = shellRegistration ? "reordered_prerequisite_first" : "satisfied_prerequisites",
                    ["dependency_ordering_summary"] = shellRegistration
                        ? "AppState was emitted before ShellViewModel because grouped-shell viewmodel generation depends on the state model."
                        : "AppState followed the navigation registration prerequisite."
                };

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return BuildNamedWorkspaceWriteRequest(
                "create_dotnet_viewmodel",
                shellRegistration && appStateExists
                    ? "Controlled shell-registration wireup follow-up writing the grouped-shell viewmodel after AppState was already satisfied."
                    : shellRegistration
                        ? "Controlled shell-registration wireup follow-up writing AppState before the grouped-shell viewmodel."
                        : "Controlled navigation wireup follow-up writing the app state viewmodel.",
                initialRequest,
                CombineRelativePath(stateDirectory, fileName),
                content,
                MergeArguments(
                    string.Equals(fileName, "ShellViewModel.cs", StringComparison.OrdinalIgnoreCase)
                        ? BuildCodeIntentArguments("state", "viewmodel", projectName, $"{projectName}.State", "integrated", ResolveDotnetBuildTarget(workspaceRoot, initialRequest, previousRequest!, stateDirectory, projectName), "binding_required", "verification_required")
                        : BuildCodeIntentArguments("state", "model", projectName, $"{projectName}.State", "integrated", ResolveDotnetBuildTarget(workspaceRoot, initialRequest, previousRequest!, stateDirectory, projectName), "viewmodel_required", "verification_required"),
                    dependencyArguments));
        }

        if (shellRegistration
            && string.Equals(previousRequest?.ToolName, "create_dotnet_viewmodel", StringComparison.OrdinalIgnoreCase)
            && previousRequest!.TryGetArgument("path", out var previousPath)
            && previousPath.EndsWith("AppState.cs", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveDotnetStateContext(previousRequest, out var stateDirectory, out var projectName))
            {
                stopReason = ToolChainStopReason.NoFurtherStepAllowed;
                stopSummary = "Shell-registration wireup chain stopped after `create_dotnet_viewmodel` because the grouped-shell viewmodel target could not be inferred.";
                return null;
            }

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return BuildNamedWorkspaceWriteRequest(
                "create_dotnet_viewmodel",
                "Controlled shell-registration wireup follow-up writing the grouped-shell viewmodel after AppState generation succeeded.",
                initialRequest,
                CombineRelativePath(stateDirectory, "ShellViewModel.cs"),
                BuildShellViewModelCs(projectName),
                MergeArguments(
                    BuildCodeIntentArguments("state", "viewmodel", projectName, $"{projectName}.State", "integrated", ResolveDotnetBuildTarget(workspaceRoot, initialRequest, previousRequest, stateDirectory, projectName), "binding_required", "verification_required"),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dependency_prerequisites"] = "AppState",
                        ["dependency_ordering_status"] = "prerequisite_satisfied_after_generation",
                        ["dependency_ordering_summary"] = "ShellViewModel generation resumed after AppState was generated and validated in the same wireup chain."
                    }));
        }

        stopReason = ToolChainStopReason.GoalCompleted;
        stopSummary = shellRegistration
            ? "Shell-registration wireup chain completed the allowed steps."
            : "Navigation wireup chain completed the allowed steps.";
        return null;
    }

    private ToolRequest? GetWorkspaceBuildVerifyStep(
        ToolChainRecord chain,
        ToolRequest initialRequest,
        ToolRequest? previousRequest,
        ToolResult? previousResult,
        string workspaceRoot,
        out ToolChainStopReason stopReason,
        out string stopSummary)
    {
        if (chain.Steps.Count == 0)
        {
            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return initialRequest.Clone();
        }

        if (string.Equals(previousRequest?.ToolName, "dotnet_build", StringComparison.OrdinalIgnoreCase))
        {
            if (previousResult?.Success != true)
            {
                stopReason = ToolChainStopReason.ToolFailed;
                stopSummary = FirstNonEmpty(previousResult?.Summary, previousResult?.ErrorMessage, "Build verification stopped after dotnet_build reported a failure.");
                return null;
            }

            if (!TryResolveDotnetTestTarget(workspaceRoot, initialRequest, out var testTarget))
            {
                stopReason = ToolChainStopReason.GoalCompleted;
                stopSummary = "Build verify chain completed after dotnet_build because no deterministic dotnet test target was discovered in the current workspace.";
                return null;
            }

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            var request = BuildFollowUpRequest(
                "dotnet_test",
                "Controlled build verification follow-up after a successful dotnet_build step.",
                initialRequest);
            request.Arguments["project"] = testTarget;
            return request;
        }

        stopReason = ToolChainStopReason.GoalCompleted;
        stopSummary = "Build verification chain completed the allowed steps.";
        return null;
    }

    private ToolRequest? GetDotnetProjectAttachStep(
        ToolChainRecord chain,
        ToolRequest initialRequest,
        ToolRequest? previousRequest,
        ToolResult? previousResult,
        string workspaceRoot,
        out ToolChainStopReason stopReason,
        out string stopSummary)
    {
        if (chain.Steps.Count == 0)
        {
            if (TryBuildMissingProjectAttachCreateStep(workspaceRoot, initialRequest, out var createRequest))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return createRequest;
            }

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return initialRequest.Clone();
        }

        if (string.Equals(previousRequest?.ToolName, "create_dotnet_project", StringComparison.OrdinalIgnoreCase)
            && initialRequest.TryGetArgument("solution_path", out var solutionPath)
            && initialRequest.TryGetArgument("project_path", out var projectPath)
            && !string.IsNullOrWhiteSpace(solutionPath)
            && !string.IsNullOrWhiteSpace(projectPath))
        {
            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            var request = BuildFollowUpRequest(
                "add_project_to_solution",
                "Controlled project-attach continuity resumed after creating the missing project.",
                initialRequest,
                previousResult);
            request.Arguments["solution_path"] = NormalizeRelativePath(solutionPath);
            request.Arguments["project_path"] = NormalizeRelativePath(projectPath);
            ApplyProjectAttachContinuationArguments(
                request,
                solutionPath,
                projectPath,
                "attach_resumed_after_project_creation",
                "create_dotnet_project",
                $"Resumed solution attach after creating the missing project `{NormalizeRelativePath(projectPath)}`.",
                projectExistsAtDecision: WorkspaceFileExists(workspaceRoot, projectPath));
            return request;
        }

        stopReason = ToolChainStopReason.GoalCompleted;
        stopSummary = "Project attach chain completed the allowed steps.";
        return null;
    }

    private ToolRequest? GetDotnetSqliteStorageBootstrapStep(
        ToolChainRecord chain,
        ToolRequest initialRequest,
        ToolRequest? previousRequest,
        out ToolChainStopReason stopReason,
        out string stopSummary)
    {
        if (chain.Steps.Count == 0)
        {
            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return initialRequest.Clone();
        }

        if (string.Equals(previousRequest?.ToolName, "make_dir", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveDotnetStorageContext(previousRequest!, out var storageDirectory, out var storageProjectName))
            {
                stopReason = ToolChainStopReason.NoFurtherStepAllowed;
                stopSummary = "SQLite storage bootstrap chain stopped after `make_dir` because the storage directory or project identity could not be inferred.";
                return null;
            }

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return BuildNamedWorkspaceWriteRequest(
                "initialize_sqlite_storage_boundary",
                "Controlled SQLite storage bootstrap after creating the storage directory.",
                initialRequest,
                CombineRelativePath(storageDirectory, "ISettingsStore.cs"),
                BuildISettingsStoreCs(storageProjectName));
        }

        if (string.Equals(previousRequest?.ToolName, "initialize_sqlite_storage_boundary", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveDotnetStorageContext(previousRequest!, out var storageDirectory, out var storageProjectName))
            {
                stopReason = ToolChainStopReason.NoFurtherStepAllowed;
                stopSummary = "SQLite storage bootstrap chain stopped after `initialize_sqlite_storage_boundary` because the storage implementation target could not be inferred.";
                return null;
            }

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return BuildNamedWorkspaceWriteRequest(
                "register_di_service",
                "Controlled SQLite storage bootstrap follow-up writing the storage implementation.",
                initialRequest,
                CombineRelativePath(storageDirectory, "FileSettingsStore.cs"),
                BuildFileSettingsStoreCs(storageProjectName));
        }

        stopReason = ToolChainStopReason.GoalCompleted;
        stopSummary = "SQLite storage bootstrap chain completed the allowed steps.";
        return null;
    }

    private ToolRequest? GetDotnetDomainContractsScaffoldStep(
        ToolChainRecord chain,
        ToolRequest initialRequest,
        ToolRequest? previousRequest,
        ToolResult? previousResult,
        string workspaceRoot,
        out ToolChainStopReason stopReason,
        out string stopSummary)
    {
        if (chain.Steps.Count == 0)
        {
            if (TryBuildMissingProjectAttachCreateStep(workspaceRoot, initialRequest, out var createRequest))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return createRequest;
            }

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return initialRequest.Clone();
        }

        if (!TryResolveDotnetDomainContractsContext(
            workspaceRoot,
            previousRequest ?? initialRequest,
            initialRequest,
            out var coreProjectDirectory,
            out var coreProjectName,
            out var buildTarget))
        {
            stopReason = ToolChainStopReason.NoFurtherStepAllowed;
            stopSummary = "Domain contracts scaffold chain stopped because the core project directory or target project could not be inferred.";
            return null;
        }

        var appName = DeriveAppNameFromCoreProjectName(coreProjectName);
        var coreProjectPath = CombineRelativePath(coreProjectDirectory, $"{coreProjectName}.csproj");
        var contractsDirectory = CombineRelativePath(coreProjectDirectory, "Contracts");
        var modelsDirectory = CombineRelativePath(coreProjectDirectory, "Models");
        var contractPath = CombineRelativePath(contractsDirectory, "CheckDefinition.cs");
        var modelPath = CombineRelativePath(modelsDirectory, "FindingRecord.cs");

        if (string.Equals(previousRequest?.ToolName, "create_dotnet_project", StringComparison.OrdinalIgnoreCase))
        {
            var solutionPath = ResolveDotnetBuildTarget(workspaceRoot, initialRequest, previousRequest!, coreProjectDirectory, coreProjectName);
            if (!string.IsNullOrWhiteSpace(solutionPath) && solutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                var request = BuildFollowUpRequest(
                    "add_project_to_solution",
                    "Controlled domain-contracts scaffold after creating the core library project.",
                    initialRequest,
                    previousResult);
                request.Arguments["solution_path"] = solutionPath;
                request.Arguments["project_path"] = coreProjectPath;
                ApplyProjectAttachContinuationArguments(
                    request,
                    solutionPath,
                    coreProjectPath,
                    "attach_resumed_after_project_creation",
                    "create_dotnet_project",
                    "Resumed solution attach after creating the missing core contracts library project.",
                    projectExistsAtDecision: WorkspaceFileExists(workspaceRoot, coreProjectPath));
                return request;
            }
        }

        if (string.Equals(previousRequest?.ToolName, "add_project_to_solution", StringComparison.OrdinalIgnoreCase))
        {
            var referenceDecision = TryResolveContinuationReferenceDecision(workspaceRoot, previousRequest!)
                ?? TryResolveContinuationReferenceDecision(workspaceRoot, initialRequest)
                ?? TryResolveDeterministicReferenceDecision(workspaceRoot, coreProjectPath);
            if (referenceDecision.ShouldExecute)
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                var request = BuildFollowUpRequest(
                    "add_dotnet_project_reference",
                    $"Controlled domain-contracts scaffold wiring the deterministic follow-up project reference. {referenceDecision.DecisionSummary}",
                    initialRequest,
                    previousResult);
                request.Arguments["project_path"] = referenceDecision.EffectiveProjectPath;
                request.Arguments["reference_path"] = referenceDecision.EffectiveReferencePath;
                request.Arguments["reference_decision_summary"] = referenceDecision.DecisionSummary;
                return request;
            }

            if (TryBuildNextDomainContractsArtifactRequest(workspaceRoot, initialRequest, appName, contractsDirectory, modelsDirectory, contractPath, modelPath, out var nextRequest))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return nextRequest;
            }
        }

        if (string.Equals(previousRequest?.ToolName, "add_dotnet_project_reference", StringComparison.OrdinalIgnoreCase))
        {
            if (TryBuildNextDomainContractsArtifactRequest(workspaceRoot, initialRequest, appName, contractsDirectory, modelsDirectory, contractPath, modelPath, out var nextRequest))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return nextRequest;
            }
        }

        if (string.Equals(previousRequest?.ToolName, "make_dir", StringComparison.OrdinalIgnoreCase))
        {
            var previousPath = previousRequest!.TryGetArgument("path", out var resolvedPath)
                ? NormalizeRelativePath(resolvedPath)
                : "";
            if (string.Equals(previousPath, contractsDirectory, StringComparison.OrdinalIgnoreCase)
                && !WorkspaceDirectoryExists(workspaceRoot, modelsDirectory))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return BuildMakeDirectoryRequest(
                    initialRequest,
                    modelsDirectory,
                    "Controlled domain-contracts scaffold creating the Models directory.");
            }

            if (!WorkspaceFileExists(workspaceRoot, contractPath))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return BuildNamedWorkspaceWriteRequest(
                    "write_file",
                    "Controlled domain-contracts scaffold writing the core contract file.",
                    initialRequest,
                    contractPath,
                    BuildDotnetCheckDefinitionCs(appName));
            }

            if (!WorkspaceFileExists(workspaceRoot, modelPath))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return BuildNamedWorkspaceWriteRequest(
                    "write_file",
                    "Controlled domain-contracts scaffold writing the core domain model file.",
                    initialRequest,
                    modelPath,
                    BuildDotnetFindingRecordCs(appName));
            }
        }

        if (string.Equals(previousRequest?.ToolName, "write_file", StringComparison.OrdinalIgnoreCase))
        {
            var previousPath = previousRequest!.TryGetArgument("path", out var resolvedPath)
                ? NormalizeRelativePath(resolvedPath)
                : "";
            if (string.Equals(previousPath, contractPath, StringComparison.OrdinalIgnoreCase)
                && !WorkspaceFileExists(workspaceRoot, modelPath))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return BuildNamedWorkspaceWriteRequest(
                    "write_file",
                    "Controlled domain-contracts scaffold follow-up writing the core domain model file.",
                    initialRequest,
                    modelPath,
                    BuildDotnetFindingRecordCs(appName));
            }

            if (previousResult?.Success == true && !string.IsNullOrWhiteSpace(buildTarget))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                var request = BuildFollowUpRequest(
                    "dotnet_build",
                    "Controlled domain-contracts scaffold rerunning bounded verification after writing the core library files.",
                    initialRequest,
                    previousResult);
                request.Arguments["project"] = buildTarget;
                return request;
            }
        }

        if (string.Equals(previousRequest?.ToolName, "dotnet_build", StringComparison.OrdinalIgnoreCase))
        {
            if (previousResult?.Success != true)
            {
                stopReason = ToolChainStopReason.ToolFailed;
                stopSummary = FirstNonEmpty(previousResult?.Summary, previousResult?.ErrorMessage, "Domain contracts scaffold verification stopped after dotnet_build reported a failure.");
                return null;
            }

            if (!TryResolveDotnetTestTarget(workspaceRoot, initialRequest, out var testTarget))
            {
                stopReason = ToolChainStopReason.GoalCompleted;
                stopSummary = "Domain contracts scaffold chain completed after dotnet_build because no deterministic test target was discovered in the current workspace.";
                return null;
            }

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            var request = BuildFollowUpRequest(
                "dotnet_test",
                "Controlled domain-contracts scaffold follow-up after a successful dotnet_build step.",
                initialRequest,
                previousResult);
            request.Arguments["project"] = testTarget;
            return request;
        }

        stopReason = ToolChainStopReason.GoalCompleted;
        stopSummary = "Domain contracts scaffold chain completed the allowed steps.";
        return null;
    }

    private ToolRequest? GetDotnetRepositoryScaffoldStep(
        ToolChainRecord chain,
        ToolRequest initialRequest,
        ToolRequest? previousRequest,
        ToolResult? previousResult,
        string workspaceRoot,
        out ToolChainStopReason stopReason,
        out string stopSummary)
    {
        if (chain.Steps.Count == 0)
        {
            if (TryBuildMissingProjectAttachCreateStep(workspaceRoot, initialRequest, out var createRequest))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return createRequest;
            }

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return initialRequest.Clone();
        }

        if (!TryResolveDotnetRepositoryContext(workspaceRoot, previousRequest ?? initialRequest, initialRequest, out var repositoryDirectory, out var storageProjectName, out var buildTarget))
        {
            stopReason = ToolChainStopReason.NoFurtherStepAllowed;
            stopSummary = "Repository scaffold chain stopped because the repository directory or target project could not be inferred.";
            return null;
        }

        var repositoryContractPath = CombineRelativePath(repositoryDirectory, "ISnapshotRepository.cs");
        var repositoryImplPath = CombineRelativePath(repositoryDirectory, "SqliteSnapshotRepository.cs");

        if (string.Equals(previousRequest?.ToolName, "create_dotnet_project", StringComparison.OrdinalIgnoreCase))
        {
            var solutionPath = ResolveDotnetBuildTarget(workspaceRoot, initialRequest, previousRequest!, repositoryDirectory, storageProjectName);
            if (!string.IsNullOrWhiteSpace(solutionPath) && solutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                var request = BuildFollowUpRequest(
                    "add_project_to_solution",
                    "Controlled repository scaffold after creating the repository project.",
                    initialRequest,
                    previousResult);
                var projectPath = CombineRelativePath(repositoryDirectory, $"{storageProjectName}.csproj");
                request.Arguments["solution_path"] = solutionPath;
                request.Arguments["project_path"] = projectPath;
                ApplyProjectAttachContinuationArguments(
                    request,
                    solutionPath,
                    projectPath,
                    "attach_resumed_after_project_creation",
                    "create_dotnet_project",
                    "Resumed solution attach after creating the missing core library project.",
                    projectExistsAtDecision: WorkspaceFileExists(workspaceRoot, projectPath));
                return request;
            }
        }

        if (string.Equals(previousRequest?.ToolName, "add_project_to_solution", StringComparison.OrdinalIgnoreCase))
        {
            var projectPath = previousRequest!.TryGetArgument("project_path", out var resolvedProjectPath)
                ? NormalizeRelativePath(resolvedProjectPath)
                : CombineRelativePath(repositoryDirectory, $"{storageProjectName}.csproj");
            var referenceDecision = TryResolveContinuationReferenceDecision(workspaceRoot, previousRequest)
                ?? TryResolveContinuationReferenceDecision(workspaceRoot, initialRequest)
                ?? TryResolveDeterministicReferenceDecision(workspaceRoot, projectPath);
            if (referenceDecision.ShouldExecute)
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                var request = BuildFollowUpRequest(
                    "add_dotnet_project_reference",
                    $"Controlled repository scaffold wiring the next deterministic project reference. {referenceDecision.DecisionSummary}",
                    initialRequest,
                    previousResult);
                request.Arguments["project_path"] = referenceDecision.EffectiveProjectPath;
                request.Arguments["reference_path"] = referenceDecision.EffectiveReferencePath;
                request.Arguments["reference_decision_summary"] = referenceDecision.DecisionSummary;
                return request;
            }

            if (!WorkspaceFileExists(workspaceRoot, repositoryContractPath))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return BuildNamedWorkspaceWriteRequest(
                    "write_file",
                    "Controlled repository scaffold writing the repository contract after project attach.",
                    initialRequest,
                    repositoryContractPath,
                    BuildSnapshotRepositoryContractCs(storageProjectName),
                    BuildCodeIntentArguments("contracts", "interface", storageProjectName, $"{storageProjectName}.Storage", "structural", buildTarget, "implementation_required", "verification_required"));
            }
        }

        if (string.Equals(previousRequest?.ToolName, "add_dotnet_project_reference", StringComparison.OrdinalIgnoreCase))
        {
            if (!WorkspaceFileExists(workspaceRoot, repositoryContractPath))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return BuildNamedWorkspaceWriteRequest(
                    "write_file",
                    "Controlled repository scaffold writing the repository contract after project reference wiring.",
                    initialRequest,
                    repositoryContractPath,
                    BuildSnapshotRepositoryContractCs(storageProjectName),
                    BuildCodeIntentArguments("contracts", "interface", storageProjectName, $"{storageProjectName}.Storage", "structural", buildTarget, "implementation_required", "verification_required"));
            }
        }

        if (string.Equals(previousRequest?.ToolName, "make_dir", StringComparison.OrdinalIgnoreCase))
        {
            if (!WorkspaceFileExists(workspaceRoot, repositoryContractPath))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return BuildNamedWorkspaceWriteRequest(
                    "write_file",
                    "Controlled repository scaffold after creating the repository directory.",
                    initialRequest,
                    repositoryContractPath,
                    BuildSnapshotRepositoryContractCs(storageProjectName),
                    BuildCodeIntentArguments("contracts", "interface", storageProjectName, $"{storageProjectName}.Storage", "structural", buildTarget, "implementation_required", "verification_required"));
            }

            if (!WorkspaceFileExists(workspaceRoot, repositoryImplPath))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return BuildNamedWorkspaceWriteRequest(
                    "write_file",
                    "Controlled repository scaffold after creating the repository directory.",
                    initialRequest,
                    repositoryImplPath,
                    BuildSnapshotRepositoryImplCs(storageProjectName),
                    BuildCodeIntentArguments("storage", "repository", storageProjectName, $"{storageProjectName}.Storage", "integrated", buildTarget, "consumer_required", "registration_required", "verification_required"));
            }
        }

        if (string.Equals(previousRequest?.ToolName, "write_file", StringComparison.OrdinalIgnoreCase))
        {
            var previousPath = previousRequest!.TryGetArgument("path", out var resolvedPath)
                ? NormalizeRelativePath(resolvedPath)
                : "";
            if (previousPath.EndsWith("ISnapshotRepository.cs", StringComparison.OrdinalIgnoreCase)
                && !WorkspaceFileExists(workspaceRoot, repositoryImplPath))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return BuildNamedWorkspaceWriteRequest(
                    "write_file",
                    "Controlled repository scaffold follow-up writing the repository implementation.",
                    initialRequest,
                    repositoryImplPath,
                    BuildSnapshotRepositoryImplCs(storageProjectName),
                    BuildCodeIntentArguments("storage", "repository", storageProjectName, $"{storageProjectName}.Storage", "integrated", buildTarget, "consumer_required", "registration_required", "verification_required"));
            }

            if (previousResult?.Success == true && !string.IsNullOrWhiteSpace(buildTarget))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                var request = BuildFollowUpRequest(
                    "dotnet_build",
                    "Controlled repository scaffold rerunning bounded verification after repository file writes.",
                    initialRequest,
                    previousResult);
                request.Arguments["project"] = buildTarget;
                return request;
            }
        }

        if (string.Equals(previousRequest?.ToolName, "dotnet_build", StringComparison.OrdinalIgnoreCase))
        {
            if (previousResult?.Success != true)
            {
                stopReason = ToolChainStopReason.ToolFailed;
                stopSummary = FirstNonEmpty(previousResult?.Summary, previousResult?.ErrorMessage, "Repository scaffold verification stopped after dotnet_build reported a failure.");
                return null;
            }

            if (!TryResolveDotnetTestTarget(workspaceRoot, initialRequest, out var testTarget))
            {
                stopReason = ToolChainStopReason.GoalCompleted;
                stopSummary = "Repository scaffold chain completed after dotnet_build because no deterministic test target was discovered in the current workspace.";
                return null;
            }

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            var request = BuildFollowUpRequest(
                "dotnet_test",
                "Controlled repository scaffold follow-up after a successful dotnet_build step.",
                initialRequest,
                previousResult);
            request.Arguments["project"] = testTarget;
            return request;
        }

        stopReason = ToolChainStopReason.GoalCompleted;
        stopSummary = "Repository scaffold chain completed the allowed steps.";
        return null;
    }

    private ToolRequest? GetDotnetCheckRunnerScaffoldStep(
        ToolChainRecord chain,
        ToolRequest initialRequest,
        ToolRequest? previousRequest,
        ToolResult? previousResult,
        string workspaceRoot,
        out ToolChainStopReason stopReason,
        out string stopSummary)
    {
        if (chain.Steps.Count == 0)
        {
            if (TryBuildMissingTestProjectCreateStep(workspaceRoot, initialRequest, out var createRequest))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return createRequest;
            }

            if (TryBuildMissingTestProjectAttachStep(workspaceRoot, initialRequest, out var attachRequest))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return attachRequest;
            }

            stopReason = ToolChainStopReason.Unknown;
            stopSummary = "";
            return initialRequest.Clone();
        }

        if (string.Equals(previousRequest?.ToolName, "create_dotnet_project", StringComparison.OrdinalIgnoreCase))
        {
            if (TryBuildMissingTestProjectAttachStep(workspaceRoot, initialRequest, out var attachRequest))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return attachRequest;
            }

            if (TryBuildResolvedDotnetTestRequest(workspaceRoot, initialRequest, previousResult, "Controlled check-runner scaffold resumed direct test execution after creating the missing test project.", out var dotnetTestRequest))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return dotnetTestRequest;
            }

            stopReason = ToolChainStopReason.NoFurtherStepAllowed;
            stopSummary = "Check-runner scaffold stopped after creating the test project because the test target still could not be resolved.";
            return null;
        }

        if (string.Equals(previousRequest?.ToolName, "add_project_to_solution", StringComparison.OrdinalIgnoreCase))
        {
            if (TryBuildResolvedDotnetTestRequest(workspaceRoot, initialRequest, previousResult, "Controlled check-runner scaffold resumed direct test execution after attaching the test project to the solution.", out var dotnetTestRequest))
            {
                stopReason = ToolChainStopReason.Unknown;
                stopSummary = "";
                return dotnetTestRequest;
            }

            stopReason = ToolChainStopReason.NoFurtherStepAllowed;
            stopSummary = "Check-runner scaffold stopped after project attach because the test target still could not be resolved.";
            return null;
        }

        stopReason = ToolChainStopReason.GoalCompleted;
        stopSummary = "Check-runner scaffold chain completed the allowed steps.";
        return null;
    }

    private static bool TryBuildMissingProjectAttachCreateStep(
        string workspaceRoot,
        ToolRequest initialRequest,
        out ToolRequest request)
    {
        request = new ToolRequest();
        if (!string.Equals(initialRequest.ToolName, "add_project_to_solution", StringComparison.OrdinalIgnoreCase)
            || !initialRequest.TryGetArgument("project_path", out var projectPath)
            || string.IsNullOrWhiteSpace(projectPath))
        {
            return false;
        }

        var normalizedProjectPath = NormalizeRelativePath(projectPath);
        if (WorkspaceFileExists(workspaceRoot, normalizedProjectPath))
            return false;

        var projectName = Path.GetFileNameWithoutExtension(normalizedProjectPath);
        var outputPath = NormalizeRelativePath(Path.GetDirectoryName(normalizedProjectPath)?.Replace('\\', Path.DirectorySeparatorChar) ?? "");
        var template = InferDeferredProjectTemplate(projectName, normalizedProjectPath);
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(template))
            return false;

        request = BuildFollowUpRequest(
            "create_dotnet_project",
            $"Controlled scaffold continuity: defer solution attach until the missing project `{normalizedProjectPath}` is created.",
            initialRequest);
        request.Arguments["template"] = template;
        request.Arguments["project_name"] = projectName;
        request.Arguments["output_path"] = outputPath;
        request.Arguments["target_framework"] = DotnetScaffoldSurfaceService.ResolveTargetFramework(
            template,
            initialRequest.TryGetArgument("target_framework", out var targetFramework) ? targetFramework : "");
        request.Arguments["template_switches"] = DotnetScaffoldSurfaceService.ResolveDefaultSwitches(
            template,
            initialRequest.TryGetArgument("template_switches", out var templateSwitches) ? templateSwitches : "");
        if (initialRequest.TryGetArgument("solution_path", out var solutionPath) && !string.IsNullOrWhiteSpace(solutionPath))
            request.Arguments["solution_path"] = NormalizeRelativePath(solutionPath);
        ApplyProjectAttachContinuationArguments(
            request,
            initialRequest.TryGetArgument("solution_path", out var attachSolutionPath) ? attachSolutionPath : "",
            normalizedProjectPath,
            "attach_deferred_missing_project",
            "create_dotnet_project",
            $"Deferred solution attach because `{normalizedProjectPath}` did not exist yet; inserted `create_dotnet_project` first.",
            projectExistsAtDecision: false);
        return true;
    }

    private static bool TryBuildMissingTestProjectCreateStep(
        string workspaceRoot,
        ToolRequest initialRequest,
        out ToolRequest request)
    {
        request = new ToolRequest();
        var requestedProjectPath = NormalizeRelativePath(
            initialRequest.TryGetArgument("project", out var projectPath) ? projectPath : "");
        if (!LooksLikeDotnetTestProject(requestedProjectPath) || WorkspaceFileExists(workspaceRoot, requestedProjectPath))
            return false;

        var projectName = Path.GetFileNameWithoutExtension(requestedProjectPath);
        var outputPath = NormalizeRelativePath(Path.GetDirectoryName(requestedProjectPath)?.Replace('\\', Path.DirectorySeparatorChar) ?? "");
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(outputPath))
            return false;

        request = BuildFollowUpRequest(
            "create_dotnet_project",
            $"Controlled check-runner scaffold deferred `dotnet_test` because `{requestedProjectPath}` did not exist yet; inserted `create_dotnet_project` first.",
            initialRequest);
        request.Arguments["template"] = "xunit";
        request.Arguments["project_name"] = projectName;
        request.Arguments["output_path"] = outputPath;
        request.Arguments["target_framework"] = DotnetScaffoldSurfaceService.ResolveTargetFramework("xunit");
        request.Arguments["template_switches"] = DotnetScaffoldSurfaceService.ResolveDefaultSwitches("xunit");
        request.Arguments["test_target_resolution_kind"] = "missing_test_target";
        request.Arguments["test_target_requested_path"] = requestedProjectPath;
        request.Arguments["test_target_resolution_summary"] = $"Deferred `dotnet_test` until the missing test project `{requestedProjectPath}` is created.";
        return true;
    }

    private static bool TryBuildMissingTestProjectAttachStep(
        string workspaceRoot,
        ToolRequest initialRequest,
        out ToolRequest request)
    {
        request = new ToolRequest();
        var requestedProjectPath = NormalizeRelativePath(
            initialRequest.TryGetArgument("project", out var projectPath) ? projectPath : "");
        if (!LooksLikeDotnetTestProject(requestedProjectPath) || !WorkspaceFileExists(workspaceRoot, requestedProjectPath))
            return false;

        if (!TryResolveSolutionPathForTestProject(workspaceRoot, initialRequest, requestedProjectPath, out var solutionPath)
            || string.IsNullOrWhiteSpace(solutionPath)
            || SolutionAlreadyContainsProject(workspaceRoot, solutionPath, requestedProjectPath))
        {
            return false;
        }

        request = BuildFollowUpRequest(
            "add_project_to_solution",
            $"Controlled check-runner scaffold deferred `dotnet_test` until `{requestedProjectPath}` is attached to `{solutionPath}`.",
            initialRequest);
        request.Arguments["solution_path"] = solutionPath;
        request.Arguments["project_path"] = requestedProjectPath;
        request.Arguments["test_target_resolution_kind"] = "attach_test_project_prerequisite";
        request.Arguments["test_target_requested_path"] = requestedProjectPath;
        request.Arguments["test_target_resolution_summary"] = $"Inserted `add_project_to_solution` before `dotnet_test` because the requested test project exists but is not yet attached to `{solutionPath}`.";
        return true;
    }

    private static bool TryBuildResolvedDotnetTestRequest(
        string workspaceRoot,
        ToolRequest initialRequest,
        ToolResult? previousResult,
        string reason,
        out ToolRequest request)
    {
        request = new ToolRequest();
        var resolution = new WorkspaceBuildIndexService().ResolveForTesting(
            workspaceRoot,
            initialRequest.TryGetArgument("project", out var projectPath) ? projectPath : "",
            initialRequest.TryGetArgument("active_target", out var activeTarget) ? activeTarget : "");
        if (!resolution.Success || resolution.Item is null)
            return false;

        request = BuildFollowUpRequest("dotnet_test", reason, initialRequest, previousResult);
        request.Arguments["project"] = resolution.Item.RelativePath;
        request.Arguments["test_target_resolution_kind"] = "resolved_target_present";
        request.Arguments["test_target_requested_path"] = NormalizeRelativePath(
            initialRequest.TryGetArgument("project", out var requestedPath) ? requestedPath : "");
        request.Arguments["test_target_resolution_summary"] = resolution.Message;
        return true;
    }

    private static string InferDeferredProjectTemplate(string projectName, string projectPath)
    {
        return new DotnetScaffoldSurfaceService().InferTemplateFromProjectIdentity(projectName, projectPath, "");
    }

    private static void ApplyProjectAttachContinuationArguments(
        ToolRequest request,
        string solutionPath,
        string projectPath,
        string continuationStatus,
        string insertedStep,
        string summary,
        bool projectExistsAtDecision)
    {
        request.Arguments["project_attach_solution_path"] = NormalizeRelativePath(solutionPath);
        request.Arguments["project_attach_target_project"] = NormalizeRelativePath(projectPath);
        request.Arguments["project_attach_continuation_status"] = continuationStatus ?? "";
        request.Arguments["project_attach_inserted_step"] = insertedStep ?? "";
        request.Arguments["project_attach_continuation_summary"] = summary ?? "";
        request.Arguments["project_attach_project_existed_at_decision"] = projectExistsAtDecision ? "true" : "false";
    }

    private static ToolRequest BuildMakeDirectoryRequest(
        ToolRequest? sourceRequest,
        string path,
        string reason)
    {
        var request = BuildFollowUpRequest("make_dir", reason, sourceRequest);
        request.Arguments["path"] = NormalizeRelativePath(path);
        return request;
    }

    private static bool TryResolveDotnetDomainContractsContext(
        string workspaceRoot,
        ToolRequest request,
        ToolRequest initialRequest,
        out string coreProjectDirectory,
        out string coreProjectName,
        out string buildTarget)
    {
        coreProjectDirectory = "";
        coreProjectName = "";
        buildTarget = "";

        if (TryResolveDotnetSecondaryProjectPathContext(request, out coreProjectDirectory, out coreProjectName))
        {
        }
        else if (TryResolveDotnetDomainContractsPathContext(request, out coreProjectDirectory, out coreProjectName))
        {
        }
        else if (request.TryGetArgument("output_path", out var outputPath) && !string.IsNullOrWhiteSpace(outputPath)
            && request.TryGetArgument("project_name", out var projectName) && !string.IsNullOrWhiteSpace(projectName))
        {
            coreProjectDirectory = NormalizeRelativePath(outputPath);
            coreProjectName = projectName.Trim();
        }
        else if (TryResolveDotnetSecondaryProjectPathContext(initialRequest, out coreProjectDirectory, out coreProjectName))
        {
        }
        else if (TryResolveDotnetDomainContractsPathContext(initialRequest, out coreProjectDirectory, out coreProjectName))
        {
        }
        else if (initialRequest.TryGetArgument("output_path", out var initialOutputPath) && !string.IsNullOrWhiteSpace(initialOutputPath)
            && initialRequest.TryGetArgument("project_name", out var initialProjectName) && !string.IsNullOrWhiteSpace(initialProjectName))
        {
            coreProjectDirectory = NormalizeRelativePath(initialOutputPath);
            coreProjectName = initialProjectName.Trim();
        }

        if (string.IsNullOrWhiteSpace(coreProjectDirectory) || string.IsNullOrWhiteSpace(coreProjectName))
            return false;

        buildTarget = ResolveDotnetBuildTarget(workspaceRoot, initialRequest, request, coreProjectDirectory, coreProjectName);
        return true;
    }

    private static string DeriveAppNameFromCoreProjectName(string coreProjectName)
    {
        if (string.IsNullOrWhiteSpace(coreProjectName))
            return "App";

        return coreProjectName.EndsWith(".Core", StringComparison.OrdinalIgnoreCase)
            ? coreProjectName[..^".Core".Length]
            : coreProjectName.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase)
                ? coreProjectName[..^".Contracts".Length]
                : coreProjectName;
    }

    private static bool WorkspaceDirectoryExists(string workspaceRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var fullPath = Path.Combine(
            workspaceRoot,
            NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(fullPath);
    }

    private static bool TryBuildNextDomainContractsArtifactRequest(
        string workspaceRoot,
        ToolRequest initialRequest,
        string appName,
        string contractsDirectory,
        string modelsDirectory,
        string contractPath,
        string modelPath,
        out ToolRequest request)
    {
        if (!WorkspaceDirectoryExists(workspaceRoot, contractsDirectory))
        {
            request = BuildMakeDirectoryRequest(
                initialRequest,
                contractsDirectory,
                "Controlled domain-contracts scaffold creating the Contracts directory.");
            return true;
        }

        if (!WorkspaceDirectoryExists(workspaceRoot, modelsDirectory))
        {
            request = BuildMakeDirectoryRequest(
                initialRequest,
                modelsDirectory,
                "Controlled domain-contracts scaffold creating the Models directory.");
            return true;
        }

        if (!WorkspaceFileExists(workspaceRoot, contractPath))
        {
            request = BuildNamedWorkspaceWriteRequest(
                "write_file",
                "Controlled domain-contracts scaffold writing the core contract file.",
                initialRequest,
                contractPath,
                BuildDotnetCheckDefinitionCs(appName));
            return true;
        }

        if (!WorkspaceFileExists(workspaceRoot, modelPath))
        {
            request = BuildNamedWorkspaceWriteRequest(
                "write_file",
                "Controlled domain-contracts scaffold writing the core domain model file.",
                initialRequest,
                modelPath,
                BuildDotnetFindingRecordCs(appName));
            return true;
        }

        request = new ToolRequest();
        return false;
    }

    private static string BuildDotnetCheckDefinitionCs(string appName)
    {
        return $$"""
namespace {{appName}}.Core.Contracts;

public sealed class CheckDefinition
{
    public string CheckId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Severity { get; init; } = "info";
}
""";
    }

    private static string BuildDotnetFindingRecordCs(string appName)
    {
        return $$"""
namespace {{appName}}.Core.Models;

public sealed class FindingRecord
{
    public string FindingId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Severity { get; init; } = "info";
    public bool IsResolved { get; set; }
}
""";
    }

    private ToolChainStopReason NormalizeStopReason(
        ToolChainRecord chain,
        ToolChainStopReason requestedStopReason,
        ToolResult? lastResult,
        string workspaceRoot,
        RamDbService ramDbService)
    {
        var autoValidationResult = LoadLatestAutoValidationResult(workspaceRoot, ramDbService);
        if (chain.ChainType == ToolChainType.AutoValidation && autoValidationResult is not null)
        {
            return autoValidationResult.OutcomeClassification switch
            {
                "manual_only" => ToolChainStopReason.ManualOnly,
                "scope_blocked" => ToolChainStopReason.ScopeBlocked,
                "safety_blocked" => ToolChainStopReason.SafetyBlocked,
                _ => requestedStopReason is ToolChainStopReason.Unknown
                    ? ToolChainStopReason.GoalCompleted
                    : requestedStopReason
            };
        }

        if (requestedStopReason != ToolChainStopReason.Unknown)
            return requestedStopReason;

        if (lastResult is not null && !lastResult.Success)
            return MapFailureStopReason(lastResult);

        return chain.Steps.Count == 0
            ? ToolChainStopReason.NoFurtherStepAllowed
            : ToolChainStopReason.GoalCompleted;
    }

    private static ToolChainStopReason MapFailureStopReason(ToolResult result)
    {
        return result.OutcomeType switch
        {
            "manual_only" => ToolChainStopReason.ManualOnly,
            "scope_blocked" or "safety_blocked_scope" => ToolChainStopReason.ScopeBlocked,
            "safety_blocked" or "timed_out" or "output_limit_exceeded" or "execution_gate_blocked" => ToolChainStopReason.SafetyBlocked,
            _ => ToolChainStopReason.ToolFailed
        };
    }

    private string BuildFinalOutcomeSummary(
        ToolChainRecord chain,
        ToolChainStopReason stopReason,
        ToolResult? lastResult,
        string workspaceRoot,
        RamDbService ramDbService)
    {
        var autoValidationResult = LoadLatestAutoValidationResult(workspaceRoot, ramDbService);
        if (chain.ChainType == ToolChainType.AutoValidation && autoValidationResult is not null)
        {
            return $"Auto-validation ended with {autoValidationResult.OutcomeClassification}: {FirstNonEmpty(autoValidationResult.Summary, autoValidationResult.Explanation)}";
        }

        if (lastResult is not null)
        {
            var summary = FirstNonEmpty(lastResult.Summary, lastResult.ErrorMessage);
            if (!string.IsNullOrWhiteSpace(summary))
                return summary;
        }

        if (stopReason == ToolChainStopReason.ChainLimitReached)
            return "The controlled chain stopped after reaching its step limit.";

        return chain.Steps.LastOrDefault()?.ResultSummary
            ?? "The controlled chain finished without a recorded step summary.";
    }

    private string BuildSuggestedNextAction(
        ToolChainRecord chain,
        ToolChainStopReason stopReason,
        string workspaceRoot,
        RamDbService ramDbService)
    {
        var autoValidationResult = LoadLatestAutoValidationResult(workspaceRoot, ramDbService);
        if (autoValidationResult is not null && !string.IsNullOrWhiteSpace(autoValidationResult.SuggestedNextStep))
            return autoValidationResult.SuggestedNextStep;

        return stopReason switch
        {
            ToolChainStopReason.ManualOnly => "Choose a narrower safe manual target before continuing.",
            ToolChainStopReason.ScopeBlocked => "Choose a narrower safe target or inspect build profiles before retrying.",
            ToolChainStopReason.SafetyBlocked => "Inspect the blocked step and retry with a safer narrower action.",
            ToolChainStopReason.ToolFailed => "Inspect the failing step output before retrying the chain.",
            ToolChainStopReason.ChainLimitReached => "Review the recorded steps and continue with a new explicit request if needed.",
            _ => "No further controlled step was allowed by the current chain template."
        };
    }

    private bool NeedsRepairProposalPreparation(string workspaceRoot, RamDbService ramDbService)
    {
        var recentArtifacts = ramDbService.LoadLatestArtifacts(workspaceRoot, 20);
        return !recentArtifacts.Any(_artifactClassificationService.IsRepairArtifact)
            && !recentArtifacts.Any(artifact => string.Equals(artifact.ArtifactType, "patch_draft", StringComparison.OrdinalIgnoreCase));
    }

    private static ToolRequest BuildPreparationRequest(string toolName, string reason, ToolRequest? sourceRequest = null)
    {
        var request = new ToolRequest
        {
            ToolName = toolName,
            Reason = reason
        };

        if (sourceRequest is null)
            return request;

        request.ExecutionSourceType = sourceRequest.ExecutionSourceType;
        request.ExecutionSourceName = sourceRequest.ExecutionSourceName;
        request.IsAutomaticTrigger = sourceRequest.IsAutomaticTrigger;
        request.ExecutionAllowed = sourceRequest.ExecutionAllowed;
        request.ExecutionPolicyMode = sourceRequest.ExecutionPolicyMode;
        request.ExecutionScopeRiskClassification = sourceRequest.ExecutionScopeRiskClassification;
        request.ExecutionBuildFamily = sourceRequest.ExecutionBuildFamily;
        request.TaskboardRunStateId = sourceRequest.TaskboardRunStateId;
        request.TaskboardPlanImportId = sourceRequest.TaskboardPlanImportId;
        request.TaskboardPlanTitle = sourceRequest.TaskboardPlanTitle;
        request.TaskboardBatchId = sourceRequest.TaskboardBatchId;
        request.TaskboardBatchTitle = sourceRequest.TaskboardBatchTitle;
        request.TaskboardWorkItemId = sourceRequest.TaskboardWorkItemId;
        request.TaskboardWorkItemTitle = sourceRequest.TaskboardWorkItemTitle;

        foreach (var key in new[]
                 {
                     "path",
                     "scope",
                     "project",
                     "active_target",
                     "solution_path",
                     "project_path",
                     "reference_path",
                     "output_path",
                     "project_name",
                     "template",
                     "continuation_source_project_path",
                     "continuation_reference_project_path"
                 })
        {
            if (sourceRequest.TryGetArgument(key, out var value))
                request.Arguments[key] = value;
        }

        return request;
    }

    private static ToolRequest BuildFollowUpRequest(
        string toolName,
        string reason,
        ToolRequest? sourceRequest,
        ToolResult? previousResult = null)
    {
        var request = BuildPreparationRequest(toolName, reason, sourceRequest);
        return request;
    }

    private static ToolRequest BuildRepairFollowUpRequest(
        string toolName,
        string reason,
        ToolRequest? sourceRequest,
        ToolResult? previousResult)
    {
        var request = BuildFollowUpRequest(toolName, reason, sourceRequest, previousResult);
        var preferredPath = ExtractRepairTargetPath(previousResult);
        if (!string.IsNullOrWhiteSpace(preferredPath))
            request.Arguments["path"] = preferredPath;

        return request;
    }

    private static ToolRequest BuildNamedWorkspaceWriteRequest(
        string toolName,
        string reason,
        ToolRequest? sourceRequest,
        string path,
        string content,
        IReadOnlyDictionary<string, string>? extraArguments = null)
    {
        var request = BuildFollowUpRequest(toolName, reason, sourceRequest);
        request.Arguments["path"] = NormalizeRelativePath(path);
        request.Arguments["content"] = content ?? "";
        if (extraArguments is not null)
        {
            foreach (var entry in extraArguments)
                request.Arguments[entry.Key] = entry.Value;
        }
        return request;
    }

    private static Dictionary<string, string> BuildCodeIntentArguments(
        string role,
        string pattern,
        string projectName,
        string namespaceName,
        string depth,
        string validationTarget,
        params string[] followThrough)
    {
        var normalizedDepth = NormalizeGenerationDepth(depth);
        var normalizedFollowThrough = followThrough
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var supportingSurfaces = ResolveSupportingSurfaces(pattern, normalizedFollowThrough);
        var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["role"] = role,
            ["file_role"] = role,
            ["pattern"] = pattern,
            ["project"] = projectName,
            ["target_project"] = projectName,
            ["namespace"] = namespaceName,
            ["depth"] = normalizedDepth,
            ["implementation_depth"] = normalizedDepth,
            ["followthrough_mode"] = normalizedFollowThrough.Count == 0 ? "single_file" : "planned_supporting_surfaces",
            ["completion_contract"] = BuildCompletionContract(pattern, normalizedDepth)
        };

        if (!string.IsNullOrWhiteSpace(validationTarget))
            arguments["validation"] = NormalizeRelativePath(validationTarget);

        if (normalizedFollowThrough.Count > 0)
            arguments["followthrough"] = string.Join(",", normalizedFollowThrough);
        if (supportingSurfaces.Count > 0)
            arguments["supporting_surfaces"] = string.Join(",", supportingSurfaces);

        return arguments;
    }

    private static string NormalizeGenerationDepth(string value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "" => "standard",
            "structural" => "scaffold",
            "behavioral" => "standard",
            "integrated" => "standard",
            _ => (value ?? "").Trim().ToLowerInvariant()
        };
    }

    private static string BuildCompletionContract(string pattern, string depth)
    {
        return pattern switch
        {
            "repository" => depth == "strong"
                ? "interface,implementation,constructor_dependencies,helper_methods"
                : "interface,implementation",
            "viewmodel" => depth == "strong"
                ? "binding_surface,commands,property_change_notifications"
                : "binding_surface,property_change_notifications",
            "test_harness" => "real_subject_linkage,deterministic_assertions",
            "interface" => "contract_members",
            _ => "role_members"
        };
    }

    private static List<string> ResolveSupportingSurfaces(string pattern, IReadOnlyList<string> followThrough)
    {
        var values = new List<string>();
        if (string.Equals(pattern, "repository", StringComparison.OrdinalIgnoreCase))
            values.Add("interface:IRepositoryContract.cs");
        if (string.Equals(pattern, "viewmodel", StringComparison.OrdinalIgnoreCase))
            values.Add("helper:DelegateCommand.cs");
        if (followThrough.Contains("verification_required", StringComparer.OrdinalIgnoreCase))
            values.Add("verification:post_write");
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, string> MergeArguments(params IReadOnlyDictionary<string, string>[] dictionaries)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dictionary in dictionaries)
        {
            if (dictionary is null)
                continue;

            foreach (var entry in dictionary)
                merged[entry.Key] = entry.Value;
        }

        return merged;
    }

    private static bool TryResolveDotnetTestTarget(string workspaceRoot, ToolRequest initialRequest, out string targetPath)
    {
        var resolution = new WorkspaceBuildIndexService().ResolveForTesting(
            workspaceRoot,
            initialRequest.TryGetArgument("project", out var project) ? project : "",
            initialRequest.TryGetArgument("active_target", out var activeTarget) ? activeTarget : "");
        targetPath = resolution.Success && resolution.Item is not null
            ? resolution.Item.RelativePath
            : "";
        return !string.IsNullOrWhiteSpace(targetPath);
    }

    private static bool TryResolveSolutionPathForTestProject(
        string workspaceRoot,
        ToolRequest initialRequest,
        string projectPath,
        out string solutionPath)
    {
        solutionPath = NormalizeRelativePath(
            initialRequest.TryGetArgument("solution_path", out var explicitSolutionPath) ? explicitSolutionPath : "");
        if (!string.IsNullOrWhiteSpace(solutionPath) && WorkspaceFileExists(workspaceRoot, solutionPath))
            return true;

        var solutionCandidates = Directory.Exists(workspaceRoot)
            ? Directory.EnumerateFiles(workspaceRoot, "*.sln", SearchOption.AllDirectories)
                .Select(path => NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        if (solutionCandidates.Count != 1)
            return false;

        solutionPath = solutionCandidates[0];
        return true;
    }

    private static bool SolutionAlreadyContainsProject(string workspaceRoot, string solutionPath, string projectPath)
    {
        if (!WorkspaceFileExists(workspaceRoot, solutionPath))
            return false;

        var solutionFullPath = Path.Combine(workspaceRoot, NormalizeRelativePath(solutionPath).Replace('/', Path.DirectorySeparatorChar));
        var normalizedProjectPath = NormalizeRelativePath(projectPath).ToLowerInvariant();
        var solutionDirectory = Path.GetDirectoryName(solutionFullPath) ?? workspaceRoot;

        foreach (var line in File.ReadLines(solutionFullPath))
        {
            if (!line.Contains(".csproj", StringComparison.OrdinalIgnoreCase))
                continue;

            var normalizedLine = line.Replace('\\', '/');
            if (normalizedLine.Contains(normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
                return true;

            var quotedSegments = normalizedLine.Split('"');
            foreach (var segment in quotedSegments)
            {
                if (!segment.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    continue;

                var combined = Path.GetFullPath(Path.Combine(solutionDirectory, segment.Replace('/', Path.DirectorySeparatorChar)));
                var relativeCandidate = NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, combined));
                if (string.Equals(relativeCandidate, normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static bool TryResolveDotnetStateContext(ToolRequest request, out string stateDirectory, out string projectName)
    {
        stateDirectory = "";
        projectName = "";
        if (!request.TryGetArgument("path", out var path)
            || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = NormalizeRelativePath(path);
        var filesystemPath = normalizedPath.Replace('/', Path.DirectorySeparatorChar);
        var stateDirectoryPath = normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(filesystemPath) ?? ""
            : filesystemPath;
        if (string.IsNullOrWhiteSpace(stateDirectoryPath))
            return false;

        stateDirectory = NormalizeRelativePath(stateDirectoryPath);
        var projectDirectory = Path.GetDirectoryName(stateDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
        projectName = Path.GetFileName(projectDirectory);
        return !string.IsNullOrWhiteSpace(stateDirectory) && !string.IsNullOrWhiteSpace(projectName);
    }

    private static bool WorkspaceRelativeFileExists(string workspaceRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(relativePath))
            return false;

        var fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(fullPath);
    }

    private static bool TryResolveDotnetStorageContext(ToolRequest request, out string storageDirectory, out string storageProjectName)
    {
        storageDirectory = "";
        storageProjectName = "";
        if (!request.TryGetArgument("path", out var path)
            || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = NormalizeRelativePath(path);
        var filesystemPath = normalizedPath.Replace('/', Path.DirectorySeparatorChar);
        var storageDirectoryPath = normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(filesystemPath) ?? ""
            : filesystemPath;
        if (string.IsNullOrWhiteSpace(storageDirectoryPath))
            return false;

        storageDirectory = NormalizeRelativePath(storageDirectoryPath);
        var trimmedDirectory = storageDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directoryName = Path.GetFileName(trimmedDirectory);
        if (string.IsNullOrWhiteSpace(directoryName))
            return false;

        if (directoryName.EndsWith(".Storage", StringComparison.OrdinalIgnoreCase))
        {
            storageProjectName = directoryName;
        }
        else if (string.Equals(directoryName, "Storage", StringComparison.OrdinalIgnoreCase))
        {
            var parentDirectory = Path.GetFileName(Path.GetDirectoryName(trimmedDirectory) ?? "");
            storageProjectName = string.IsNullOrWhiteSpace(parentDirectory)
                ? "App.Storage"
                : $"{parentDirectory}.Storage";
        }
        else
        {
            storageProjectName = directoryName;
        }

        return !string.IsNullOrWhiteSpace(storageDirectory) && !string.IsNullOrWhiteSpace(storageProjectName);
    }

    private static bool TryResolveDotnetRepositoryContext(
        string workspaceRoot,
        ToolRequest request,
        ToolRequest initialRequest,
        out string repositoryDirectory,
        out string storageProjectName,
        out string buildTarget)
    {
        repositoryDirectory = "";
        storageProjectName = "";
        buildTarget = "";

        if (!TryResolveDotnetStorageContext(request, out repositoryDirectory, out storageProjectName)
            && !TryResolveDotnetStorageContext(initialRequest, out repositoryDirectory, out storageProjectName)
            && !TryResolveDotnetSecondaryProjectPathContext(request, out repositoryDirectory, out storageProjectName)
            && !TryResolveDotnetSecondaryProjectPathContext(initialRequest, out repositoryDirectory, out storageProjectName)
            && !TryResolveDotnetProjectPathContext(request, out repositoryDirectory, out storageProjectName)
            && !TryResolveDotnetProjectPathContext(initialRequest, out repositoryDirectory, out storageProjectName)
            && !TryResolveDotnetProjectOutputContext(request, out repositoryDirectory, out storageProjectName)
            && !TryResolveDotnetProjectOutputContext(initialRequest, out repositoryDirectory, out storageProjectName))
        {
            return false;
        }

        buildTarget = ResolveDotnetBuildTarget(workspaceRoot, initialRequest, request, repositoryDirectory, storageProjectName);
        return !string.IsNullOrWhiteSpace(repositoryDirectory) && !string.IsNullOrWhiteSpace(storageProjectName);
    }

    private static bool TryResolveDotnetProjectPathContext(ToolRequest request, out string repositoryDirectory, out string projectName)
    {
        repositoryDirectory = "";
        projectName = "";

        if (!request.TryGetArgument("project_path", out var projectPath)
            || string.IsNullOrWhiteSpace(projectPath))
        {
            return false;
        }

        var normalizedProjectPath = NormalizeRelativePath(projectPath);
        repositoryDirectory = NormalizeRelativePath(Path.GetDirectoryName(normalizedProjectPath)?.Replace('\\', Path.DirectorySeparatorChar) ?? "");
        projectName = Path.GetFileNameWithoutExtension(normalizedProjectPath);
        return !string.IsNullOrWhiteSpace(repositoryDirectory) && !string.IsNullOrWhiteSpace(projectName);
    }

    private static bool TryResolveDotnetSecondaryProjectPathContext(ToolRequest request, out string projectDirectory, out string projectName)
    {
        projectDirectory = "";
        projectName = "";

        foreach (var argumentName in new[] { "reference_path", "project_attach_target_project", "project_path" })
        {
            if (!request.TryGetArgument(argumentName, out var projectPath)
                || string.IsNullOrWhiteSpace(projectPath))
            {
                continue;
            }

            var normalizedProjectPath = NormalizeRelativePath(projectPath);
            projectDirectory = NormalizeRelativePath(Path.GetDirectoryName(normalizedProjectPath)?.Replace('\\', Path.DirectorySeparatorChar) ?? "");
            projectName = Path.GetFileNameWithoutExtension(normalizedProjectPath);
            if (!string.IsNullOrWhiteSpace(projectDirectory) && !string.IsNullOrWhiteSpace(projectName))
                return true;
        }

        return false;
    }

    private static bool TryResolveDotnetDomainContractsPathContext(ToolRequest request, out string coreProjectDirectory, out string coreProjectName)
    {
        coreProjectDirectory = "";
        coreProjectName = "";

        if (!request.TryGetArgument("path", out var path)
            || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = NormalizeRelativePath(path);
        var filesystemPath = normalizedPath.Replace('/', Path.DirectorySeparatorChar);
        var candidateDirectory = normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(filesystemPath) ?? ""
            : filesystemPath;
        if (string.IsNullOrWhiteSpace(candidateDirectory))
            return false;

        var trimmedDirectory = candidateDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leafName = Path.GetFileName(trimmedDirectory);
        if (string.Equals(leafName, "Contracts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(leafName, "Models", StringComparison.OrdinalIgnoreCase))
        {
            candidateDirectory = Path.GetDirectoryName(trimmedDirectory) ?? "";
        }

        coreProjectDirectory = NormalizeRelativePath(candidateDirectory);
        coreProjectName = Path.GetFileName(candidateDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return !string.IsNullOrWhiteSpace(coreProjectDirectory) && !string.IsNullOrWhiteSpace(coreProjectName);
    }

    private static bool TryResolveDotnetProjectOutputContext(ToolRequest request, out string repositoryDirectory, out string projectName)
    {
        repositoryDirectory = "";
        projectName = "";

        if (!request.TryGetArgument("output_path", out var outputPath)
            || string.IsNullOrWhiteSpace(outputPath)
            || !request.TryGetArgument("project_name", out var resolvedProjectName)
            || string.IsNullOrWhiteSpace(resolvedProjectName))
        {
            return false;
        }

        repositoryDirectory = NormalizeRelativePath(outputPath);
        projectName = resolvedProjectName.Trim();
        return !string.IsNullOrWhiteSpace(repositoryDirectory) && !string.IsNullOrWhiteSpace(projectName);
    }

    private static string ResolveDotnetBuildTarget(
        string workspaceRoot,
        ToolRequest initialRequest,
        ToolRequest request,
        string repositoryDirectory,
        string storageProjectName)
    {
        foreach (var candidate in new[]
                 {
                     request.TryGetArgument("project", out var requestProject) ? requestProject : "",
                     initialRequest.TryGetArgument("project", out var initialProject) ? initialProject : "",
                     request.TryGetArgument("solution_path", out var requestSolutionPath) ? requestSolutionPath : "",
                     initialRequest.TryGetArgument("solution_path", out var initialSolutionPath) ? initialSolutionPath : "",
                     request.TryGetArgument("reference_path", out var requestReferencePath) ? requestReferencePath : "",
                     initialRequest.TryGetArgument("reference_path", out var initialReferencePath) ? initialReferencePath : "",
                     request.TryGetArgument("project_attach_target_project", out var requestAttachedProjectPath) ? requestAttachedProjectPath : "",
                     initialRequest.TryGetArgument("project_attach_target_project", out var initialAttachedProjectPath) ? initialAttachedProjectPath : "",
                     request.TryGetArgument("project_path", out var requestProjectPath) ? requestProjectPath : "",
                     initialRequest.TryGetArgument("project_path", out var initialProjectPath) ? initialProjectPath : ""
                 })
        {
            var normalized = NormalizeRelativePath(candidate);
            if (normalized.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }
        }

        var solutionCandidates = Directory.Exists(workspaceRoot)
            ? Directory.EnumerateFiles(workspaceRoot, "*.sln", SearchOption.AllDirectories)
                .Select(path => NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        if (solutionCandidates.Count == 1)
            return solutionCandidates[0];

        var inferredProjectPath = CombineRelativePath(repositoryDirectory, $"{storageProjectName}.csproj");
        return WorkspaceFileExists(workspaceRoot, inferredProjectPath) ? inferredProjectPath : "";
    }

    private DotnetProjectReferenceDecision TryResolveDeterministicReferenceDecision(string workspaceRoot, string projectPath)
    {
        if (!Directory.Exists(workspaceRoot))
            return new DotnetProjectReferenceDecision();

        var normalizedProjectPath = NormalizeRelativePath(projectPath);
        var fileIdentityService = new FileIdentityService();
        var candidates = Directory.EnumerateFiles(workspaceRoot, "*.csproj", SearchOption.AllDirectories)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, path)))
            .Where(path => !string.Equals(path, normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
            .Where(path => !string.Equals(fileIdentityService.Identify(path).Role, "tests", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var decisions = candidates
            .Select(candidate => _dotnetProjectReferencePolicyService.Evaluate(workspaceRoot, normalizedProjectPath, candidate))
            .Where(decision => decision.ShouldExecute)
            .GroupBy(decision => $"{decision.EffectiveProjectPath}|{decision.EffectiveReferencePath}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(decision => decision.RulePriority)
                .ThenBy(decision => decision.EffectiveProjectPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(decision => decision.EffectiveReferencePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(decision => decision.RulePriority)
            .ThenBy(decision => decision.EffectiveProjectPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(decision => decision.EffectiveReferencePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (decisions.Count == 0)
            return new DotnetProjectReferenceDecision();

        var best = decisions[0];
        var hasTie = decisions.Count > 1 && decisions[1].RulePriority == best.RulePriority;
        return hasTie ? new DotnetProjectReferenceDecision() : best;
    }

    private DotnetProjectReferenceDecision? TryResolveContinuationReferenceDecision(string workspaceRoot, ToolRequest request)
    {
        if (!request.TryGetArgument("continuation_source_project_path", out var sourceProjectPath)
            || !request.TryGetArgument("continuation_reference_project_path", out var referenceProjectPath))
        {
            return null;
        }

        var normalizedSourcePath = NormalizeRelativePath(sourceProjectPath);
        var normalizedReferencePath = NormalizeRelativePath(referenceProjectPath);
        if (string.IsNullOrWhiteSpace(normalizedSourcePath)
            || string.IsNullOrWhiteSpace(normalizedReferencePath)
            || string.Equals(normalizedSourcePath, normalizedReferencePath, StringComparison.OrdinalIgnoreCase)
            || !WorkspaceFileExists(workspaceRoot, normalizedSourcePath)
            || !WorkspaceFileExists(workspaceRoot, normalizedReferencePath))
        {
            return null;
        }

        var decision = _dotnetProjectReferencePolicyService.Evaluate(
            workspaceRoot,
            normalizedSourcePath,
            normalizedReferencePath);
        return decision.ShouldExecute ? decision : null;
    }

    private static bool WorkspaceFileExists(string workspaceRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(relativePath))
            return false;

        var fullPath = Path.Combine(
            workspaceRoot,
            NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(fullPath);
    }

    private static bool LooksLikeDotnetTestProject(string? relativePath)
    {
        return !string.IsNullOrWhiteSpace(relativePath)
            && relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            && (relativePath.Contains("Tests", StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains("Test", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRelativePath(string? value)
    {
        return (value ?? "").Replace('\\', '/').Trim();
    }

    private static string CombineRelativePath(string directory, string fileName)
    {
        return NormalizeRelativePath(Path.Combine(
            (directory ?? "").Replace('/', Path.DirectorySeparatorChar),
            fileName ?? ""));
    }

    private static string ExtractRepairTargetPath(ToolResult? result)
    {
        if (result is null || string.IsNullOrWhiteSpace(result.StructuredDataJson))
            return "";

        try
        {
            using var document = JsonDocument.Parse(result.StructuredDataJson);
            if (TryExtractTargetPath(document.RootElement, "draft", out var draftTarget))
                return draftTarget;

            if (TryExtractTargetPath(document.RootElement, "proposal", out var proposalTarget))
                return proposalTarget;
        }
        catch
        {
            return "";
        }

        return "";
    }

    private static bool TryExtractTargetPath(JsonElement root, string propertyName, out string targetPath)
    {
        targetPath = "";
        if (!root.TryGetProperty(propertyName, out var section))
            return false;

        if (!section.TryGetProperty("TargetFilePath", out var pathElement))
            return false;

        targetPath = pathElement.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(targetPath);
    }

    private static ArtifactRecord SaveArtifact(
        RamDbService ramDbService,
        string workspaceRoot,
        string artifactType,
        string title,
        string relativePath,
        string content,
        string summary)
    {
        var existing = ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existing ?? new ArtifactRecord();
        artifact.IntentTitle = "";
        artifact.ArtifactType = artifactType;
        artifact.Title = title;
        artifact.RelativePath = relativePath;
        artifact.Content = content;
        artifact.Summary = summary;

        if (existing is null)
            return ramDbService.SaveArtifact(workspaceRoot, artifact);

        ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private static string BuildGoal(string userPrompt, string toolName)
    {
        if (!string.IsNullOrWhiteSpace(userPrompt))
            return userPrompt.Trim();

        return toolName switch
        {
            "write_file" => "Update a workspace file safely.",
            "replace_in_file" => "Apply a deterministic file edit.",
            "plan_repair" => "Plan a targeted repair.",
            "preview_patch_draft" => "Preview a repair patch draft.",
            "detect_build_system" => "Inspect workspace build configuration.",
            _ => $"Run {toolName} under controlled chaining."
        };
    }

    private static string SummarizeArguments(ToolRequest request)
    {
        if (request.Arguments.Count == 0)
            return "(none)";

        return string.Join(
            "; ",
            request.Arguments
                .Take(4)
                .Select(pair => $"{pair.Key}={TrimValue(pair.Value)}"));
    }

    private static string NormalizeToolName(string toolName)
    {
        return (toolName ?? "").Trim().ToLowerInvariant();
    }

    private static string TrimValue(string value)
    {
        value ??= "";
        return value.Length <= 80 ? value : value[..80] + "...";
    }

    private static AutoValidationResultRecord? LoadLatestAutoValidationResult(string workspaceRoot, RamDbService ramDbService)
    {
        var artifact = ramDbService.LoadLatestArtifactByType(workspaceRoot, "auto_validation_result");
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.Content))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AutoValidationResultRecord>(artifact.Content);
        }
        catch
        {
            return null;
        }
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

    private static string BuildSuggestionPromptSummary(ActionableSuggestionRecord suggestion)
    {
        var prompt = FirstNonEmpty(suggestion.PromptText, suggestion.Title);
        if (string.IsNullOrWhiteSpace(prompt))
            return "";

        return suggestion.Readiness switch
        {
            SuggestionReadiness.ReadyNow => prompt,
            SuggestionReadiness.NeedsPrerequisite => $"{prompt} (after prerequisite)",
            SuggestionReadiness.ManualOnly => $"{prompt} (manual-only)",
            SuggestionReadiness.Blocked => $"{prompt} (currently blocked)",
            _ => prompt
        };
    }

    private static int ReadinessOrder(SuggestionReadiness readiness)
    {
        return readiness switch
        {
            SuggestionReadiness.ReadyNow => 0,
            SuggestionReadiness.NeedsPrerequisite => 1,
            SuggestionReadiness.ManualOnly => 2,
            SuggestionReadiness.Blocked => 3,
            _ => 4
        };
    }

    private static string BuildNavigationItemCs(string projectName)
    {
        return $$"""
namespace {{projectName}}.State;

public sealed class NavigationItem
{
    public string Title { get; init; } = "";
    public string RouteKey { get; init; } = "";
}
""";
    }

    private static string BuildAppStateCs(string projectName)
    {
        return $$"""
using System.Collections.Generic;

namespace {{projectName}}.State;

public sealed class AppState
{
    public string CurrentRoute { get; set; } = "dashboard";
    public string StatusMessage { get; set; } = "Baseline verification succeeded and bounded follow-up feature work is ready.";
    public string LastBuildResult { get; set; } = "Build verification green";

    public List<NavigationItem> NavigationItems { get; } = [.. ShellNavigationRegistry.CreateDefault()];
}
""";
    }

    private static string BuildShellNavigationRegistryCs(string projectName)
    {
        return $$"""
using System.Collections.Generic;

namespace {{projectName}}.State;

public static class ShellNavigationRegistry
{
    public static IReadOnlyList<NavigationItem> CreateDefault()
    {
        return
        [
            new() { Title = "Dashboard", RouteKey = "dashboard" },
            new() { Title = "Findings", RouteKey = "findings" },
            new() { Title = "History", RouteKey = "history" },
            new() { Title = "Settings", RouteKey = "settings" }
        ];
    }
}
""";
    }

    private static string BuildShellViewModelCs(string projectName)
    {
        return $$"""
using System.Collections.Generic;

namespace {{projectName}}.State;

public sealed class ShellViewModel
{
    public AppState State { get; } = new();
    public string WindowTitle { get; } = "Windows Security App Test Build";
    public string CurrentStatusSummary => $"{State.LastBuildResult}. Active route: {State.CurrentRoute}. {State.StatusMessage}";
    public IReadOnlyList<string> DashboardHighlights { get; } =
    [
        "Baseline verification is green for the imported desktop workspace.",
        "Navigation, storage, and repository layers are wired for bounded follow-up work.",
        "The shell exposes state-backed sections with usable bindings and verification-ready navigation."
    ];

    public IReadOnlyList<FindingSummary> RecentFindings { get; } =
    [
        new() { Title = "Antivirus signature check", Severity = "Low", Status = "Healthy" },
        new() { Title = "Firewall profile audit", Severity = "Medium", Status = "Needs review" },
        new() { Title = "History pipeline backfill", Severity = "Low", Status = "Queued" }
    ];

    public IReadOnlyList<string> HistoryEntries { get; } =
    [
        "Imported baseline and completed bounded build verification.",
        "Navigation/state shell wiring applied successfully.",
        "Storage and repository boundary updates completed without reopening prior defects."
    ];

    public IReadOnlyList<SettingRow> SettingsItems { get; } =
    [
        new() { Label = "Data refresh cadence", Value = "Every verification pass" },
        new() { Label = "Notification mode", Value = "Deterministic shell banner only" },
        new() { Label = "Storage backend", Value = "SQLite boundary with snapshot repository" }
    ];

    public void Navigate(string routeKey)
    {
        if (!string.IsNullOrWhiteSpace(routeKey))
            State.CurrentRoute = routeKey;
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
""";
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
}
