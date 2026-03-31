using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class BuilderWorkItemDecompositionService
{
    public const string ResolverContractVersion = "builder_work_item_decomposition.v4";
    private static readonly DotnetScaffoldSurfaceService DotnetScaffoldSurfaceService = new();

    private readonly BuildProfileResolutionService _buildProfileResolutionService;
    private readonly TaskboardBuildFailureRecoveryService _buildFailureRecoveryService = new();
    private readonly TaskboardStateSatisfactionService _stateSatisfactionService = new();
    private readonly DeterministicPhraseFamilyFallbackService _phraseFamilyFallbackService;
    private readonly PhraseFamilyTieBreakService _phraseFamilyTieBreakService;
    private readonly PhraseFamilyAgentService? _phraseFamilyAgentService;
    private readonly TemplateSelectorAgentService? _templateSelectorAgentService;
    private readonly TaskboardMaintenanceBaselineService _maintenanceBaselineService = new();
    private readonly WorkspaceBuildIndexService _workspaceBuildIndexService = new();

    private static readonly Regex ExplicitProjectToSolutionPattern = new(
        @"\b(?:add|attach|include|wire|register)\s+(?:app\s+|test\s+)?project\s+(?<project>[A-Za-z0-9_.-]+)\s+(?:to|into)\s+solution\s+(?<solution>[A-Za-z0-9_.-]+(?:\.sln)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitReferenceFromToPattern = new(
        @"\b(?:add\s+(?:dotnet\s+)?(?:project\s+)?)?reference\s+from\s+(?<source>[A-Za-z0-9_.-]+)\s+to\s+(?<target>[A-Za-z0-9_.-]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitReferencePairPattern = new(
        @"\badd\s+dotnet\s+project\s+reference\s+(?<source>[A-Za-z0-9_.-]+)\s+(?<target>[A-Za-z0-9_.-]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly List<string> AllowedPhraseFamilies =
    [
        "build_first_ui_shell",
        "ui_shell_sections",
        "solution_scaffold",
        "project_scaffold",
        "native_project_bootstrap",
        "cmake_bootstrap",
        "core_domain_models_contracts",
        "repository_scaffold",
        "add_navigation_app_state",
        "setup_storage_layer",
        "add_settings_page",
        "add_history_log_view",
        "wire_dashboard",
        "maintenance_context",
        "check_runner",
        "findings_pipeline",
        "build_verify"
    ];

    public BuilderWorkItemDecompositionService(
        BuildProfileResolutionService? buildProfileResolutionService = null,
        PhraseFamilyAgentService? phraseFamilyAgentService = null,
        TemplateSelectorAgentService? templateSelectorAgentService = null,
        DeterministicPhraseFamilyFallbackService? phraseFamilyFallbackService = null,
        PhraseFamilyTieBreakService? phraseFamilyTieBreakService = null)
    {
        _buildProfileResolutionService = buildProfileResolutionService ?? new BuildProfileResolutionService();
        _phraseFamilyAgentService = phraseFamilyAgentService;
        _templateSelectorAgentService = templateSelectorAgentService;
        _phraseFamilyFallbackService = phraseFamilyFallbackService ?? new DeterministicPhraseFamilyFallbackService();
        _phraseFamilyTieBreakService = phraseFamilyTieBreakService ?? new PhraseFamilyTieBreakService();
    }

    public async Task<TaskboardWorkItemDecompositionRecord> DecomposeAsync(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardDocument activeDocument,
        TaskboardBatch batch,
        TaskboardRunWorkItem workItem,
        RamDbService? ramDbService = null,
        AppSettings? settings = null,
        string endpoint = "",
        string selectedModel = "",
        CancellationToken cancellationToken = default,
        TaskboardPlanRunStateRecord? activeRunState = null)
    {
        var executionState = ramDbService?.LoadExecutionState(workspaceRoot);
        var maintenanceBaseline = _maintenanceBaselineService.Resolve(workspaceRoot, activeDocument, executionState);
        var headingPolicy = TaskboardStructuralHeadingService.Classify(workItem.Title);
        var supportActionablePhraseFamily = TaskboardStructuralHeadingService.ResolveActionableFollowupPhraseFamily(
            workItem.Title,
            workItem.Summary,
            workItem.PromptText);
        if (TaskboardStructuralHeadingService.IsNonActionableHeading(headingPolicy))
        {
            if (string.IsNullOrWhiteSpace(supportActionablePhraseFamily))
            {
                var supportBuildProfile = await _buildProfileResolutionService.ResolveAsync(
                    workspaceRoot,
                    activeDocument,
                    batch,
                    workItem,
                    ramDbService,
                    settings,
                    endpoint,
                    selectedModel,
                    cancellationToken,
                    activeRunState);
                var supportPhraseFamilyResolution = BuildStructuralSupportPhraseFamilyResolution(
                    activeImport,
                    batch,
                    workItem,
                    headingPolicy,
                    maintenanceBaseline.IsMaintenanceMode);

                return BuildCoveredSupport(
                    activeImport,
                    batch,
                    workItem,
                    supportPhraseFamilyResolution,
                    supportBuildProfile,
                    TaskboardStructuralHeadingService.BuildSupportCoverageReason(headingPolicy));
            }
        }

        var phraseFamilyResolution = !string.IsNullOrWhiteSpace(supportActionablePhraseFamily)
            ? BuildSupportActionablePhraseFamilyResolution(
                activeImport,
                batch,
                workItem,
                headingPolicy,
                supportActionablePhraseFamily)
            : await ResolvePhraseFamilyAsync(
                workspaceRoot,
                activeImport,
                activeDocument,
                batch,
                workItem,
                settings,
                endpoint,
                selectedModel,
                cancellationToken);
        if (phraseFamilyResolution.IsBlocked)
        {
            return BuildBlocked(
                activeImport,
                batch,
                workItem,
                phraseFamilyResolution,
                new TaskboardBuildProfileResolutionRecord
                {
                    ResolutionId = Guid.NewGuid().ToString("N"),
                    Status = TaskboardBuildProfileResolutionStatus.Unknown,
                    ResolutionReason = phraseFamilyResolution.BlockReason
                },
                phraseFamilyResolution.BlockReason);
        }

        if (!phraseFamilyResolution.ShouldDecompose)
        {
            return new TaskboardWorkItemDecompositionRecord
            {
                DecompositionId = Guid.NewGuid().ToString("N"),
                WorkspaceRoot = workspaceRoot,
                PlanImportId = activeImport.ImportId,
                BatchId = batch.BatchId,
                OriginalWorkItemId = workItem.WorkItemId,
                OriginalTitle = workItem.Title,
                PhraseFamily = phraseFamilyResolution.PhraseFamily,
                PhraseFamilyConfidence = phraseFamilyResolution.Confidence,
                PhraseFamilyTraceId = phraseFamilyResolution.TraceId,
                PhraseFamilySource = phraseFamilyResolution.Source,
                PhraseFamilyResolutionSummary = phraseFamilyResolution.ResolutionSummary,
                PhraseFamilyCandidates = [.. phraseFamilyResolution.Candidates],
                PhraseFamilyDeterministicCandidate = phraseFamilyResolution.DeterministicCandidate,
                PhraseFamilyAdvisoryCandidate = phraseFamilyResolution.AdvisoryCandidate,
                PhraseFamilyBlockerCode = phraseFamilyResolution.BlockerCode,
                PhraseFamilyTieBreakRuleId = phraseFamilyResolution.TieBreakRuleId,
                PhraseFamilyTieBreakSummary = phraseFamilyResolution.TieBreakSummary,
                Disposition = TaskboardWorkItemDecompositionDisposition.NotApplicable,
                PhraseFamilyResolution = phraseFamilyResolution.Record
            };
        }

        var buildProfile = await _buildProfileResolutionService.ResolveAsync(
            workspaceRoot,
            activeDocument,
            batch,
            workItem,
            ramDbService,
            settings,
            endpoint,
            selectedModel,
            cancellationToken,
            activeRunState);

        if (buildProfile.Status != TaskboardBuildProfileResolutionStatus.Resolved)
        {
            return BuildBlocked(
                activeImport,
                batch,
                workItem,
                phraseFamilyResolution,
                buildProfile,
                FirstNonEmpty(
                    buildProfile.ResolutionReason,
                    $"Taskboard auto-run paused: `{workItem.Title}` needs stack resolution before RAM can decompose it into bounded work."));
        }

        if (TaskboardStructuralHeadingService.IsNonActionableHeading(TaskboardStructuralHeadingService.Classify(workItem.Title))
            && string.IsNullOrWhiteSpace(supportActionablePhraseFamily))
        {
            return BuildCoveredSupport(
                activeImport,
                batch,
                workItem,
                phraseFamilyResolution,
                buildProfile,
                TaskboardStructuralHeadingService.BuildSupportCoverageReason(workItem.Title));
        }

        var templateSelection = await ResolveTemplateSelectionAsync(
            workspaceRoot,
            workItem,
            phraseFamilyResolution.PhraseFamily,
            buildProfile,
            settings,
            endpoint,
            selectedModel,
            cancellationToken);
        if (!templateSelection.IsResolved)
        {
            return BuildBlocked(
                activeImport,
                batch,
                workItem,
                phraseFamilyResolution,
                buildProfile,
                templateSelection.Reason,
                templateSelection.CandidateTemplateIds,
                templateSelection.TraceId,
                templateSelection.Reason);
        }

        return buildProfile.StackFamily switch
        {
            TaskboardStackFamily.DotnetDesktop => BuildDotnetDecomposition(workspaceRoot, activeImport, activeDocument, batch, workItem, headingPolicy, supportActionablePhraseFamily, phraseFamilyResolution, templateSelection, buildProfile, ramDbService, activeRunState),
            TaskboardStackFamily.NativeCppDesktop => BuildNativeCppDecomposition(workspaceRoot, activeImport, activeDocument, batch, workItem, phraseFamilyResolution, templateSelection, buildProfile),
            _ => BuildBlocked(
                activeImport,
                batch,
                workItem,
                phraseFamilyResolution,
                buildProfile,
                $"Taskboard auto-run paused: no decomposition template exists yet for phrase family `{phraseFamilyResolution.PhraseFamily}` on stack `{FormatStackFamily(buildProfile.StackFamily)}`.",
                templateSelection.CandidateTemplateIds,
                templateSelection.TraceId,
                templateSelection.Reason)
        };
    }

    private async Task<PhraseFamilyResolution> ResolvePhraseFamilyAsync(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardDocument activeDocument,
        TaskboardBatch batch,
        TaskboardRunWorkItem workItem,
        AppSettings? settings,
        string endpoint,
        string selectedModel,
        CancellationToken cancellationToken)
    {
        var fallback = _phraseFamilyFallbackService.Resolve(workspaceRoot, activeImport, activeDocument, batch, workItem);
        if (fallback.ResolutionSource == TaskboardPhraseFamilyResolutionSource.OperationKind
            || fallback.ResolutionSource == TaskboardPhraseFamilyResolutionSource.DeterministicFallback)
        {
            FinalizePhraseResolutionTrace(fallback);
            return PhraseFamilyResolution.FromRecord(fallback);
        }

        if (!fallback.IsBlocked)
        {
            FinalizePhraseResolutionTrace(fallback);
            return PhraseFamilyResolution.FromRecord(fallback);
        }

        if (fallback.BlockerCode == TaskboardPhraseFamilyBlockerCode.DeterministicRuleConflict)
        {
            var tieBreak = _phraseFamilyTieBreakService.Resolve(
                activeDocument,
                batch,
                workItem,
                fallback.CandidatePhraseFamilies);
            fallback.TieBreakRuleId = tieBreak.RuleId;
            fallback.TieBreakSummary = tieBreak.Summary;

            if (tieBreak.IsResolved)
            {
                fallback.ShouldDecompose = true;
                fallback.IsBlocked = false;
                fallback.PhraseFamily = tieBreak.SelectedPhraseFamily;
                fallback.Confidence = "high";
                fallback.ResolutionSource = TaskboardPhraseFamilyResolutionSource.DeterministicFallback;
                fallback.ResolutionSummary = tieBreak.Summary;
                fallback.DeterministicCandidate = tieBreak.SelectedPhraseFamily;
                fallback.DeterministicConfidence = "high";
                fallback.DeterministicReason = tieBreak.RuleId;
                fallback.BlockerCode = TaskboardPhraseFamilyBlockerCode.None;
                fallback.BlockerMessage = "";
                if (!fallback.CandidatePhraseFamilies.Any(candidate => string.Equals(candidate, tieBreak.SelectedPhraseFamily, StringComparison.OrdinalIgnoreCase)))
                    fallback.CandidatePhraseFamilies.Insert(0, tieBreak.SelectedPhraseFamily);
                fallback.TerminalResolverStage = "phrase_family_tie_break_resolved";
                fallback.BuilderOperationResolutionStatus = "builder_operation_pending_downstream";
                fallback.LaneResolutionStatus = "lane_resolution_pending_downstream";
                FinalizePhraseResolutionTrace(fallback);
                return PhraseFamilyResolution.FromRecord(fallback);
            }

            fallback.IsBlocked = true;
            fallback.BlockerCode = tieBreak.BlockerCode;
            fallback.BlockerMessage = tieBreak.Summary;
            fallback.ResolutionSummary = tieBreak.Summary;
            fallback.TerminalResolverStage = "phrase_family_tie_break_blocked";
            fallback.BuilderOperationResolutionStatus = "builder_operation_not_reached_due_to_phrase_family_block";
            fallback.LaneResolutionStatus = "lane_resolution_not_reached_due_to_phrase_family_block";
        }

        var needsAdvisoryAssist = fallback.BlockerCode is TaskboardPhraseFamilyBlockerCode.NoDeterministicRule
            or TaskboardPhraseFamilyBlockerCode.DeterministicRuleConflict
            or TaskboardPhraseFamilyBlockerCode.TieBreakRuleNotApplicable
            or TaskboardPhraseFamilyBlockerCode.PhraseFamilyTieUnresolved;
        if (!needsAdvisoryAssist)
        {
            FinalizePhraseResolutionTrace(fallback);
            return PhraseFamilyResolution.FromRecord(fallback);
        }

        if (_phraseFamilyAgentService is null || settings is null)
        {
            fallback.AdvisoryStatus = "unavailable";
            fallback.TerminalResolverStage = "phrase_family_advisory_unavailable";
            fallback.BuilderOperationResolutionStatus = "builder_operation_not_reached_due_to_phrase_family_block";
            fallback.LaneResolutionStatus = "lane_resolution_not_reached_due_to_phrase_family_block";
            fallback.BlockerMessage = fallback.BlockerCode == TaskboardPhraseFamilyBlockerCode.NoDeterministicRule
                ? $"No deterministic phrase-family rule exists yet for `{workItem.Title}`, and advisory classification was unavailable."
                : $"Phrase family unresolved after deterministic fallback and tie-break rules for `{workItem.Title}`, and advisory tie-break was unavailable.";
            fallback.ResolutionSummary = fallback.BlockerMessage;
            FinalizePhraseResolutionTrace(fallback);
            return PhraseFamilyResolution.FromRecord(fallback);
        }

        var request = new PhraseFamilyAgentRequestPayload
        {
            TaskboardTitle = activeDocument.Title,
            BatchTitle = batch.Title,
            WorkItemTitle = workItem.Title,
            WorkItemSummary = workItem.Summary,
            WorkItemPrompt = workItem.PromptText,
            AllowedPhraseFamilies = [.. AllowedPhraseFamilies]
        };
        var result = await _phraseFamilyAgentService.ClassifyAsync(endpoint, selectedModel, settings, workspaceRoot, request, cancellationToken);
        var originalBlockerCode = fallback.BlockerCode;
        fallback.AdvisoryAttempted = true;
        fallback.AdvisoryTraceId = result.TraceId;
        fallback.AdvisoryStatus = result.Accepted
            ? "accepted"
            : result.Skipped
                ? "skipped"
                : "rejected";

        if (result.Accepted)
        {
            fallback.ShouldDecompose = true;
            fallback.IsBlocked = false;
            fallback.PhraseFamily = result.Payload.PhraseFamily;
            fallback.Confidence = result.Payload.Confidence;
            fallback.ResolutionSource = TaskboardPhraseFamilyResolutionSource.AdvisoryAgent;
            fallback.ResolutionSummary = $"Resolved phrase family `{result.Payload.PhraseFamily}` from advisory tie-break after deterministic fallback was insufficient.";
            fallback.AdvisoryAccepted = true;
            fallback.AdvisoryPhraseFamily = result.Payload.PhraseFamily;
            fallback.AdvisoryConfidence = result.Payload.Confidence;
            fallback.BlockerCode = TaskboardPhraseFamilyBlockerCode.None;
            fallback.BlockerMessage = "";
            fallback.TerminalResolverStage = "phrase_family_advisory_resolved";
            fallback.BuilderOperationResolutionStatus = "builder_operation_pending_downstream";
            fallback.LaneResolutionStatus = "lane_resolution_pending_downstream";
            FinalizePhraseResolutionTrace(fallback);
            return PhraseFamilyResolution.FromRecord(fallback);
        }

        fallback.AdvisoryAccepted = false;
        fallback.BlockerCode = originalBlockerCode == TaskboardPhraseFamilyBlockerCode.NoDeterministicRule
            ? TaskboardPhraseFamilyBlockerCode.NoDeterministicRule
            : TaskboardPhraseFamilyBlockerCode.PhraseFamilyTieUnresolved;
        fallback.BlockerMessage = originalBlockerCode == TaskboardPhraseFamilyBlockerCode.NoDeterministicRule
            ? (result.Skipped
                ? $"No deterministic phrase-family rule exists yet for `{workItem.Title}`, and advisory classification was unavailable."
                : $"No deterministic phrase-family rule exists yet for `{workItem.Title}`, and advisory classification was rejected.")
            : (result.Skipped
                ? $"Phrase family unresolved after deterministic fallback and tie-break rules for `{workItem.Title}`, and advisory tie-break was unavailable."
                : $"Phrase family unresolved after deterministic fallback and tie-break rules for `{workItem.Title}`, and advisory tie-break was rejected.");
        fallback.ResolutionSummary = fallback.BlockerMessage;
        fallback.TerminalResolverStage = result.Skipped
            ? "phrase_family_advisory_unavailable"
            : "phrase_family_advisory_rejected";
        fallback.BuilderOperationResolutionStatus = "builder_operation_not_reached_due_to_phrase_family_block";
        fallback.LaneResolutionStatus = "lane_resolution_not_reached_due_to_phrase_family_block";
        FinalizePhraseResolutionTrace(fallback);
        return PhraseFamilyResolution.FromRecord(fallback);
    }

    private static void FinalizePhraseResolutionTrace(TaskboardPhraseFamilyResolutionRecord record)
    {
        record.TerminalResolverStage = FirstNonEmpty(record.TerminalResolverStage, record.IsBlocked ? "phrase_family_blocked" : "phrase_family_resolved");
        record.BuilderOperationResolutionStatus = FirstNonEmpty(
            record.BuilderOperationResolutionStatus,
            record.IsBlocked ? "builder_operation_not_reached_due_to_phrase_family_block" : "builder_operation_pending_downstream");
        record.LaneResolutionStatus = FirstNonEmpty(
            record.LaneResolutionStatus,
            record.IsBlocked ? "lane_resolution_not_reached_due_to_phrase_family_block" : "lane_resolution_pending_downstream");
        record.ResolutionPathTrace = FirstNonEmpty(
            record.ResolutionPathTrace,
            $"taskboard_run_projection>auto_run>decomposition>phrase_family terminal_stage={DisplayValue(record.TerminalResolverStage)} builder_operation={DisplayValue(record.BuilderOperationResolutionStatus)} lane_resolution={DisplayValue(record.LaneResolutionStatus)}");

        var traceSummary =
            $"trace={DisplayValue(record.ResolutionPathTrace)} raw={DisplayValue(record.RawPhraseText)} normalized={DisplayValue(record.NormalizedPhraseText)} closest={DisplayValue(record.ClosestKnownFamilyGroup)} builder_operation={DisplayValue(record.BuilderOperationResolutionStatus)} lane_resolution={DisplayValue(record.LaneResolutionStatus)}";
        record.ResolutionSummary = AppendTrace(record.ResolutionSummary, traceSummary);
        if (record.IsBlocked)
            record.BlockerMessage = AppendTrace(record.BlockerMessage, traceSummary);
    }

    private static string AppendTrace(string primaryText, string traceSummary)
    {
        if (string.IsNullOrWhiteSpace(traceSummary))
            return primaryText;
        if (string.IsNullOrWhiteSpace(primaryText))
            return traceSummary;
        return primaryText.Contains(traceSummary, StringComparison.OrdinalIgnoreCase)
            ? primaryText
            : $"{primaryText} {traceSummary}";
    }

    private TaskboardWorkItemDecompositionRecord BuildDotnetDecomposition(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardDocument activeDocument,
        TaskboardBatch batch,
        TaskboardRunWorkItem workItem,
        TaskboardHeadingPolicyRecord headingPolicy,
        string supportActionablePhraseFamily,
        PhraseFamilyResolution phraseFamilyResolution,
        TemplateSelection templateSelection,
        TaskboardBuildProfileResolutionRecord buildProfile,
        RamDbService? ramDbService,
        TaskboardPlanRunStateRecord? activeRunState)
    {
        var phraseFamily = phraseFamilyResolution.PhraseFamily;
        var templateId = templateSelection.TemplateId;
        var candidateTemplateIds = templateSelection.CandidateTemplateIds;
        var executionState = ramDbService?.LoadExecutionState(workspaceRoot);
        var maintenanceBaseline = _maintenanceBaselineService.Resolve(workspaceRoot, activeDocument, executionState);
        var canonicalOperationKind = CleanIdentifier(phraseFamilyResolution.CanonicalOperationKind);
        var canonicalTargetPath = NormalizeRelativePath(phraseFamilyResolution.CanonicalTargetPath);
        var canonicalProjectName = CleanIdentifier(phraseFamilyResolution.CanonicalProjectName);
        var canonicalTemplateHint = CleanIdentifier(phraseFamilyResolution.CanonicalTemplateHint);
        var canonicalRoleHint = CleanIdentifier(phraseFamilyResolution.CanonicalRoleHint);
        var canonicalSolutionPath = canonicalTargetPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            ? canonicalTargetPath
            : "";
        var canonicalProjectPath = canonicalTargetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? canonicalTargetPath
            : "";
        var canonicalProjectDirectoryHint = ResolveOwningProjectDirectory(canonicalTargetPath, canonicalRoleHint);
        var canonicalAppName = FirstNonEmpty(
            TrimKnownProjectRoleSuffix(canonicalProjectName),
            TrimKnownProjectRoleSuffix(Path.GetFileNameWithoutExtension(canonicalProjectPath)),
            TrimKnownProjectRoleSuffix(Path.GetFileName(canonicalProjectDirectoryHint)),
            TrimKnownProjectRoleSuffix(Path.GetFileNameWithoutExtension(canonicalSolutionPath)));
        var appName = FirstNonEmpty(
            canonicalAppName,
            DeriveAppName(activeDocument.Title, workspaceRoot));
        var template = ResolveDotnetTemplate(buildProfile);
        var requestedProjectTemplate = ResolveRequestedDotnetProjectTemplate(canonicalTemplateHint, canonicalOperationKind, workItem, template);
        var buildTargetResolution = _workspaceBuildIndexService.ResolveForBuild(workspaceRoot, "", "");
        var existingBuildTargetPath = buildTargetResolution.Success && buildTargetResolution.Item is not null
            ? buildTargetResolution.Item.RelativePath
            : "";
        var existingSolutionPaths = ResolveExistingWorkspaceFiles(workspaceRoot, "*.sln");
        var existingSolutionPath = existingSolutionPaths.FirstOrDefault();
        var existingNamedSolutionPath = existingSolutionPaths.FirstOrDefault(path =>
            Path.GetFileNameWithoutExtension(path).Equals(appName, StringComparison.OrdinalIgnoreCase));
        var existingProjectPaths = ResolveExistingWorkspaceFiles(workspaceRoot, "*.csproj");
        var solutionPath = maintenanceBaseline.IsMaintenanceMode && maintenanceBaseline.BaselineResolved
            ? maintenanceBaseline.PrimarySolutionPath
            : FirstNonEmpty(canonicalSolutionPath, existingNamedSolutionPath, $"{appName}.sln", existingSolutionPath);
        var canonicalTargetsPrimaryApp =
            canonicalOperationKind.EndsWith(".wpf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(canonicalRoleHint, "ui", StringComparison.OrdinalIgnoreCase)
            || string.Equals(canonicalRoleHint, "views", StringComparison.OrdinalIgnoreCase)
            || string.Equals(canonicalRoleHint, "state", StringComparison.OrdinalIgnoreCase)
            || string.Equals(canonicalProjectName, appName, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(canonicalProjectPath)
                && Path.GetFileNameWithoutExtension(canonicalProjectPath).Equals(appName, StringComparison.OrdinalIgnoreCase));
        var defaultCoreProjectPath = NormalizeRelativePath(Path.Combine("src", $"{appName}.Core", $"{appName}.Core.csproj"));
        var defaultTestsProjectPath = NormalizeRelativePath(Path.Combine("tests", $"{appName}.Tests", $"{appName}.Tests.csproj"));
        var existingProjectPath = ResolvePrimaryWorkspaceProjectPath(existingProjectPaths, appName, canonicalProjectPath, canonicalProjectDirectoryHint, canonicalRoleHint);
        var defaultAppProjectPath = FirstNonEmpty(
            canonicalTargetsPrimaryApp ? canonicalProjectPath : "",
            existingProjectPath,
            NormalizeRelativePath(Path.Combine("src", appName, $"{appName}.csproj")));
        var existingCoreProjectPath = ResolveRoleWorkspaceProjectPath(existingProjectPaths, $"{appName}.Core", "core", canonicalProjectPath, canonicalRoleHint);
        var existingTestsProjectPath = ResolveRoleWorkspaceProjectPath(existingProjectPaths, $"{appName}.Tests", "tests", canonicalProjectPath, canonicalRoleHint);
        var projectPath = maintenanceBaseline.IsMaintenanceMode && maintenanceBaseline.BaselineResolved
            ? FirstNonEmpty(maintenanceBaseline.PrimaryUiProjectPath, existingProjectPath)
            : FirstNonEmpty(
                canonicalRoleHint.Equals("ui", StringComparison.OrdinalIgnoreCase)
                || canonicalRoleHint.Equals("state", StringComparison.OrdinalIgnoreCase)
                || canonicalRoleHint.Equals("views", StringComparison.OrdinalIgnoreCase)
                || canonicalRoleHint.Equals("storage", StringComparison.OrdinalIgnoreCase)
                || canonicalRoleHint.Equals("contracts", StringComparison.OrdinalIgnoreCase)
                || canonicalRoleHint.Equals("models", StringComparison.OrdinalIgnoreCase)
                    ? canonicalProjectPath
                    : "",
                existingProjectPath,
                defaultAppProjectPath);
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var projectDirectory = FirstNonEmpty(
            canonicalProjectDirectoryHint,
            NormalizeRelativePath(Path.GetDirectoryName(projectPath)?.Replace('\\', Path.DirectorySeparatorChar) ?? Path.Combine("src", projectName)));
        var requestedProjectName = FirstNonEmpty(
            ResolveExplicitProjectName(workItem, canonicalOperationKind),
            canonicalProjectName,
            !string.IsNullOrWhiteSpace(canonicalProjectPath) ? Path.GetFileNameWithoutExtension(canonicalProjectPath) : "",
            projectName);
        var requestedProjectPath = FirstNonEmpty(
            ResolveExplicitProjectPath(workItem, existingProjectPaths, requestedProjectTemplate),
            canonicalProjectPath,
            BuildDefaultProjectPath(requestedProjectName, requestedProjectTemplate),
            projectPath);
        var requestedProjectDirectory = FirstNonEmpty(
            ResolveOwningProjectDirectory(requestedProjectPath, canonicalRoleHint),
            NormalizeRelativePath(Path.GetDirectoryName(requestedProjectPath)?.Replace('\\', Path.DirectorySeparatorChar) ?? ""));
        var requestedProjectRole = FirstNonEmpty(
            canonicalRoleHint,
            InferProjectRoleFromName(requestedProjectName));
        var canonicalCreateProjectOperation = canonicalOperationKind.StartsWith("dotnet.create_project", StringComparison.OrdinalIgnoreCase);
        var canonicalAddProjectToSolutionOperation = canonicalOperationKind.Equals("dotnet.add_project_to_solution", StringComparison.OrdinalIgnoreCase);
        var canonicalAddProjectReferenceOperation = canonicalOperationKind.Equals("dotnet.add_project_reference", StringComparison.OrdinalIgnoreCase);
        var canonicalCreateSolutionOperation = canonicalOperationKind.Equals("dotnet.create_solution", StringComparison.OrdinalIgnoreCase);
        var requestedProjectIsPrimaryApp = PathsResolveToSameWorkspaceTarget(requestedProjectPath, defaultAppProjectPath)
            || string.Equals(requestedProjectName, appName, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(requestedProjectTemplate, "wpf", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(requestedProjectRole)
                    || string.Equals(requestedProjectRole, "ui", StringComparison.OrdinalIgnoreCase)))
            || (string.IsNullOrWhiteSpace(existingProjectPath)
                && requestedProjectTemplate is "console" or "worker" or "webapi"
                && requestedProjectRole is "app" or "worker" or "api");
        var coreProjectPath = maintenanceBaseline.IsMaintenanceMode && maintenanceBaseline.BaselineResolved
            ? maintenanceBaseline.CoreProjectPath
            : existingCoreProjectPath ?? defaultCoreProjectPath;
        var coreProjectDirectory = NormalizeRelativePath(Path.GetDirectoryName(coreProjectPath)?.Replace('\\', Path.DirectorySeparatorChar) ?? Path.Combine("src", $"{appName}.Core"));
        var storageProjectPath = maintenanceBaseline.IsMaintenanceMode && maintenanceBaseline.BaselineResolved
            ? maintenanceBaseline.StorageProjectPath
            : NormalizeRelativePath(Path.Combine(projectDirectory, "Storage", $"{projectName}.Storage.csproj"));
        var storageProjectDirectory = maintenanceBaseline.IsMaintenanceMode && maintenanceBaseline.BaselineResolved
            ? FirstNonEmpty(
                maintenanceBaseline.StorageAuthorityRoot,
                NormalizeRelativePath(Path.GetDirectoryName(storageProjectPath)?.Replace('\\', Path.DirectorySeparatorChar) ?? ""))
            : NormalizeRelativePath(Path.Combine(projectDirectory, "Storage"));
        var storageProjectName = !string.IsNullOrWhiteSpace(storageProjectPath)
            ? Path.GetFileNameWithoutExtension(storageProjectPath)
            : projectName;
        var testsProjectPath = maintenanceBaseline.IsMaintenanceMode && maintenanceBaseline.BaselineResolved
            ? maintenanceBaseline.TestsProjectPath
            : existingTestsProjectPath ?? defaultTestsProjectPath;
        var testsProjectDirectory = NormalizeRelativePath(Path.GetDirectoryName(testsProjectPath)?.Replace('\\', Path.DirectorySeparatorChar) ?? Path.Combine("tests", $"{appName}.Tests"));
        var testsProjectName = Path.GetFileNameWithoutExtension(testsProjectPath);
        var buildVerificationTargetPath = maintenanceBaseline.IsMaintenanceMode && maintenanceBaseline.BaselineResolved
            ? FirstNonEmpty(maintenanceBaseline.PrimarySolutionPath, projectPath)
            : FirstNonEmpty(
                canonicalOperationKind.Equals("dotnet.build", StringComparison.OrdinalIgnoreCase)
                || canonicalOperationKind.Equals("dotnet.test", StringComparison.OrdinalIgnoreCase)
                    ? canonicalTargetPath
                    : "",
                existingBuildTargetPath,
                existingNamedSolutionPath,
                $"{appName}.sln",
                existingSolutionPath,
                existingProjectPath,
                existingCoreProjectPath,
                existingTestsProjectPath);
        var items = new List<TaskboardDecomposedWorkItem>();
        TaskboardDecomposedWorkItem DotnetTool(string subOrdinal, string operationKind, string description, string expectedArtifact, string validationHint, ToolRequest toolRequest)
            => CreateToolItem(workItem, subOrdinal, operationKind, buildProfile.StackFamily, phraseFamily, templateId, candidateTemplateIds, description, expectedArtifact, validationHint, toolRequest);
        TaskboardDecomposedWorkItem DotnetPage(string pageName, string title, string subOrdinal)
            => CreatePureXamlPageItem(workItem, buildProfile.StackFamily, phraseFamily, templateId, candidateTemplateIds, projectDirectory, projectName, pageName, title, subOrdinal);

        switch (phraseFamily)
        {
            case "solution_scaffold":
            case "project_scaffold":
                if (maintenanceBaseline.IsMaintenanceMode)
                {
                    return BuildBlocked(
                        activeImport,
                        batch,
                        workItem,
                        phraseFamilyResolution,
                        buildProfile,
                        BuildMaintenanceScaffoldBlockedReason(workItem.Title, maintenanceBaseline));
                }

                if (canonicalCreateSolutionOperation)
                {
                    items.Add(DotnetTool(
                        "1",
                        "create_solution",
                        "Create app solution",
                        solutionPath,
                        "Workspace solution exists.",
                        new ToolRequest
                        {
                            ToolName = "create_dotnet_solution",
                            Reason = $"Decomposed `{workItem.Title}` into a deterministic .NET solution scaffold.",
                            Arguments =
                            {
                                ["solution_name"] = appName
                            }
                        }));
                    break;
                }

                var explicitSiblingProjectScaffold =
                    (canonicalCreateProjectOperation || canonicalAddProjectToSolutionOperation)
                    && !requestedProjectIsPrimaryApp
                    && !string.IsNullOrWhiteSpace(requestedProjectName)
                    && !string.IsNullOrWhiteSpace(requestedProjectPath)
                    && !string.IsNullOrWhiteSpace(requestedProjectTemplate);
                var explicitReferenceSourceProjectPath = "";
                var explicitReferenceTargetProjectPath = "";
                var explicitReferenceWiring = canonicalAddProjectReferenceOperation
                    && TryResolveExplicitProjectReferencePair(workItem, existingProjectPaths, out explicitReferenceSourceProjectPath, out explicitReferenceTargetProjectPath);
                if (explicitSiblingProjectScaffold || explicitReferenceWiring)
                {
                    var stepOrdinal = 1;
                    if (!canonicalAddProjectReferenceOperation && !WorkspaceFileExists(workspaceRoot, solutionPath))
                    {
                        items.Add(DotnetTool(
                            stepOrdinal.ToString(),
                            "create_solution",
                            "Create app solution",
                            solutionPath,
                            "Workspace solution exists.",
                            new ToolRequest
                            {
                                ToolName = "create_dotnet_solution",
                                Reason = $"Decomposed `{workItem.Title}` into authoritative single-solution scaffold continuity for the app family.",
                                Arguments =
                                {
                                    ["solution_name"] = appName
                                }
                            }));
                        stepOrdinal++;
                    }

                    if (canonicalCreateProjectOperation)
                    {
                        items.Add(DotnetTool(
                            stepOrdinal.ToString(),
                            "create_project",
                            BuildSecondaryProjectCreateDescription(requestedProjectName, requestedProjectTemplate),
                            requestedProjectPath,
                            $"Project `{requestedProjectName}` exists.",
                            new ToolRequest
                            {
                                ToolName = "create_dotnet_project",
                                Reason = $"Decomposed `{workItem.Title}` into a deterministic sibling-project scaffold that preserves the authoritative solution, project identity, and template.",
                                Arguments = BuildProjectScaffoldArguments(
                                    requestedProjectTemplate,
                                    requestedProjectName,
                                    requestedProjectDirectory,
                                    solutionPath,
                                    requestedProjectRole,
                                    attach: true,
                                    validationTarget: solutionPath)
                            }));
                        stepOrdinal++;
                    }

                    if (canonicalAddProjectToSolutionOperation || canonicalCreateProjectOperation)
                    {
                        items.Add(DotnetTool(
                            stepOrdinal.ToString(),
                            "add_project_to_solution",
                            $"Add project `{requestedProjectName}` to solution",
                            solutionPath,
                            $"Solution references `{requestedProjectName}`.",
                            new ToolRequest
                            {
                                ToolName = "add_project_to_solution",
                                Reason = $"Decomposed `{workItem.Title}` into deterministic sibling-project solution attach using the authoritative solution target.",
                                Arguments =
                                {
                                    ["solution_path"] = solutionPath,
                                    ["project_path"] = requestedProjectPath
                                }
                            }));
                        stepOrdinal++;
                    }

                    if (explicitReferenceWiring
                        && !PathsResolveToSameWorkspaceTarget(explicitReferenceSourceProjectPath, explicitReferenceTargetProjectPath))
                    {
                        items.Add(DotnetTool(
                            stepOrdinal.ToString(),
                            "add_project_reference",
                            BuildExplicitProjectReferenceDescription(explicitReferenceSourceProjectPath, explicitReferenceTargetProjectPath),
                            explicitReferenceSourceProjectPath,
                            BuildExplicitProjectReferenceValidationHint(explicitReferenceSourceProjectPath, explicitReferenceTargetProjectPath),
                            new ToolRequest
                            {
                                ToolName = "add_dotnet_project_reference",
                                Reason = $"Decomposed `{workItem.Title}` into deterministic sibling-project reference wiring with preserved source/reference identity.",
                                Arguments =
                                {
                                    ["project_path"] = explicitReferenceSourceProjectPath,
                                    ["reference_path"] = explicitReferenceTargetProjectPath,
                                    ["continuation_source_project_path"] = explicitReferenceSourceProjectPath,
                                    ["continuation_reference_project_path"] = explicitReferenceTargetProjectPath
                                }
                            }));
                    }

                    break;
                }

                if (!WorkspaceFileExists(workspaceRoot, solutionPath))
                {
                    items.Add(DotnetTool(
                        "1",
                        "create_solution",
                        "Create app solution",
                        solutionPath,
                        "Workspace solution exists.",
                        new ToolRequest
                        {
                            ToolName = "create_dotnet_solution",
                            Reason = $"Decomposed `{workItem.Title}` into a deterministic .NET solution scaffold.",
                            Arguments =
                            {
                                ["solution_name"] = appName
                            }
                        }));
                }

                if (!WorkspaceFileExists(workspaceRoot, projectPath))
                {
                    items.Add(DotnetTool(
                        "2",
                        "create_project",
                        BuildPrimaryProjectCreateDescription(projectName, requestedProjectTemplate),
                        projectPath,
                        $"{projectName}.csproj exists.",
                        new ToolRequest
                        {
                            ToolName = "create_dotnet_project",
                            Reason = $"Decomposed `{workItem.Title}` into a deterministic .NET project scaffold.",
                            Arguments = BuildProjectScaffoldArguments(
                                requestedProjectTemplate,
                                projectName,
                                projectDirectory,
                                solutionPath,
                                requestedProjectRole,
                                attach: true,
                                validationTarget: solutionPath)
                        }));
                }

                items.Add(DotnetTool(
                    "3",
                    "add_project_to_solution",
                    "Add app project to solution",
                    solutionPath,
                    "Solution references the app project.",
                    new ToolRequest
                    {
                        ToolName = "add_project_to_solution",
                        Reason = $"Decomposed `{workItem.Title}` into a deterministic solution wiring step.",
                        Arguments =
                        {
                            ["solution_path"] = solutionPath,
                            ["project_path"] = projectPath
                        }
                    }));
                break;

            case "build_first_ui_shell":
                if (maintenanceBaseline.IsMaintenanceMode
                    && !EnsureMaintenanceTargetResolved(
                        maintenanceBaseline,
                        projectPath,
                        "UI project",
                        activeImport,
                        batch,
                        workItem,
                        phraseFamilyResolution,
                        buildProfile,
                        out var maintenanceUiBlocked))
                {
                    return maintenanceUiBlocked;
                }

                if (!maintenanceBaseline.IsMaintenanceMode && !WorkspaceFileExists(workspaceRoot, solutionPath))
                {
                    items.Add(DotnetTool(
                        "1",
                        "create_solution",
                        "Create app solution",
                        solutionPath,
                        "Workspace solution exists.",
                        new ToolRequest
                        {
                            ToolName = "create_dotnet_solution",
                            Reason = $"Decomposed `{workItem.Title}` into a deterministic .NET solution scaffold.",
                            Arguments =
                            {
                                ["solution_name"] = appName
                            }
                        }));
                }

                if (!maintenanceBaseline.IsMaintenanceMode && !WorkspaceFileExists(workspaceRoot, projectPath))
                {
                    items.Add(DotnetTool(
                        "2",
                        "create_project",
                        BuildPrimaryProjectCreateDescription(projectName, requestedProjectTemplate),
                        projectPath,
                        $"{projectName}.csproj exists.",
                        new ToolRequest
                        {
                            ToolName = "create_dotnet_project",
                            Reason = $"Decomposed `{workItem.Title}` into a deterministic .NET project scaffold.",
                            Arguments = BuildProjectScaffoldArguments(
                                requestedProjectTemplate,
                                projectName,
                                projectDirectory,
                                solutionPath,
                                requestedProjectRole,
                                attach: true,
                                validationTarget: solutionPath)
                        }));
                }

                if (!maintenanceBaseline.IsMaintenanceMode)
                {
                    items.Add(DotnetTool(
                        "3",
                        "add_project_to_solution",
                        "Add app project to solution",
                        solutionPath,
                        "Solution references the app project.",
                        new ToolRequest
                        {
                            ToolName = "add_project_to_solution",
                            Reason = $"Decomposed `{workItem.Title}` into a deterministic solution wiring step.",
                            Arguments =
                            {
                                ["solution_path"] = solutionPath,
                                ["project_path"] = projectPath
                            }
                        }));
                }

                items.Add(DotnetTool(
                    maintenanceBaseline.IsMaintenanceMode ? "1" : "4",
                    "write_shell_layout",
                    "Write main UI shell layout",
                    NormalizeRelativePath(Path.Combine(projectDirectory, "MainWindow.xaml")),
                    "MainWindow.xaml contains a shell layout.",
                    new ToolRequest
                    {
                        ToolName = "create_dotnet_page_view",
                        Reason = $"Decomposed `{workItem.Title}` into a bounded WPF shell layout update.",
                        Arguments =
                        {
                            ["path"] = NormalizeRelativePath(Path.Combine(projectDirectory, "MainWindow.xaml")),
                            ["content"] = BuildDotnetMainWindowXaml(projectName, activeDocument.PhaseMetadata.DisplayTitle, activeDocument.ObjectiveText)
                        }
                    }));

                items.Add(DotnetPage("DashboardPage", "Dashboard", maintenanceBaseline.IsMaintenanceMode ? "2" : "5"));
                items.Add(DotnetPage("FindingsPage", "Findings", maintenanceBaseline.IsMaintenanceMode ? "3" : "6"));
                items.Add(DotnetPage("HistoryPage", "History", maintenanceBaseline.IsMaintenanceMode ? "4" : "7"));
                items.Add(DotnetPage("SettingsPage", "Settings", maintenanceBaseline.IsMaintenanceMode ? "5" : "8"));

                items.Add(DotnetTool(
                    maintenanceBaseline.IsMaintenanceMode ? "6" : "9",
                    "build_solution",
                    "Validate desktop shell build",
                    solutionPath,
                    "The .NET solution builds successfully.",
                    new ToolRequest
                    {
                        ToolName = "dotnet_build",
                        Reason = $"Decomposed `{workItem.Title}` into a bounded validation build.",
                        Arguments =
                        {
                            ["project"] = solutionPath
                        }
                    }));
                break;

            case "ui_shell_sections":
                if (maintenanceBaseline.IsMaintenanceMode
                    && !EnsureMaintenanceTargetResolved(
                        maintenanceBaseline,
                        projectPath,
                        "UI project",
                        activeImport,
                        batch,
                        workItem,
                        phraseFamilyResolution,
                        buildProfile,
                        out maintenanceUiBlocked))
                {
                    return maintenanceUiBlocked;
                }

                if (!maintenanceBaseline.IsMaintenanceMode && !WorkspaceFileExists(workspaceRoot, solutionPath))
                {
                    items.Add(DotnetTool(
                        "1",
                        "create_solution",
                        "Create app solution",
                        solutionPath,
                        "Workspace solution exists.",
                        new ToolRequest
                        {
                            ToolName = "create_dotnet_solution",
                            Reason = $"Decomposed `{workItem.Title}` into a deterministic .NET solution scaffold.",
                            Arguments =
                            {
                                ["solution_name"] = appName
                            }
                        }));
                }

                if (!maintenanceBaseline.IsMaintenanceMode && !WorkspaceFileExists(workspaceRoot, projectPath))
                {
                    items.Add(DotnetTool(
                        "2",
                        "create_project",
                        BuildPrimaryProjectCreateDescription(projectName, requestedProjectTemplate),
                        projectPath,
                        $"{projectName}.csproj exists.",
                        new ToolRequest
                        {
                            ToolName = "create_dotnet_project",
                            Reason = $"Decomposed `{workItem.Title}` into a deterministic .NET project scaffold.",
                            Arguments = BuildProjectScaffoldArguments(
                                requestedProjectTemplate,
                                projectName,
                                projectDirectory,
                                solutionPath,
                                requestedProjectRole,
                                attach: true,
                                validationTarget: solutionPath)
                        }));
                }

                if (!maintenanceBaseline.IsMaintenanceMode)
                {
                    items.Add(DotnetTool(
                        "3",
                        "add_project_to_solution",
                        "Add app project to solution",
                        solutionPath,
                        "Solution references the app project.",
                        new ToolRequest
                        {
                            ToolName = "add_project_to_solution",
                            Reason = $"Decomposed `{workItem.Title}` into a deterministic solution wiring step.",
                            Arguments =
                            {
                                ["solution_path"] = solutionPath,
                                ["project_path"] = projectPath
                            }
                        }));
                }
                items.Add(DotnetPage("DashboardPage", "Dashboard", maintenanceBaseline.IsMaintenanceMode ? "1" : "4"));
                items.Add(DotnetPage("FindingsPage", "Findings", maintenanceBaseline.IsMaintenanceMode ? "2" : "5"));
                items.Add(DotnetPage("HistoryPage", "History", maintenanceBaseline.IsMaintenanceMode ? "3" : "6"));
                items.Add(DotnetPage("SettingsPage", "Settings", maintenanceBaseline.IsMaintenanceMode ? "4" : "7"));
                items.Add(DotnetTool(maintenanceBaseline.IsMaintenanceMode ? "5" : "8", "make_state_dir", "Create navigation/state folder", NormalizeRelativePath(Path.Combine(projectDirectory, "State")), "State folder exists.", MakeDirRequest(NormalizeRelativePath(Path.Combine(projectDirectory, "State")), workItem.Title)));
                items.Add(DotnetTool(maintenanceBaseline.IsMaintenanceMode ? "6" : "9", "write_navigation_item", "Write navigation item model", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "NavigationItem.cs")), "NavigationItem.cs exists.", NamedWriteRequest("register_navigation", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "NavigationItem.cs")), BuildNavigationItemCs(projectName), workItem.Title)));
                items.Add(DotnetTool(maintenanceBaseline.IsMaintenanceMode ? "7" : "10", "write_shell_registration", "Write shell navigation registry", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "ShellNavigationRegistry.cs")), "ShellNavigationRegistry.cs exists.", NamedWriteRequest("register_navigation", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "ShellNavigationRegistry.cs")), BuildShellNavigationRegistryCs(projectName), workItem.Title)));
                items.Add(DotnetTool(maintenanceBaseline.IsMaintenanceMode ? "8" : "11", "write_app_state", "Write app state model", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "AppState.cs")), "AppState.cs exists.", NamedWriteRequest("create_dotnet_viewmodel", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "AppState.cs")), BuildAppStateCs(projectName), workItem.Title, MergeArguments(
                    BuildCodeIntentArguments("state", "model", projectName, $"{projectName}.State", "integrated", solutionPath, "viewmodel_required", "verification_required"),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dependency_prerequisites"] = "NavigationItem,ShellNavigationRegistry",
                        ["dependency_ordering_status"] = "satisfied_prerequisites",
                        ["dependency_ordering_summary"] = "AppState depends on navigation registration artifacts and is scheduled after them."
                    }))));
                items.Add(DotnetTool(maintenanceBaseline.IsMaintenanceMode ? "9" : "12", "write_shell_viewmodel", "Write shell viewmodel", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "ShellViewModel.cs")), "ShellViewModel.cs exists.", NamedWriteRequest("create_dotnet_viewmodel", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "ShellViewModel.cs")), BuildShellViewModelCs(projectName), workItem.Title, MergeArguments(
                    BuildCodeIntentArguments("state", "viewmodel", projectName, $"{projectName}.State", "integrated", solutionPath, "binding_required", "verification_required"),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dependency_prerequisites"] = "AppState",
                        ["dependency_ordering_status"] = "defer_until_prerequisite_ready",
                        ["dependency_ordering_summary"] = "ShellViewModel requires AppState and should only be written after the state model exists."
                    }))));
                items.Add(DotnetTool(maintenanceBaseline.IsMaintenanceMode ? "10" : "13", "build_solution", "Validate shell page set build", solutionPath, "The .NET solution builds successfully.", new ToolRequest
                {
                    ToolName = "dotnet_build",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded grouped shell build validation step.",
                    Arguments =
                    {
                        ["project"] = solutionPath
                    }
                }));
                break;

            case "core_domain_models_contracts":
            case "repository_scaffold":
                var continuationSourceProjectPath = ResolveDomainContractsContinuationSourceProjectPath(workItem, projectPath, testsProjectPath);
                if (maintenanceBaseline.IsMaintenanceMode
                    && !EnsureMaintenanceTargetResolved(
                        maintenanceBaseline,
                        coreProjectPath,
                        "Core project",
                        activeImport,
                        batch,
                        workItem,
                        phraseFamilyResolution,
                        buildProfile,
                        out var maintenanceCoreBlocked))
                {
                    return maintenanceCoreBlocked;
                }

                if (!maintenanceBaseline.IsMaintenanceMode)
                {
                    items.Add(DotnetTool(
                        "1",
                        "create_core_library",
                        "Create core contracts library",
                        coreProjectPath,
                        "Core contracts library project exists.",
                        new ToolRequest
                        {
                            ToolName = "create_dotnet_project",
                            Reason = $"Decomposed `{workItem.Title}` into a deterministic core library scaffold.",
                            Arguments = BuildProjectScaffoldArguments(
                                "classlib",
                                $"{appName}.Core",
                                coreProjectDirectory,
                                solutionPath,
                                "core",
                                attach: true,
                                validationTarget: solutionPath)
                        }));
                    items[^1].ToolRequest!.Arguments["continuation_source_project_path"] = continuationSourceProjectPath;
                    items[^1].ToolRequest!.Arguments["continuation_reference_project_path"] = coreProjectPath;
                    items.Add(DotnetTool(
                        "2",
                        "attach_core_library",
                        "Add core contracts library to solution",
                        solutionPath,
                        "Solution references the core contracts library.",
                        new ToolRequest
                        {
                            ToolName = "add_project_to_solution",
                            Reason = $"Decomposed `{workItem.Title}` into a deterministic solution attach step.",
                            Arguments =
                            {
                                ["solution_path"] = solutionPath,
                                ["project_path"] = coreProjectPath,
                                ["continuation_source_project_path"] = continuationSourceProjectPath,
                                ["continuation_reference_project_path"] = coreProjectPath
                            }
                        }));
                    if (!PathsResolveToSameWorkspaceTarget(continuationSourceProjectPath, coreProjectPath))
                    {
                        items.Add(DotnetTool(
                            "3",
                            "add_domain_reference",
                            BuildDomainContractsReferenceDescription(continuationSourceProjectPath, testsProjectPath),
                            continuationSourceProjectPath,
                            BuildDomainContractsReferenceValidationHint(continuationSourceProjectPath, testsProjectPath),
                            new ToolRequest
                            {
                                ToolName = "add_dotnet_project_reference",
                                Reason = $"Decomposed `{workItem.Title}` into a deterministic project reference step.",
                                Arguments =
                                {
                                    ["project_path"] = continuationSourceProjectPath,
                                    ["reference_path"] = coreProjectPath,
                                    ["continuation_source_project_path"] = continuationSourceProjectPath,
                                    ["continuation_reference_project_path"] = coreProjectPath
                                }
                            }));
                    }
                }
                items.Add(DotnetTool(maintenanceBaseline.IsMaintenanceMode ? "1" : "4", "make_contracts_dir", "Create contracts folder", NormalizeRelativePath(Path.Combine(coreProjectDirectory, "Contracts")), "Contracts folder exists.", MakeDirRequest(NormalizeRelativePath(Path.Combine(coreProjectDirectory, "Contracts")), workItem.Title)));
                items.Add(DotnetTool(maintenanceBaseline.IsMaintenanceMode ? "2" : "5", "make_models_dir", "Create models folder", NormalizeRelativePath(Path.Combine(coreProjectDirectory, "Models")), "Models folder exists.", MakeDirRequest(NormalizeRelativePath(Path.Combine(coreProjectDirectory, "Models")), workItem.Title)));
                items.Add(DotnetTool(maintenanceBaseline.IsMaintenanceMode ? "3" : "6", "write_contract_file", "Write check contract", NormalizeRelativePath(Path.Combine(coreProjectDirectory, "Contracts", "CheckDefinition.cs")), "CheckDefinition.cs exists.", NamedWriteRequest("write_file", NormalizeRelativePath(Path.Combine(coreProjectDirectory, "Contracts", "CheckDefinition.cs")), BuildDotnetCheckDefinitionCs(appName), workItem.Title, BuildCodeIntentArguments("contracts", "interface", $"{appName}.Core", $"{appName}.Core.Contracts", "structural", solutionPath, "implementation_required", "verification_required"))));
                items.Add(DotnetTool(maintenanceBaseline.IsMaintenanceMode ? "4" : "7", "write_domain_model_file", "Write finding model", NormalizeRelativePath(Path.Combine(coreProjectDirectory, "Models", "FindingRecord.cs")), "FindingRecord.cs exists.", NamedWriteRequest("write_file", NormalizeRelativePath(Path.Combine(coreProjectDirectory, "Models", "FindingRecord.cs")), BuildDotnetFindingRecordCs(appName), workItem.Title, BuildCodeIntentArguments("core", "model", $"{appName}.Core", $"{appName}.Core.Models", "structural", solutionPath, "consumer_required", "verification_required"))));
                break;

            case "add_navigation_app_state":
                if (maintenanceBaseline.IsMaintenanceMode
                    && !EnsureMaintenanceTargetResolved(
                        maintenanceBaseline,
                        projectPath,
                        "UI project",
                        activeImport,
                        batch,
                        workItem,
                        phraseFamilyResolution,
                        buildProfile,
                        out maintenanceUiBlocked))
                {
                    return maintenanceUiBlocked;
                }

                items.Add(DotnetTool("1", "make_state_dir", "Create navigation/state folder", NormalizeRelativePath(Path.Combine(projectDirectory, "State")), "State folder exists.", MakeDirRequest(NormalizeRelativePath(Path.Combine(projectDirectory, "State")), workItem.Title)));
                items.Add(DotnetTool("2", "write_navigation_item", "Write navigation item model", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "NavigationItem.cs")), "NavigationItem.cs exists.", NamedWriteRequest("register_navigation", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "NavigationItem.cs")), BuildNavigationItemCs(projectName), workItem.Title)));
                items.Add(DotnetTool("3", "write_shell_registration", "Write shell navigation registry", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "ShellNavigationRegistry.cs")), "ShellNavigationRegistry.cs exists.", NamedWriteRequest("register_navigation", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "ShellNavigationRegistry.cs")), BuildShellNavigationRegistryCs(projectName), workItem.Title)));
                items.Add(DotnetTool("4", "write_app_state", "Write app state model", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "AppState.cs")), "AppState.cs exists.", NamedWriteRequest("create_dotnet_viewmodel", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "AppState.cs")), BuildAppStateCs(projectName), workItem.Title, MergeArguments(
                    BuildCodeIntentArguments("state", "model", projectName, $"{projectName}.State", "integrated", solutionPath, "viewmodel_required", "verification_required"),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dependency_prerequisites"] = "NavigationItem,ShellNavigationRegistry",
                        ["dependency_ordering_status"] = "satisfied_prerequisites",
                        ["dependency_ordering_summary"] = "AppState depends on navigation registration artifacts and is scheduled after them."
                    }))));
                items.Add(DotnetTool("5", "write_shell_viewmodel", "Write shell viewmodel", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "ShellViewModel.cs")), "ShellViewModel.cs exists.", NamedWriteRequest("create_dotnet_viewmodel", NormalizeRelativePath(Path.Combine(projectDirectory, "State", "ShellViewModel.cs")), BuildShellViewModelCs(projectName), workItem.Title, MergeArguments(
                    BuildCodeIntentArguments("state", "viewmodel", projectName, $"{projectName}.State", "integrated", solutionPath, "binding_required", "verification_required"),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dependency_prerequisites"] = "AppState",
                        ["dependency_ordering_status"] = "defer_until_prerequisite_ready",
                        ["dependency_ordering_summary"] = "ShellViewModel requires AppState and should only be written after the state model exists."
                    }))));
                break;

            case "setup_storage_layer":
                if (maintenanceBaseline.IsMaintenanceMode
                    && !EnsureMaintenanceTargetResolved(
                        maintenanceBaseline,
                        FirstNonEmpty(storageProjectDirectory, storageProjectPath),
                        "Storage authority surface",
                        activeImport,
                        batch,
                        workItem,
                        phraseFamilyResolution,
                        buildProfile,
                        out var maintenanceStorageBlocked))
                {
                    return maintenanceStorageBlocked;
                }

                items.Add(DotnetTool("1", "make_storage_dir", "Create storage folder", storageProjectDirectory, "Storage folder exists.", MakeDirRequest(storageProjectDirectory, workItem.Title)));
                items.Add(DotnetTool("2", "write_storage_contract", "Write storage contract", NormalizeRelativePath(Path.Combine(storageProjectDirectory, "ISettingsStore.cs")), "ISettingsStore.cs exists.", NamedWriteRequest("initialize_sqlite_storage_boundary", NormalizeRelativePath(Path.Combine(storageProjectDirectory, "ISettingsStore.cs")), BuildISettingsStoreCs(storageProjectName), workItem.Title, BuildCodeIntentArguments("contracts", "interface", storageProjectName, $"{storageProjectName}.Storage", "structural", solutionPath, "implementation_required", "verification_required"))));
                items.Add(DotnetTool("3", "write_storage_impl", "Write file settings store", NormalizeRelativePath(Path.Combine(storageProjectDirectory, "FileSettingsStore.cs")), "FileSettingsStore.cs exists.", NamedWriteRequest("register_di_service", NormalizeRelativePath(Path.Combine(storageProjectDirectory, "FileSettingsStore.cs")), BuildFileSettingsStoreCs(storageProjectName), workItem.Title, BuildCodeIntentArguments("storage", "repository", storageProjectName, $"{storageProjectName}.Storage", "integrated", solutionPath, "consumer_required", "registration_required", "verification_required"))));
                items.Add(DotnetTool("4", "write_repository_contract", "Write snapshot repository contract", NormalizeRelativePath(Path.Combine(storageProjectDirectory, "ISnapshotRepository.cs")), "ISnapshotRepository.cs exists.", NamedWriteRequest("write_file", NormalizeRelativePath(Path.Combine(storageProjectDirectory, "ISnapshotRepository.cs")), BuildSnapshotRepositoryContractCs(storageProjectName), workItem.Title, BuildCodeIntentArguments("contracts", "interface", storageProjectName, $"{storageProjectName}.Storage", "structural", solutionPath, "implementation_required", "verification_required"))));
                items.Add(DotnetTool("5", "write_repository_impl", "Write snapshot repository implementation", NormalizeRelativePath(Path.Combine(storageProjectDirectory, "SqliteSnapshotRepository.cs")), "SqliteSnapshotRepository.cs exists.", NamedWriteRequest("write_file", NormalizeRelativePath(Path.Combine(storageProjectDirectory, "SqliteSnapshotRepository.cs")), BuildSnapshotRepositoryImplCs(storageProjectName), workItem.Title, BuildCodeIntentArguments("storage", "repository", storageProjectName, $"{storageProjectName}.Storage", "integrated", solutionPath, "consumer_required", "registration_required", "verification_required"))));
                break;

            case "add_settings_page":
                items.Add(DotnetPage("SettingsPage", "Settings", "1"));
                items.Add(DotnetTool("2", "build_solution", "Validate settings page build", solutionPath, "The .NET solution builds successfully.", new ToolRequest
                {
                    ToolName = "dotnet_build",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded page implementation verification step.",
                    Arguments =
                    {
                        ["project"] = solutionPath
                    }
                }));
                break;

            case "add_history_log_view":
                items.Add(DotnetPage("HistoryPage", "History / Log", "1"));
                items.Add(DotnetTool("2", "build_solution", "Validate history page build", solutionPath, "The .NET solution builds successfully.", new ToolRequest
                {
                    ToolName = "dotnet_build",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded page implementation verification step.",
                    Arguments =
                    {
                        ["project"] = solutionPath
                    }
                }));
                break;

            case "wire_dashboard":
                items.Add(DotnetPage("DashboardPage", "Dashboard", "1"));
                items.Add(DotnetTool("2", "build_solution", "Validate dashboard page build", solutionPath, "The .NET solution builds successfully.", new ToolRequest
                {
                    ToolName = "dotnet_build",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded page implementation verification step.",
                    Arguments =
                    {
                        ["project"] = solutionPath
                    }
                }));
                break;

            case "check_runner":
                if (maintenanceBaseline.IsMaintenanceMode
                    && !EnsureMaintenanceTargetResolved(
                        maintenanceBaseline,
                        testsProjectPath,
                        "Tests project",
                        activeImport,
                        batch,
                        workItem,
                        phraseFamilyResolution,
                        buildProfile,
                        out var maintenanceTestsBlocked))
                {
                    return maintenanceTestsBlocked;
                }

                if (!maintenanceBaseline.IsMaintenanceMode)
                {
                    items.Add(DotnetTool("1", "create_test_project", "Create test project", testsProjectPath, "Test project exists.", new ToolRequest
                    {
                        ToolName = "create_dotnet_project",
                        Reason = $"Decomposed `{workItem.Title}` into a deterministic test-project scaffold.",
                        Arguments = BuildProjectScaffoldArguments(
                            "xunit",
                            testsProjectName,
                            testsProjectDirectory,
                            solutionPath,
                            "tests",
                            attach: true,
                            validationTarget: solutionPath)
                    }));
                    items.Add(DotnetTool("2", "attach_test_project", "Add test project to solution", solutionPath, "Solution references the test project.", new ToolRequest
                    {
                        ToolName = "add_project_to_solution",
                        Reason = $"Decomposed `{workItem.Title}` into a deterministic test-project attach step.",
                        Arguments =
                        {
                            ["solution_path"] = solutionPath,
                            ["project_path"] = testsProjectPath
                        }
                    }));
                }
                items.Add(DotnetTool(maintenanceBaseline.IsMaintenanceMode ? "1" : "3", "write_check_registry", "Write check registry", NormalizeRelativePath(Path.Combine(testsProjectDirectory, "CheckRegistry.cs")), "CheckRegistry.cs exists.", NamedWriteRequest("write_file", NormalizeRelativePath(Path.Combine(testsProjectDirectory, "CheckRegistry.cs")), BuildCheckRegistryCs(appName), workItem.Title, BuildCodeIntentArguments("tests", "test_harness", testsProjectName, $"{appName}.Tests", "strong", testsProjectPath, "verification_required"))));
                items.Add(DotnetTool(maintenanceBaseline.IsMaintenanceMode ? "2" : "4", "write_snapshot_builder", "Write snapshot builder", NormalizeRelativePath(Path.Combine(testsProjectDirectory, "SnapshotBuilder.cs")), "SnapshotBuilder.cs exists.", NamedWriteRequest("write_file", NormalizeRelativePath(Path.Combine(testsProjectDirectory, "SnapshotBuilder.cs")), BuildSnapshotBuilderCs(appName), workItem.Title, BuildCodeIntentArguments("tests", "test_harness", testsProjectName, $"{appName}.Tests", "strong", testsProjectPath, "verification_required"))));
                items.Add(DotnetTool(maintenanceBaseline.IsMaintenanceMode ? "3" : "5", "write_findings_normalizer", "Write findings normalizer", NormalizeRelativePath(Path.Combine(testsProjectDirectory, "FindingsNormalizer.cs")), "FindingsNormalizer.cs exists.", NamedWriteRequest("write_file", NormalizeRelativePath(Path.Combine(testsProjectDirectory, "FindingsNormalizer.cs")), BuildFindingsNormalizerCs(appName), workItem.Title, BuildCodeIntentArguments("tests", "test_harness", testsProjectName, $"{appName}.Tests", "strong", testsProjectPath, "verification_required"))));
                items.Add(DotnetTool(maintenanceBaseline.IsMaintenanceMode ? "4" : "6", "run_test_project", "Run test project", testsProjectPath, "The test project runs successfully.", new ToolRequest
                {
                    ToolName = "dotnet_test",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded test validation step.",
                    Arguments =
                    {
                        ["project"] = testsProjectPath
                    }
                }));
                break;

            case "findings_pipeline":
                if (maintenanceBaseline.IsMaintenanceMode
                    && !EnsureMaintenanceTargetResolved(
                        maintenanceBaseline,
                        testsProjectPath,
                        "Tests project",
                        activeImport,
                        batch,
                        workItem,
                        phraseFamilyResolution,
                        buildProfile,
                        out maintenanceTestsBlocked))
                {
                    return maintenanceTestsBlocked;
                }

                items.Add(DotnetTool("1", "write_check_registry", "Write check registry", NormalizeRelativePath(Path.Combine(testsProjectDirectory, "CheckRegistry.cs")), "CheckRegistry.cs exists.", NamedWriteRequest("write_file", NormalizeRelativePath(Path.Combine(testsProjectDirectory, "CheckRegistry.cs")), BuildCheckRegistryCs(appName), workItem.Title, BuildCodeIntentArguments("tests", "test_harness", testsProjectName, $"{appName}.Tests", "strong", testsProjectPath, "verification_required"))));
                items.Add(DotnetTool("2", "write_snapshot_builder", "Write snapshot builder", NormalizeRelativePath(Path.Combine(testsProjectDirectory, "SnapshotBuilder.cs")), "SnapshotBuilder.cs exists.", NamedWriteRequest("write_file", NormalizeRelativePath(Path.Combine(testsProjectDirectory, "SnapshotBuilder.cs")), BuildSnapshotBuilderCs(appName), workItem.Title, BuildCodeIntentArguments("tests", "test_harness", testsProjectName, $"{appName}.Tests", "strong", testsProjectPath, "verification_required"))));
                items.Add(DotnetTool("3", "write_findings_normalizer", "Write findings normalizer", NormalizeRelativePath(Path.Combine(testsProjectDirectory, "FindingsNormalizer.cs")), "FindingsNormalizer.cs exists.", NamedWriteRequest("write_file", NormalizeRelativePath(Path.Combine(testsProjectDirectory, "FindingsNormalizer.cs")), BuildFindingsNormalizerCs(appName), workItem.Title, BuildCodeIntentArguments("tests", "test_harness", testsProjectName, $"{appName}.Tests", "strong", testsProjectPath, "verification_required"))));
                break;

            case "build_verify":
                if (maintenanceBaseline.IsMaintenanceMode && !maintenanceBaseline.BaselineResolved)
                {
                    return BuildBlocked(
                        activeImport,
                        batch,
                        workItem,
                        phraseFamilyResolution,
                        buildProfile,
                        BuildMaintenanceBuildBlockedReason(workItem.Title, maintenanceBaseline));
                }

                if (string.IsNullOrWhiteSpace(buildVerificationTargetPath))
                {
                    var bootstrapPrerequisite = ResolveDotnetBuildBootstrapPrerequisite(
                        workspaceRoot,
                        activeImport,
                        activeDocument,
                        batch,
                        workItem);
                    if (bootstrapPrerequisite is not null)
                    {
                        items.Add(CreateToolItem(
                            workItem,
                            "1",
                            "create_solution",
                            buildProfile.StackFamily,
                            "solution_scaffold",
                            "dotnet.solution_scaffold.v1",
                            ["dotnet.solution_scaffold.v1"],
                            "Create app solution",
                            solutionPath,
                            "Workspace solution exists.",
                            new ToolRequest
                            {
                                ToolName = "create_dotnet_solution",
                                Reason = $"Bootstrapped `{workItem.Title}` from authoritative imported-plan scaffold lineage before workspace build verification.",
                                Arguments =
                                {
                                    ["solution_name"] = appName
                                }
                            }));

                        items.Add(CreateToolItem(
                            workItem,
                            "2",
                            "create_project",
                            buildProfile.StackFamily,
                            "solution_scaffold",
                            "dotnet.solution_scaffold.v1",
                            ["dotnet.solution_scaffold.v1"],
                            BuildPrimaryProjectCreateDescription(projectName, requestedProjectTemplate),
                            projectPath,
                            $"{projectName}.csproj exists.",
                            new ToolRequest
                            {
                                ToolName = "create_dotnet_project",
                                Reason = $"Bootstrapped `{workItem.Title}` from authoritative imported-plan scaffold lineage before workspace build verification.",
                                Arguments = BuildProjectScaffoldArguments(
                                    requestedProjectTemplate,
                                    projectName,
                                    projectDirectory,
                                    solutionPath,
                                    requestedProjectRole,
                                    attach: true,
                                    validationTarget: solutionPath)
                            }));

                        items.Add(CreateToolItem(
                            workItem,
                            "3",
                            "add_project_to_solution",
                            buildProfile.StackFamily,
                            "solution_scaffold",
                            "dotnet.solution_scaffold.v1",
                            ["dotnet.solution_scaffold.v1"],
                            "Add app project to solution",
                            solutionPath,
                            "Solution references the app project.",
                            new ToolRequest
                            {
                                ToolName = "add_project_to_solution",
                                Reason = $"Bootstrapped `{workItem.Title}` from authoritative imported-plan scaffold lineage before workspace build verification.",
                                Arguments =
                                {
                                    ["solution_path"] = solutionPath,
                                    ["project_path"] = projectPath
                                }
                            }));

                        items.Add(DotnetTool(
                            "4",
                            "build_solution",
                            "Run workspace build verification",
                            solutionPath,
                            "The .NET solution builds successfully.",
                            new ToolRequest
                            {
                                ToolName = "dotnet_build",
                                Reason = $"Decomposed `{workItem.Title}` into bounded workspace build verification after prerequisite scaffold bootstrap.",
                                Arguments =
                                {
                                    ["project"] = solutionPath
                                }
                            }));

                        var bootstrapRecord = BuildDecomposed(
                            activeImport,
                            batch,
                            workItem,
                            phraseFamilyResolution,
                            buildProfile,
                            items,
                            templateId,
                            candidateTemplateIds,
                            templateSelection.TraceId,
                            templateSelection.Reason);
                        bootstrapRecord.Reason =
                            $"Taskboard auto-run decomposed `{workItem.Title}` into prerequisite workspace scaffold and build verification because imported-plan lineage identified `{bootstrapPrerequisite.WorkItemTitle}` in Batch {bootstrapPrerequisite.BatchNumber} as the target-producing prerequisite for the missing .NET build target.";
                        return bootstrapRecord;
                    }

                    return BuildBlocked(
                        activeImport,
                        batch,
                        workItem,
                        phraseFamilyResolution,
                        buildProfile,
                        $"Taskboard auto-run paused: `{workItem.Title}` resolved to build verification work, but no deterministic .NET build target exists yet and no authoritative target-producing prerequisite could be identified from the imported plan.",
                        candidateTemplateIds,
                        templateSelection.TraceId,
                        templateSelection.Reason);
                }

                items.Add(DotnetTool("1", "build_solution", "Run workspace build verification", buildVerificationTargetPath, "The .NET solution builds successfully.", new ToolRequest
                {
                    ToolName = "dotnet_build",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded workspace build verification step.",
                    Arguments =
                    {
                        ["project"] = buildVerificationTargetPath
                    }
                }));
                break;

            case "maintenance_context":
                if (ramDbService is not null
                    && TryBuildActiveFailureRepairDecomposition(
                        workspaceRoot,
                        activeImport,
                        batch,
                        workItem,
                        phraseFamilyResolution,
                        buildProfile,
                        activeRunState,
                        ramDbService,
                        out var repairDecomposition))
                {
                    return repairDecomposition;
                }

                items.Add(DotnetTool("1", "inspect_context_artifacts", "Inspect maintenance context artifacts", ".ram", "Recent run summaries, normalized records, and repair context artifacts were inspected.", new ToolRequest
                {
                    ToolName = "show_artifacts",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded maintenance-context artifact inspection step."
                }));
                break;

            case "build_failure_repair":
            case "solution_graph_repair":
                if (ramDbService is not null
                    && TryBuildActiveFailureRepairDecomposition(
                        workspaceRoot,
                        activeImport,
                        batch,
                        workItem,
                        phraseFamilyResolution,
                        buildProfile,
                        activeRunState,
                        ramDbService,
                        out var activeFailureRepair))
                {
                    return activeFailureRepair;
                }

                if (TryBuildExistingRepairDecomposition(
                        activeImport,
                        batch,
                        workItem,
                        phraseFamilyResolution,
                        buildProfile,
                        out var existingRepair))
                {
                    return existingRepair;
                }

                return BuildBlocked(
                    activeImport,
                    batch,
                    workItem,
                    phraseFamilyResolution,
                    buildProfile,
                    $"Taskboard auto-run paused: `{workItem.Title}` resolved to `{phraseFamily}`, but RAM could not recover a bounded repair target from the recorded workspace failure context.",
                    candidateTemplateIds,
                    templateSelection.TraceId,
                    templateSelection.Reason);

            default:
                return BuildBlocked(activeImport, batch, workItem, phraseFamilyResolution, buildProfile, $"Taskboard auto-run paused: no decomposition template exists yet for phrase family `{phraseFamily}` on stack `{FormatStackFamily(buildProfile.StackFamily)}`.", candidateTemplateIds, templateSelection.TraceId, templateSelection.Reason);
        }

        if (TryBuildSatisfiedSupportActionableCoverage(
                workspaceRoot,
                activeImport,
                batch,
                workItem,
                headingPolicy,
                supportActionablePhraseFamily,
                phraseFamilyResolution,
                buildProfile,
                items,
                activeRunState,
                ramDbService,
                out var coveredSupport))
        {
            return coveredSupport;
        }

        return BuildDecomposed(activeImport, batch, workItem, phraseFamilyResolution, buildProfile, items, templateId, candidateTemplateIds, templateSelection.TraceId, templateSelection.Reason);
    }

    private bool TryBuildSatisfiedSupportActionableCoverage(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardBatch batch,
        TaskboardRunWorkItem workItem,
        TaskboardHeadingPolicyRecord headingPolicy,
        string supportActionablePhraseFamily,
        PhraseFamilyResolution phraseFamilyResolution,
        TaskboardBuildProfileResolutionRecord buildProfile,
        List<TaskboardDecomposedWorkItem> items,
        TaskboardPlanRunStateRecord? activeRunState,
        RamDbService? ramDbService,
        out TaskboardWorkItemDecompositionRecord decomposition)
    {
        decomposition = new TaskboardWorkItemDecompositionRecord();
        if (!TaskboardStructuralHeadingService.IsNonActionableHeading(headingPolicy)
            || string.IsNullOrWhiteSpace(supportActionablePhraseFamily)
            || activeRunState is null
            || ramDbService is null
            || items.Count == 0)
        {
            return false;
        }

        var satisfiedSteps = new List<TaskboardStateSatisfactionResultRecord>();
        foreach (var item in items)
        {
            if (item.ToolRequest is null)
                return false;

            var satisfaction = _stateSatisfactionService.EvaluatePlannedStep(
                workspaceRoot,
                activeRunState,
                item.ParentWorkItemId,
                workItem.Title,
                item.WorkFamily,
                item.ToolRequest,
                ResolvePlannedSupportTargetPath(item),
                ramDbService,
                allowNoSkipReuse: true);
            if (!satisfaction.Satisfied)
                return false;

            satisfiedSteps.Add(satisfaction);
        }

        var repeatedTouchesAvoided = satisfiedSteps.Sum(current => current.RepeatedTouchesAvoidedCount);
        decomposition = BuildCoveredSupport(
            activeImport,
            batch,
            workItem,
            phraseFamilyResolution,
            buildProfile,
            TaskboardStructuralHeadingService.BuildSupportCoverageReason(headingPolicy)
            + Environment.NewLine
            + $"suppressed_redundant_shallow_cycle: Reused previously satisfied `{supportActionablePhraseFamily}` follow-up because all {satisfiedSteps.Count} generated step(s) were already satisfied and no later invalidating mutation required another shallow validation cycle. repeated_touches_avoided={repeatedTouchesAvoided}.");
        return true;
    }

    private bool TryBuildActiveFailureRepairDecomposition(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardBatch batch,
        TaskboardRunWorkItem workItem,
        PhraseFamilyResolution phraseFamilyResolution,
        TaskboardBuildProfileResolutionRecord buildProfile,
        TaskboardPlanRunStateRecord? activeRunState,
        RamDbService ramDbService,
        out TaskboardWorkItemDecompositionRecord decomposition)
    {
        var classification = _buildFailureRecoveryService.TryClassifyActiveFailure(
            workspaceRoot,
            activeRunState,
            workItem,
            ramDbService);
        if (!classification.IsActionable)
        {
            decomposition = new TaskboardWorkItemDecompositionRecord();
            return false;
        }

        var targetPath = NormalizeRelativePath(classification.TargetPath);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            decomposition = new TaskboardWorkItemDecompositionRecord();
            return false;
        }

        var failureIdentity = FirstNonEmpty(classification.ErrorCode, classification.FailureFamily, "build_failure");
        var validationHint = string.IsNullOrWhiteSpace(classification.NormalizedSummary)
            ? $"Use recorded build failure evidence to repair `{targetPath}` and rerun bounded workspace verification."
            : $"Use recorded build failure evidence `{failureIdentity}` to repair `{targetPath}` and rerun bounded workspace verification: {classification.NormalizedSummary}";
        var subItem = CreateToolItem(
            workItem,
            "1",
            classification.OperationKind,
            buildProfile.StackFamily,
            classification.PhraseFamily,
            classification.TemplateId,
            [classification.TemplateId],
            $"Plan bounded repair for {targetPath}",
            targetPath,
            validationHint,
            new ToolRequest
            {
                ToolName = "plan_repair",
                PreferredChainTemplateName = classification.TemplateId,
                Reason = $"Decomposed `{workItem.Title}` into bounded repair because active build failure evidence already identified `{failureIdentity}` on `{targetPath}`.",
                ExecutionSourceType = ExecutionSourceType.BuildTool,
                ExecutionSourceName = "taskboard_active_failure_repair",
                IsAutomaticTrigger = true,
                ExecutionAllowed = true,
                ExecutionPolicyMode = "taskboard_auto_run",
                ExecutionBuildFamily = "repair",
                Arguments =
                {
                    ["scope"] = "build",
                    ["path"] = targetPath
                }
            });

        decomposition = BuildDecomposed(
            activeImport,
            batch,
            workItem,
            classification.PhraseFamily,
            buildProfile,
            [subItem],
            phraseFamilyResolution.Confidence,
            phraseFamilyResolution.TraceId,
            classification.TemplateId,
            [classification.TemplateId],
            "active_failure_repair",
            $"Active build failure evidence promoted maintenance-context work item `{workItem.Title}` into bounded repair.");
        ApplyPhraseFamilyResolution(decomposition, phraseFamilyResolution);
        decomposition.Reason =
            $"Taskboard auto-run decomposed `{workItem.Title}` into bounded repair because active build failure evidence already identified `{failureIdentity}` on `{targetPath}`. This avoided additional artifact-only maintenance churn.";
        return true;
    }

    private bool TryBuildExistingRepairDecomposition(
        TaskboardImportRecord activeImport,
        TaskboardBatch batch,
        TaskboardRunWorkItem workItem,
        PhraseFamilyResolution phraseFamilyResolution,
        TaskboardBuildProfileResolutionRecord buildProfile,
        out TaskboardWorkItemDecompositionRecord decomposition)
    {
        var request = workItem.DirectToolRequest?.Clone();
        if (!string.Equals((request?.ToolName ?? "").Trim(), "plan_repair", StringComparison.OrdinalIgnoreCase))
        {
            decomposition = new TaskboardWorkItemDecompositionRecord();
            return false;
        }

        var targetPath = NormalizeRelativePath(FirstNonEmpty(
            request!.Arguments.TryGetValue("path", out var explicitPath) ? explicitPath : "",
            workItem.ExpectedArtifact));
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            decomposition = new TaskboardWorkItemDecompositionRecord();
            return false;
        }

        request.PreferredChainTemplateName = FirstNonEmpty(request.PreferredChainTemplateName, "repair_execution_chain");
        request.ExecutionSourceType = request.ExecutionSourceType == ExecutionSourceType.Unknown
            ? ExecutionSourceType.BuildTool
            : request.ExecutionSourceType;
        request.ExecutionSourceName = FirstNonEmpty(request.ExecutionSourceName, "taskboard_repair_work_item");
        request.ExecutionPolicyMode = FirstNonEmpty(request.ExecutionPolicyMode, "taskboard_auto_run");
        request.ExecutionBuildFamily = FirstNonEmpty(request.ExecutionBuildFamily, "repair");
        request.IsAutomaticTrigger = true;
        request.ExecutionAllowed = true;
        request.Arguments["scope"] = FirstNonEmpty(request.Arguments.TryGetValue("scope", out var scope) ? scope : "", "build");
        request.Arguments["path"] = targetPath;

        var operationKind = FirstNonEmpty(workItem.OperationKind, "inspect_solution_wiring");
        var subItem = CreateToolItem(
            workItem,
            "1",
            operationKind,
            buildProfile.StackFamily,
            FirstNonEmpty(workItem.PhraseFamily, phraseFamilyResolution.PhraseFamily, "build_failure_repair"),
            "repair_execution_chain",
            ["repair_execution_chain"],
            $"Plan bounded repair for {targetPath}",
            targetPath,
            FirstNonEmpty(
                workItem.ValidationHint,
                $"Use the recorded build-failure context for `{targetPath}` to plan a bounded repair and rerun deterministic verification."),
            request);

        decomposition = BuildDecomposed(
            activeImport,
            batch,
            workItem,
            phraseFamilyResolution.PhraseFamily,
            buildProfile,
            [subItem],
            phraseFamilyResolution.Confidence,
            phraseFamilyResolution.TraceId,
            "repair_execution_chain",
            ["repair_execution_chain"],
            "existing_repair_work_item",
            $"Recovered the existing bounded repair work item `{workItem.Title}` into repair execution.");
        ApplyPhraseFamilyResolution(decomposition, phraseFamilyResolution);
        decomposition.Reason =
            $"Taskboard auto-run decomposed `{workItem.Title}` into bounded repair using the existing repair work item context for `{targetPath}`.";
        return true;
    }

    private static string ResolvePlannedSupportTargetPath(TaskboardDecomposedWorkItem item)
    {
        if (item.ToolRequest is not null)
        {
            if (item.ToolRequest.TryGetArgument("project", out var project) && !string.IsNullOrWhiteSpace(project))
                return NormalizeRelativePath(project);
            if (item.ToolRequest.TryGetArgument("path", out var path) && !string.IsNullOrWhiteSpace(path))
                return NormalizeRelativePath(path);
            if (item.ToolRequest.TryGetArgument("project_path", out var projectPath) && !string.IsNullOrWhiteSpace(projectPath))
                return NormalizeRelativePath(projectPath);
        }

        return NormalizeRelativePath(item.ExpectedArtifact);
    }

    private TaskboardWorkItemDecompositionRecord BuildNativeCppDecomposition(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardDocument activeDocument,
        TaskboardBatch batch,
        TaskboardRunWorkItem workItem,
        PhraseFamilyResolution phraseFamilyResolution,
        TemplateSelection templateSelection,
        TaskboardBuildProfileResolutionRecord buildProfile)
    {
        var phraseFamily = phraseFamilyResolution.PhraseFamily;
        var templateId = templateSelection.TemplateId;
        var candidateTemplateIds = templateSelection.CandidateTemplateIds;
        var appName = DeriveAppName(activeDocument.Title, workspaceRoot);
        var items = new List<TaskboardDecomposedWorkItem>();
        TaskboardDecomposedWorkItem NativeTool(string subOrdinal, string operationKind, string description, string expectedArtifact, string validationHint, ToolRequest toolRequest)
            => CreateToolItem(workItem, subOrdinal, operationKind, buildProfile.StackFamily, phraseFamily, templateId, candidateTemplateIds, description, expectedArtifact, validationHint, toolRequest);

        switch (phraseFamily)
        {
            case "solution_scaffold":
            case "project_scaffold":
            case "native_project_bootstrap":
            case "cmake_bootstrap":
                items.Add(NativeTool("1", "make_src_dir", "Create src folder", "src", "src folder exists.", MakeDirRequest("src", workItem.Title)));
                items.Add(NativeTool("2", "make_include_dir", "Create include folder", "include", "include folder exists.", MakeDirRequest("include", workItem.Title)));
                items.Add(NativeTool("3", "write_cmake_lists", "Write CMake project file", "CMakeLists.txt", "CMakeLists.txt exists.", NamedWriteRequest("create_cmake_project", "CMakeLists.txt", BuildNativeCMakeLists(appName), workItem.Title)));
                items.Add(NativeTool("4", "configure_cmake", "Configure native build", "build/CMakeCache.txt", "CMake configure completes successfully.", new ToolRequest
                {
                    ToolName = "cmake_configure",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded native configure step.",
                    Arguments =
                    {
                        ["source_dir"] = ".",
                        ["build_dir"] = "build"
                    }
                }));
                break;

            case "build_first_ui_shell":
                items.Add(NativeTool("1", "make_src_dir", "Create src folder", "src", "src folder exists.", MakeDirRequest("src", workItem.Title)));
                items.Add(NativeTool("2", "make_include_dir", "Create include folder", "include", "include folder exists.", MakeDirRequest("include", workItem.Title)));
                items.Add(NativeTool("3", "write_cmake_lists", "Write CMake project file", "CMakeLists.txt", "CMakeLists.txt exists.", NamedWriteRequest("create_cmake_project", "CMakeLists.txt", BuildNativeCMakeLists(appName), workItem.Title)));
                items.Add(NativeTool("4", "write_app_window_header", "Write app window header", "include/AppWindow.h", "AppWindow.h exists.", NamedWriteRequest("create_cpp_header_file", "include/AppWindow.h", BuildNativeAppWindowHeader(), workItem.Title)));
                items.Add(NativeTool("5", "write_app_window_source", "Write app window source", "src/AppWindow.cpp", "AppWindow.cpp exists.", NamedWriteRequest("create_cpp_source_file", "src/AppWindow.cpp", BuildNativeAppWindowSource(appName), workItem.Title)));
                items.Add(NativeTool("6", "write_main_cpp", "Write Win32 entry point", "src/main.cpp", "main.cpp exists.", NamedWriteRequest("create_cpp_source_file", "src/main.cpp", BuildNativeMainCpp(appName), workItem.Title)));
                items.Add(NativeTool("7", "configure_cmake", "Configure native build", "build/CMakeCache.txt", "CMake configure completes successfully.", new ToolRequest
                {
                    ToolName = "cmake_configure",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded native configure step.",
                    Arguments =
                    {
                        ["source_dir"] = ".",
                        ["build_dir"] = "build"
                    }
                }));
                items.Add(NativeTool("8", "build_native_workspace", "Validate native build", "build", "The native workspace builds successfully.", new ToolRequest
                {
                    ToolName = "cmake_build",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded native build validation step.",
                    Arguments =
                    {
                        ["build_dir"] = "build"
                    }
                }));
                break;

            case "ui_shell_sections":
                items.Add(NativeTool("1", "make_include_dir", "Create include folder", "include", "include folder exists.", MakeDirRequest("include", workItem.Title)));
                items.Add(NativeTool("2", "make_state_dir", "Create state folder", "include/State", "State folder exists.", MakeDirRequest("include/State", workItem.Title)));
                items.Add(NativeTool("3", "write_dashboard_panel", "Write dashboard panel header", "include/DashboardPanel.h", "DashboardPanel.h exists.", NamedWriteRequest("create_cpp_header_file", "include/DashboardPanel.h", BuildNativePanelHeader("DashboardPanel"), workItem.Title)));
                items.Add(NativeTool("4", "write_findings_panel", "Write findings panel header", "include/FindingsPanel.h", "FindingsPanel.h exists.", NamedWriteRequest("create_cpp_header_file", "include/FindingsPanel.h", BuildNativePanelHeader("FindingsPanel"), workItem.Title)));
                items.Add(NativeTool("5", "write_history_panel", "Write history panel header", "include/HistoryPanel.h", "HistoryPanel.h exists.", NamedWriteRequest("create_cpp_header_file", "include/HistoryPanel.h", BuildNativePanelHeader("HistoryPanel"), workItem.Title)));
                items.Add(NativeTool("6", "write_settings_panel", "Write settings panel header", "include/SettingsPanel.h", "SettingsPanel.h exists.", NamedWriteRequest("create_cpp_header_file", "include/SettingsPanel.h", BuildNativePanelHeader("SettingsPanel"), workItem.Title)));
                items.Add(NativeTool("7", "write_navigation_header", "Write navigation header", "include/State/NavigationItem.h", "NavigationItem.h exists.", NamedWriteRequest("create_cpp_header_file", "include/State/NavigationItem.h", BuildNativeNavigationHeader(), workItem.Title)));
                items.Add(NativeTool("8", "configure_cmake", "Configure native build", "build/CMakeCache.txt", "CMake configure completes successfully.", new ToolRequest
                {
                    ToolName = "cmake_configure",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded native configure step.",
                    Arguments =
                    {
                        ["source_dir"] = ".",
                        ["build_dir"] = "build"
                    }
                }));
                items.Add(NativeTool("9", "build_native_workspace", "Validate grouped shell build", "build", "The native workspace builds successfully.", new ToolRequest
                {
                    ToolName = "cmake_build",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded grouped shell build validation step.",
                    Arguments =
                    {
                        ["build_dir"] = "build"
                    }
                }));
                break;

            case "core_domain_models_contracts":
            case "repository_scaffold":
                items.Add(NativeTool("1", "make_contracts_dir", "Create contracts folder", "include/Contracts", "Contracts folder exists.", MakeDirRequest("include/Contracts", workItem.Title)));
                items.Add(NativeTool("2", "make_models_dir", "Create models folder", "include/Models", "Models folder exists.", MakeDirRequest("include/Models", workItem.Title)));
                items.Add(NativeTool("3", "write_contract_header", "Write check contract header", "include/Contracts/CheckDefinition.h", "CheckDefinition.h exists.", NamedWriteRequest("create_cpp_header_file", "include/Contracts/CheckDefinition.h", BuildNativeCheckDefinitionHeader(), workItem.Title)));
                items.Add(NativeTool("4", "write_domain_model_header", "Write finding model header", "include/Models/FindingRecord.h", "FindingRecord.h exists.", NamedWriteRequest("create_cpp_header_file", "include/Models/FindingRecord.h", BuildNativeFindingRecordHeader(), workItem.Title)));
                break;

            case "add_navigation_app_state":
                items.Add(NativeTool("1", "make_state_dir", "Create state folder", "include/State", "State folder exists.", MakeDirRequest("include/State", workItem.Title)));
                items.Add(NativeTool("2", "write_navigation_header", "Write navigation header", "include/State/NavigationItem.h", "NavigationItem.h exists.", NamedWriteRequest("create_cpp_header_file", "include/State/NavigationItem.h", BuildNativeNavigationHeader(), workItem.Title)));
                items.Add(NativeTool("3", "write_app_state_header", "Write app state header", "include/State/AppState.h", "AppState.h exists.", NamedWriteRequest("create_cpp_header_file", "include/State/AppState.h", BuildNativeAppStateHeader(), workItem.Title)));
                break;

            case "setup_storage_layer":
                items.Add(NativeTool("1", "make_storage_dir", "Create storage folder", "include/Storage", "Storage folder exists.", MakeDirRequest("include/Storage", workItem.Title)));
                items.Add(NativeTool("2", "write_storage_header", "Write storage header", "include/Storage/SettingsStore.h", "SettingsStore.h exists.", NamedWriteRequest("create_cpp_header_file", "include/Storage/SettingsStore.h", BuildNativeSettingsStoreHeader(), workItem.Title)));
                items.Add(NativeTool("3", "write_storage_source", "Write storage source", "src/SettingsStore.cpp", "SettingsStore.cpp exists.", NamedWriteRequest("create_cpp_source_file", "src/SettingsStore.cpp", BuildNativeSettingsStoreSource(), workItem.Title)));
                break;

            case "add_settings_page":
                items.Add(NativeTool("1", "write_settings_panel", "Write settings panel header", "include/SettingsPanel.h", "SettingsPanel.h exists.", NamedWriteRequest("create_cpp_header_file", "include/SettingsPanel.h", BuildNativePanelHeader("SettingsPanel"), workItem.Title)));
                break;

            case "add_history_log_view":
                items.Add(NativeTool("1", "write_history_panel", "Write history panel header", "include/HistoryPanel.h", "HistoryPanel.h exists.", NamedWriteRequest("create_cpp_header_file", "include/HistoryPanel.h", BuildNativePanelHeader("HistoryPanel"), workItem.Title)));
                break;

            case "wire_dashboard":
                items.Add(NativeTool("1", "write_dashboard_panel", "Write dashboard panel header", "include/DashboardPanel.h", "DashboardPanel.h exists.", NamedWriteRequest("create_cpp_header_file", "include/DashboardPanel.h", BuildNativePanelHeader("DashboardPanel"), workItem.Title)));
                break;

            case "build_verify":
                items.Add(NativeTool("1", "configure_cmake", "Configure native build", "build/CMakeCache.txt", "CMake configure completes successfully.", new ToolRequest
                {
                    ToolName = "cmake_configure",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded native configure step.",
                    Arguments =
                    {
                        ["source_dir"] = ".",
                        ["build_dir"] = "build"
                    }
                }));
                items.Add(NativeTool("2", "build_native_workspace", "Run native build verification", "build", "The native workspace builds successfully.", new ToolRequest
                {
                    ToolName = "cmake_build",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded native build verification step.",
                    Arguments =
                    {
                        ["build_dir"] = "build"
                    }
                }));
                break;

            case "check_runner":
            case "findings_pipeline":
                items.Add(NativeTool("1", "write_contract_header", "Write check contract header", "include/Contracts/CheckDefinition.h", "CheckDefinition.h exists.", NamedWriteRequest("create_cpp_header_file", "include/Contracts/CheckDefinition.h", BuildNativeCheckDefinitionHeader(), workItem.Title)));
                items.Add(NativeTool("2", "write_domain_model_header", "Write finding model header", "include/Models/FindingRecord.h", "FindingRecord.h exists.", NamedWriteRequest("create_cpp_header_file", "include/Models/FindingRecord.h", BuildNativeFindingRecordHeader(), workItem.Title)));
                items.Add(NativeTool("3", "build_native_workspace", "Run native findings/check verification", "build", "The native workspace builds successfully.", new ToolRequest
                {
                    ToolName = "cmake_build",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded native findings/check verification step.",
                    Arguments =
                    {
                        ["build_dir"] = "build"
                    }
                }));
                break;

            case "maintenance_context":
                items.Add(NativeTool("1", "inspect_context_artifacts", "Inspect maintenance context artifacts", ".ram", "Recent run summaries, normalized records, and repair context artifacts were inspected.", new ToolRequest
                {
                    ToolName = "show_artifacts",
                    Reason = $"Decomposed `{workItem.Title}` into a bounded maintenance-context artifact inspection step."
                }));
                break;

            default:
                return BuildBlocked(activeImport, batch, workItem, phraseFamilyResolution, buildProfile, $"Taskboard auto-run paused: no decomposition template exists yet for phrase family `{phraseFamily}` on stack `{FormatStackFamily(buildProfile.StackFamily)}`.", candidateTemplateIds, templateSelection.TraceId, templateSelection.Reason);
        }

        return BuildDecomposed(activeImport, batch, workItem, phraseFamilyResolution, buildProfile, items, templateId, candidateTemplateIds, templateSelection.TraceId, templateSelection.Reason);
    }

    private static TaskboardWorkItemDecompositionRecord BuildDecomposed(TaskboardImportRecord activeImport, TaskboardBatch batch, TaskboardRunWorkItem workItem, PhraseFamilyResolution phraseFamilyResolution, TaskboardBuildProfileResolutionRecord buildProfile, List<TaskboardDecomposedWorkItem> items, string templateId, List<string> templateCandidateIds, string templateSelectionTraceId, string templateSelectionReason)
    {
        var record = BuildDecomposed(
            activeImport,
            batch,
            workItem,
            phraseFamilyResolution.PhraseFamily,
            buildProfile,
            items,
            phraseFamilyResolution.Confidence,
            phraseFamilyResolution.TraceId,
            templateId,
            templateCandidateIds,
            templateSelectionTraceId,
            templateSelectionReason);
        ApplyPhraseFamilyResolution(record, phraseFamilyResolution);
        return record;
    }

    private static PhraseFamilyResolution BuildStructuralSupportPhraseFamilyResolution(
        TaskboardImportRecord activeImport,
        TaskboardBatch batch,
        TaskboardRunWorkItem workItem,
        TaskboardHeadingPolicyRecord headingPolicy,
        bool isMaintenanceMode)
    {
        var phraseFamily = isMaintenanceMode ? "maintenance_context" : "structural_support_heading";
        var summary = isMaintenanceMode
            ? $"Heading `{workItem.Title}` class={headingPolicy.HeadingClass} treatment={headingPolicy.Treatment} was folded into the active executable maintenance flow before standalone decomposition."
            : $"Heading `{workItem.Title}` class={headingPolicy.HeadingClass} treatment={headingPolicy.Treatment} was folded into the active executable scaffold/build flow before standalone decomposition.";

        return PhraseFamilyResolution.FromRecord(new TaskboardPhraseFamilyResolutionRecord
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = activeImport.WorkspaceRoot,
            PlanImportId = activeImport.ImportId,
            BatchId = batch.BatchId,
            WorkItemId = workItem.WorkItemId,
            WorkItemTitle = workItem.Title,
            ShouldDecompose = true,
            IsBlocked = false,
            PhraseFamily = phraseFamily,
            Confidence = "deterministic",
            ResolutionSource = TaskboardPhraseFamilyResolutionSource.DeterministicFallback,
            ResolutionSummary = summary,
            CandidatePhraseFamilies = [phraseFamily],
            DeterministicCandidate = phraseFamily,
            DeterministicConfidence = "deterministic",
            DeterministicReason = summary,
            CreatedUtc = DateTime.UtcNow.ToString("O")
        });
    }

    private static PhraseFamilyResolution BuildSupportActionablePhraseFamilyResolution(
        TaskboardImportRecord activeImport,
        TaskboardBatch batch,
        TaskboardRunWorkItem workItem,
        TaskboardHeadingPolicyRecord headingPolicy,
        string phraseFamily)
    {
        var summary = $"Heading `{workItem.Title}` class={headingPolicy.HeadingClass} treatment={headingPolicy.Treatment} was promoted into actionable follow-up phrase family `{phraseFamily}` because the section text still contains bounded executable C# work.";
        return PhraseFamilyResolution.FromRecord(new TaskboardPhraseFamilyResolutionRecord
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = activeImport.WorkspaceRoot,
            PlanImportId = activeImport.ImportId,
            BatchId = batch.BatchId,
            WorkItemId = workItem.WorkItemId,
            WorkItemTitle = workItem.Title,
            ShouldDecompose = true,
            IsBlocked = false,
            PhraseFamily = phraseFamily,
            Confidence = "deterministic",
            ResolutionSource = TaskboardPhraseFamilyResolutionSource.DeterministicFallback,
            ResolutionSummary = summary,
            CandidatePhraseFamilies = [phraseFamily],
            DeterministicCandidate = phraseFamily,
            DeterministicConfidence = "deterministic",
            DeterministicReason = summary,
            CreatedUtc = DateTime.UtcNow.ToString("O")
        });
    }

    private static TaskboardWorkItemDecompositionRecord BuildCoveredSupport(
        TaskboardImportRecord activeImport,
        TaskboardBatch batch,
        TaskboardRunWorkItem workItem,
        PhraseFamilyResolution phraseFamilyResolution,
        TaskboardBuildProfileResolutionRecord buildProfile,
        string reason)
    {
        var record = new TaskboardWorkItemDecompositionRecord
        {
            DecompositionId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = activeImport.WorkspaceRoot,
            PlanImportId = activeImport.ImportId,
            BatchId = batch.BatchId,
            OriginalWorkItemId = workItem.WorkItemId,
            OriginalTitle = workItem.Title,
            PhraseFamily = phraseFamilyResolution.PhraseFamily,
            PhraseFamilyConfidence = phraseFamilyResolution.Confidence,
            PhraseFamilyTraceId = phraseFamilyResolution.TraceId,
            PhraseFamilySource = phraseFamilyResolution.Source,
            PhraseFamilyResolutionSummary = phraseFamilyResolution.ResolutionSummary,
            PhraseFamilyCandidates = [.. phraseFamilyResolution.Candidates],
            PhraseFamilyDeterministicCandidate = phraseFamilyResolution.DeterministicCandidate,
            PhraseFamilyAdvisoryCandidate = phraseFamilyResolution.AdvisoryCandidate,
            PhraseFamilyBlockerCode = phraseFamilyResolution.BlockerCode,
            PhraseFamilyTieBreakRuleId = phraseFamilyResolution.TieBreakRuleId,
            PhraseFamilyTieBreakSummary = phraseFamilyResolution.TieBreakSummary,
            Disposition = TaskboardWorkItemDecompositionDisposition.Covered,
            Reason = reason,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            BuildProfile = buildProfile,
            PhraseFamilyResolution = phraseFamilyResolution.Record
        };
        ApplyPhraseFamilyResolution(record, phraseFamilyResolution);
        return record;
    }

    private static TaskboardWorkItemDecompositionRecord BuildDecomposed(TaskboardImportRecord activeImport, TaskboardBatch batch, TaskboardRunWorkItem workItem, string phraseFamily, TaskboardBuildProfileResolutionRecord buildProfile, List<TaskboardDecomposedWorkItem> items, string phraseFamilyConfidence, string phraseFamilyTraceId, string templateId, List<string> templateCandidateIds, string templateSelectionTraceId, string templateSelectionReason)
    {
        return new TaskboardWorkItemDecompositionRecord
        {
            DecompositionId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = activeImport.WorkspaceRoot,
            PlanImportId = activeImport.ImportId,
            BatchId = batch.BatchId,
            OriginalWorkItemId = workItem.WorkItemId,
            OriginalTitle = workItem.Title,
            PhraseFamily = phraseFamily,
            PhraseFamilyConfidence = phraseFamilyConfidence,
            PhraseFamilyTraceId = phraseFamilyTraceId,
            TemplateId = templateId,
            TemplateCandidateIds = templateCandidateIds,
            TemplateSelectionTraceId = templateSelectionTraceId,
            TemplateSelectionReason = templateSelectionReason,
            Disposition = TaskboardWorkItemDecompositionDisposition.Decomposed,
            Reason = $"Taskboard auto-run decomposed `{workItem.Title}` into {items.Count} bounded `{FormatStackFamily(buildProfile.StackFamily)}` work item(s).",
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            BuildProfile = buildProfile,
            SubItems = items
        };
    }

    private static TaskboardWorkItemDecompositionRecord BuildDecomposed(TaskboardImportRecord activeImport, TaskboardBatch batch, TaskboardRunWorkItem workItem, string phraseFamily, TaskboardBuildProfileResolutionRecord buildProfile, List<TaskboardDecomposedWorkItem> items)
    {
        return BuildDecomposed(activeImport, batch, workItem, phraseFamily, buildProfile, items, "", "", $"{FormatStackFamily(buildProfile.StackFamily)}:{phraseFamily}", [], "", "");
    }

    private static TaskboardWorkItemDecompositionRecord BuildBlocked(TaskboardImportRecord activeImport, TaskboardBatch batch, TaskboardRunWorkItem workItem, PhraseFamilyResolution phraseFamilyResolution, TaskboardBuildProfileResolutionRecord buildProfile, string reason, List<string>? templateCandidateIds = null, string templateSelectionTraceId = "", string templateSelectionReason = "")
    {
        var record = BuildBlocked(
            activeImport,
            batch,
            workItem,
            phraseFamilyResolution.PhraseFamily,
            buildProfile,
            reason,
            phraseFamilyResolution.Confidence,
            phraseFamilyResolution.TraceId,
            templateCandidateIds,
            templateSelectionTraceId,
            templateSelectionReason);
        ApplyPhraseFamilyResolution(record, phraseFamilyResolution);
        return record;
    }

    private static TaskboardWorkItemDecompositionRecord BuildBlocked(TaskboardImportRecord activeImport, TaskboardBatch batch, TaskboardRunWorkItem workItem, string phraseFamily, TaskboardBuildProfileResolutionRecord buildProfile, string reason, string phraseFamilyConfidence = "", string phraseFamilyTraceId = "", List<string>? templateCandidateIds = null, string templateSelectionTraceId = "", string templateSelectionReason = "")
    {
        return new TaskboardWorkItemDecompositionRecord
        {
            DecompositionId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = activeImport.WorkspaceRoot,
            PlanImportId = activeImport.ImportId,
            BatchId = batch.BatchId,
            OriginalWorkItemId = workItem.WorkItemId,
            OriginalTitle = workItem.Title,
            PhraseFamily = phraseFamily,
            PhraseFamilyConfidence = phraseFamilyConfidence,
            PhraseFamilyTraceId = phraseFamilyTraceId,
            TemplateId = "",
            TemplateCandidateIds = templateCandidateIds ?? [],
            TemplateSelectionTraceId = templateSelectionTraceId,
            TemplateSelectionReason = templateSelectionReason,
            Disposition = TaskboardWorkItemDecompositionDisposition.Blocked,
            Reason = reason,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            BuildProfile = buildProfile
        };
    }

    private static void ApplyPhraseFamilyResolution(TaskboardWorkItemDecompositionRecord record, PhraseFamilyResolution phraseFamilyResolution)
    {
        record.PhraseFamily = FirstNonEmpty(record.PhraseFamily, phraseFamilyResolution.PhraseFamily);
        record.PhraseFamilyConfidence = FirstNonEmpty(record.PhraseFamilyConfidence, phraseFamilyResolution.Confidence);
        record.PhraseFamilyTraceId = FirstNonEmpty(record.PhraseFamilyTraceId, phraseFamilyResolution.TraceId);
        record.PhraseFamilySource = phraseFamilyResolution.Source;
        record.PhraseFamilyResolutionSummary = phraseFamilyResolution.ResolutionSummary;
        record.PhraseFamilyCandidates = [.. phraseFamilyResolution.Candidates];
        record.PhraseFamilyDeterministicCandidate = phraseFamilyResolution.DeterministicCandidate;
        record.PhraseFamilyAdvisoryCandidate = phraseFamilyResolution.AdvisoryCandidate;
        record.PhraseFamilyBlockerCode = phraseFamilyResolution.BlockerCode;
        record.PhraseFamilyTieBreakRuleId = phraseFamilyResolution.TieBreakRuleId;
        record.PhraseFamilyTieBreakSummary = phraseFamilyResolution.TieBreakSummary;
        record.PhraseFamilyResolution = phraseFamilyResolution.Record;
    }

    private static TaskboardDecomposedWorkItem CreatePureXamlPageItem(TaskboardRunWorkItem workItem, TaskboardStackFamily stackFamily, string phraseFamily, string templateId, List<string> candidateTemplateIds, string projectDirectory, string projectName, string pageName, string title, string subOrdinal)
    {
        var relativePath = NormalizeRelativePath(Path.Combine(projectDirectory, "Views", $"{pageName}.xaml"));
        return CreateToolItem(workItem, subOrdinal, "write_page", stackFamily, phraseFamily, templateId, candidateTemplateIds, $"Write {title} page", relativePath, $"{pageName}.xaml exists.", new ToolRequest
        {
            ToolName = "create_dotnet_page_view",
            Reason = $"Decomposed `{workItem.Title}` into a bounded WPF page implementation step.",
            Arguments =
            {
                ["path"] = relativePath,
                ["content"] = BuildPureXamlPage(projectName, pageName, title),
                ["role"] = "ui",
                ["pattern"] = "page",
                ["project"] = projectName,
                ["namespace"] = $"{projectName}.Views",
                ["depth"] = "integrated",
                ["followthrough"] = "binding_required,use_site_required,verification_required"
            }
        });
    }

    private static ToolRequest MakeDirRequest(string path, string originalTitle)
    {
        return new ToolRequest
        {
            ToolName = "make_dir",
            Reason = $"Decomposed `{originalTitle}` into a bounded workspace directory step.",
            Arguments =
            {
                ["path"] = NormalizeRelativePath(path)
            }
        };
    }

    private static TaskboardDecomposedWorkItem CreatePureXamlPageItem(TaskboardRunWorkItem workItem, TaskboardStackFamily stackFamily, string projectDirectory, string projectName, string pageName, string title, string subOrdinal)
    {
        return CreatePureXamlPageItem(workItem, stackFamily, "", "", [], projectDirectory, projectName, pageName, title, subOrdinal);
    }

    private static ToolRequest NamedWriteRequest(string toolName, string path, string content, string originalTitle, IReadOnlyDictionary<string, string>? extraArguments = null)
    {
        var request = new ToolRequest
        {
            ToolName = toolName,
            Reason = $"Decomposed `{originalTitle}` into a bounded `{toolName}` scaffold step.",
            Arguments =
            {
                ["path"] = NormalizeRelativePath(path),
                ["content"] = content
            }
        };

        if (extraArguments is not null)
        {
            foreach (var entry in extraArguments)
            {
                request.Arguments[entry.Key] = entry.Value;
            }
        }

        return request;
    }

    private static Dictionary<string, string> BuildProjectScaffoldArguments(
        string template,
        string projectName,
        string outputPath,
        string solutionPath,
        string role,
        bool attach,
        string validationTarget = "",
        string targetFramework = "",
        string templateSwitches = "")
    {
        var normalizedTemplate = DotnetScaffoldSurfaceService.NormalizeTemplate(template);
        var normalizedRole = DotnetScaffoldSurfaceService.ResolveDefaultRole(normalizedTemplate, projectName, role);
        var normalizedTargetFramework = DotnetScaffoldSurfaceService.ResolveTargetFramework(normalizedTemplate, targetFramework);
        var normalizedTemplateSwitches = DotnetScaffoldSurfaceService.ResolveDefaultSwitches(normalizedTemplate, templateSwitches);
        var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["template"] = normalizedTemplate,
            ["project_name"] = projectName,
            ["output_path"] = NormalizeRelativePath(outputPath),
            ["name"] = projectName,
            ["project"] = projectName,
            ["path"] = NormalizeRelativePath(Path.Combine(outputPath, $"{projectName}.csproj")),
            ["solution_path"] = NormalizeRelativePath(solutionPath),
            ["solution"] = NormalizeRelativePath(solutionPath),
            ["role"] = normalizedRole,
            ["target_framework"] = normalizedTargetFramework,
            ["template_switches"] = normalizedTemplateSwitches,
            ["scaffold_surface_version"] = DotnetScaffoldSurfaceService.MatrixVersion,
            ["scaffold_surface_status"] = DotnetScaffoldSurfaceService.ResolveSupportStatus(normalizedTemplate),
            ["attach"] = attach ? "true" : "false"
        };

        if (!string.IsNullOrWhiteSpace(validationTarget))
            arguments["validation"] = NormalizeRelativePath(validationTarget);

        return arguments;
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

    private static ToolRequest WriteFileRequest(string path, string content, string originalTitle)
    {
        return new ToolRequest
        {
            ToolName = "write_file",
            Reason = $"Decomposed `{originalTitle}` into a bounded workspace file scaffold.",
            Arguments =
            {
                ["path"] = NormalizeRelativePath(path),
                ["content"] = content
            }
        };
    }

    private static TaskboardDecomposedWorkItem CreateToolItem(TaskboardRunWorkItem workItem, string subOrdinal, string operationKind, TaskboardStackFamily stackFamily, string phraseFamily, string templateId, List<string> candidateTemplateIds, string description, string expectedArtifact, string validationHint, ToolRequest toolRequest)
    {
        return new TaskboardDecomposedWorkItem
        {
            SubItemId = StableId(workItem.WorkItemId, subOrdinal, operationKind, expectedArtifact),
            ParentWorkItemId = workItem.WorkItemId,
            Ordinal = int.TryParse(subOrdinal, out var parsed) ? parsed : 1,
            DisplayOrdinal = $"{FirstNonEmpty(workItem.DisplayOrdinal, workItem.Ordinal.ToString())}.{subOrdinal}",
            OperationKind = operationKind,
            TargetStack = FormatStackFamily(stackFamily),
            Description = description,
            PromptText = description,
            Summary = $"Decomposed from `{workItem.Title}`.",
            ExpectedArtifact = NormalizeRelativePath(expectedArtifact),
            ValidationHint = validationHint,
            PhraseFamily = phraseFamily,
            TemplateId = templateId,
            TemplateCandidateIds = [.. candidateTemplateIds],
            ToolRequest = toolRequest
        };
    }

    private static TaskboardDecomposedWorkItem CreateToolItem(TaskboardRunWorkItem workItem, string subOrdinal, string operationKind, TaskboardStackFamily stackFamily, string description, string expectedArtifact, string validationHint, ToolRequest toolRequest)
    {
        return CreateToolItem(workItem, subOrdinal, operationKind, stackFamily, "", "", [], description, expectedArtifact, validationHint, toolRequest);
    }

    private static string ResolveDotnetTemplate(TaskboardBuildProfileResolutionRecord buildProfile)
    {
        return DotnetScaffoldSurfaceService.NormalizeTemplate(buildProfile.Framework switch
        {
            "wpf" => "wpf",
            "windows_app_sdk" => "wpf",
            "webapi" or "aspnet" or "aspnetcore" => "webapi",
            "console" => "console",
            "worker" => "worker",
            _ => "wpf"
        });
    }

    private static string ResolveRequestedDotnetProjectTemplate(
        string canonicalTemplateHint,
        string canonicalOperationKind,
        TaskboardRunWorkItem workItem,
        string fallbackTemplate)
    {
        if (!string.IsNullOrWhiteSpace(canonicalTemplateHint))
            return DotnetScaffoldSurfaceService.NormalizeTemplate(canonicalTemplateHint);

        if (canonicalOperationKind.EndsWith(".classlib", StringComparison.OrdinalIgnoreCase))
            return "classlib";
        if (canonicalOperationKind.EndsWith(".xunit", StringComparison.OrdinalIgnoreCase))
            return "xunit";
        if (canonicalOperationKind.EndsWith(".wpf", StringComparison.OrdinalIgnoreCase))
            return "wpf";
        if (canonicalOperationKind.EndsWith(".console", StringComparison.OrdinalIgnoreCase))
            return "console";
        if (canonicalOperationKind.EndsWith(".worker", StringComparison.OrdinalIgnoreCase))
            return "worker";
        if (canonicalOperationKind.EndsWith(".webapi", StringComparison.OrdinalIgnoreCase))
            return "webapi";

        var text = NormalizeTemplateSelectionText(workItem);
        if (ContainsAny(text, " web api ", " webapi ", " asp.net core web api ", " api project "))
            return "webapi";
        if (ContainsAny(text, " worker ", " worker service ", " background worker "))
            return "worker";
        if (ContainsAny(text, " console ", " console app ", " console application "))
            return "console";
        if (ContainsAny(text, " xunit ", " tests ", " test project "))
            return "xunit";
        if (ContainsAny(text, " classlib ", " class library ", " core ", " storage ", " services ", " contracts ", " repository "))
            return "classlib";

        return DotnetScaffoldSurfaceService.NormalizeTemplate(fallbackTemplate);
    }

    private static string BuildDefaultProjectPath(string projectName, string template)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return "";

        var root = DotnetScaffoldSurfaceService.ResolveDefaultProjectRoot(template);
        return NormalizeRelativePath(Path.Combine(root, projectName, $"{projectName}.csproj"));
    }

    private static string ResolveExplicitProjectName(TaskboardRunWorkItem workItem, string canonicalOperationKind)
    {
        if (!canonicalOperationKind.StartsWith("dotnet.create_project", StringComparison.OrdinalIgnoreCase)
            && !canonicalOperationKind.Equals("dotnet.add_project_to_solution", StringComparison.OrdinalIgnoreCase)
            && !canonicalOperationKind.Equals("dotnet.add_project_reference", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        foreach (var source in EnumerateWorkItemSources(workItem))
        {
            if (TryMatchProjectToSolution(source, out var projectName, out _))
                return SanitizeProjectName(projectName);
            if (TryMatchProjectReferencePair(source, out var sourceProject, out _))
                return SanitizeProjectName(sourceProject);

            var fileMatch = Regex.Match(source, @"(?<name>[A-Za-z0-9_./\\-]+)\.csproj\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (fileMatch.Success)
                return SanitizeProjectName(Path.GetFileNameWithoutExtension(fileMatch.Groups["name"].Value.Replace('\\', '/')));

            var createProjectMatch = Regex.Match(
                source,
                @"\b(?:create|make|scaffold|initialize)\s+(?:dotnet\s+)?project(?:\s+(?:wpf|classlib|xunit|console|worker|web\s+api|webapi|class\s+library|desktop\s+app|test\s+project|app\s+project|worker\s+service|console\s+app|api\s+project))?\s+(?<name>[A-Za-z0-9_.-]+)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (createProjectMatch.Success)
                return SanitizeProjectName(createProjectMatch.Groups["name"].Value);
        }

        return "";
    }

    private static string ResolveExplicitProjectPath(
        TaskboardRunWorkItem workItem,
        IReadOnlyList<string> existingProjectPaths,
        string template)
    {
        var explicitProjectName = ResolveExplicitProjectName(workItem, "dotnet.create_project");
        if (string.IsNullOrWhiteSpace(explicitProjectName))
            return "";

        var exact = existingProjectPaths.FirstOrDefault(path =>
            Path.GetFileNameWithoutExtension(path).Equals(explicitProjectName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exact))
            return exact;

        return BuildDefaultProjectPath(explicitProjectName, template);
    }

    private static string InferProjectRoleFromName(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return "";

        return DotnetScaffoldSurfaceService.ResolveDefaultRole(
            InferProjectTemplateFromName(projectName),
            projectName);
    }

    private static bool TryResolveExplicitProjectReferencePair(
        TaskboardRunWorkItem workItem,
        IReadOnlyList<string> existingProjectPaths,
        out string sourceProjectPath,
        out string referenceProjectPath)
    {
        sourceProjectPath = "";
        referenceProjectPath = "";
        foreach (var source in EnumerateWorkItemSources(workItem))
        {
            if (!TryMatchProjectReferencePair(source, out var sourceProjectName, out var referenceProjectName))
                continue;

            sourceProjectPath = ResolveProjectPathByName(existingProjectPaths, sourceProjectName);
            referenceProjectPath = ResolveProjectPathByName(existingProjectPaths, referenceProjectName);
            return !string.IsNullOrWhiteSpace(sourceProjectPath) && !string.IsNullOrWhiteSpace(referenceProjectPath);
        }

        return false;
    }

    private static string ResolveProjectPathByName(IReadOnlyList<string> existingProjectPaths, string projectName)
    {
        var normalized = SanitizeProjectName(projectName);
        var existing = existingProjectPaths.FirstOrDefault(path =>
            Path.GetFileNameWithoutExtension(path).Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        return BuildDefaultProjectPath(normalized, InferProjectTemplateFromName(normalized));
    }

    private static string InferProjectTemplateFromName(string projectName)
    {
        return DotnetScaffoldSurfaceService.InferTemplateFromProjectIdentity(projectName, projectName, "wpf");
    }

    private static bool TryMatchProjectToSolution(string source, out string projectName, out string solutionName)
    {
        projectName = "";
        solutionName = "";
        var match = ExplicitProjectToSolutionPattern.Match(source ?? "");
        if (!match.Success)
            return false;

        projectName = match.Groups["project"].Value;
        solutionName = match.Groups["solution"].Value;
        return !string.IsNullOrWhiteSpace(projectName) && !string.IsNullOrWhiteSpace(solutionName);
    }

    private static bool TryMatchProjectReferencePair(string source, out string sourceProjectName, out string referenceProjectName)
    {
        sourceProjectName = "";
        referenceProjectName = "";
        var match = ExplicitReferenceFromToPattern.Match(source ?? "");
        if (!match.Success)
            match = ExplicitReferencePairPattern.Match(source ?? "");
        if (!match.Success)
            return false;

        sourceProjectName = match.Groups["source"].Value;
        referenceProjectName = match.Groups["target"].Value;
        return !string.IsNullOrWhiteSpace(sourceProjectName) && !string.IsNullOrWhiteSpace(referenceProjectName);
    }

    private static IEnumerable<string> EnumerateWorkItemSources(TaskboardRunWorkItem workItem)
    {
        if (!string.IsNullOrWhiteSpace(workItem.Title))
            yield return workItem.Title;
        if (!string.IsNullOrWhiteSpace(workItem.Summary))
            yield return workItem.Summary;
        if (!string.IsNullOrWhiteSpace(workItem.PromptText))
            yield return workItem.PromptText;
    }

    private static string SanitizeProjectName(string value)
    {
        var cleaned = (value ?? "")
            .Trim()
            .Trim('"', '\'')
            .Replace('\\', '.')
            .Replace('/', '.')
            .Replace(' ', '.');
        var parts = cleaned
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanIdentifier)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
        return parts.Count == 0 ? "" : string.Join(".", parts);
    }

    private static string BuildSecondaryProjectCreateDescription(string projectName, string template)
    {
        var normalizedTemplate = DotnetScaffoldSurfaceService.NormalizeTemplate(template);
        return normalizedTemplate switch
        {
            "xunit" => $"Create test project `{projectName}`",
            "console" => $"Create console project `{projectName}`",
            "worker" => $"Create worker service project `{projectName}`",
            "webapi" => $"Create Web API project `{projectName}`",
            "wpf" => $"Create WPF app project `{projectName}`",
            _ => $"Create class library project `{projectName}`"
        };
    }

    private static string BuildPrimaryProjectCreateDescription(string projectName, string template)
    {
        var normalizedTemplate = DotnetScaffoldSurfaceService.NormalizeTemplate(template);
        return normalizedTemplate switch
        {
            "console" => $"Create console app project `{projectName}`",
            "worker" => $"Create worker service project `{projectName}`",
            "webapi" => $"Create Web API project `{projectName}`",
            "xunit" => $"Create test project `{projectName}`",
            "classlib" => $"Create class library project `{projectName}`",
            _ => $"Create desktop app project `{projectName}`"
        };
    }

    private static string BuildExplicitProjectReferenceDescription(string sourceProjectPath, string referenceProjectPath)
    {
        return $"Add project reference from `{Path.GetFileNameWithoutExtension(sourceProjectPath)}` to `{Path.GetFileNameWithoutExtension(referenceProjectPath)}`";
    }

    private static string BuildExplicitProjectReferenceValidationHint(string sourceProjectPath, string referenceProjectPath)
    {
        return $"Project `{Path.GetFileNameWithoutExtension(sourceProjectPath)}` references `{Path.GetFileNameWithoutExtension(referenceProjectPath)}`.";
    }

    private static bool EnsureMaintenanceTargetResolved(
        TaskboardMaintenanceBaselineRecord baseline,
        string targetPath,
        string targetLabel,
        TaskboardImportRecord activeImport,
        TaskboardBatch batch,
        TaskboardRunWorkItem workItem,
        PhraseFamilyResolution phraseFamilyResolution,
        TaskboardBuildProfileResolutionRecord buildProfile,
        out TaskboardWorkItemDecompositionRecord blockedRecord)
    {
        if (baseline.IsMaintenanceMode && string.IsNullOrWhiteSpace(targetPath))
        {
            blockedRecord = BuildBlocked(
                activeImport,
                batch,
                workItem,
                phraseFamilyResolution,
                buildProfile,
                $"Taskboard auto-run blocked by maintenance baseline guard: `{workItem.Title}` requires the authoritative {targetLabel} inside the declared baseline, but RAM could not resolve it. storage_resolution=`{DisplayValue(baseline.StorageResolutionKind)}` storage_summary=`{DisplayValue(baseline.StorageResolutionSummary)}` declared_roots=`{DisplayJoined(baseline.DeclaredMutationRoots)}` allowed_roots=`{DisplayJoined(baseline.AllowedMutationRoots)}` discovered_roots=`{DisplayJoined(baseline.DiscoveredProjectRoots)}` compatible_storage_roots=`{DisplayJoined(baseline.CompatibleStorageRoots)}`. {baseline.Summary}");
            return false;
        }

        blockedRecord = new TaskboardWorkItemDecompositionRecord();
        return true;
    }

    private static string BuildMaintenanceScaffoldBlockedReason(string workItemTitle, TaskboardMaintenanceBaselineRecord baseline)
    {
        return $"Taskboard auto-run blocked by maintenance baseline guard: `{workItemTitle}` resolved to scaffold/create work, but this phase must reuse the existing baseline in place. Authoritative solution `{DisplayValue(baseline.PrimarySolutionPath)}` allowed roots `{DisplayJoined(baseline.AllowedMutationRoots)}` excluded generated roots `{DisplayJoined(baseline.ExcludedGeneratedRoots)}`.";
    }

    private static string BuildMaintenanceBuildBlockedReason(string workItemTitle, TaskboardMaintenanceBaselineRecord baseline)
    {
        var declaredSolution = baseline.DeclaredPaths.FirstOrDefault(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)) ?? "(none)";
        return $"Taskboard auto-run blocked by maintenance baseline guard: `{workItemTitle}` needs the declared maintenance baseline before build verification can run. Expected solution `{DisplayValue(baseline.PrimarySolutionPath, declaredSolution)}` allowed roots `{DisplayJoined(baseline.AllowedMutationRoots)}`.";
    }

    private static string DeriveAppName(string planTitle, string workspaceRoot)
    {
        var source = CleanIdentifier(planTitle);
        if (source.Contains(':'))
            source = source[(source.LastIndexOf(':') + 1)..].Trim();

        source = Regex.Replace(source, @"^\s*phase\s+\S+\s*[-:]\s*", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        source = Regex.Replace(source, @"\b(taskboard|starter|phase)\b", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        if (string.IsNullOrWhiteSpace(source))
            source = Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var words = Regex.Matches(source, @"[A-Za-z0-9]+").Select(match => match.Value).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        return words.Count == 0 ? "WorkspaceApp" : string.Concat(words.Select(Capitalize));
    }

    private static string CleanIdentifier(string value)
    {
        return (value ?? "").Trim().Trim('"', '\'').TrimEnd('.', ',', ';', ':', '!', '?');
    }

    private static string Capitalize(string value)
    {
        return value.Length == 0 ? "" : char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string BuildDotnetMainWindowXaml(string projectName, string title, string objective)
    {
        var windowTitle = string.IsNullOrWhiteSpace(title) ? projectName : title.Trim();
        var objectiveLine = EscapeXml(FirstNonEmpty(objective, "Initial shell scaffold generated by RAM."));
        return $$"""
<Window x:Class="{{projectName}}.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:state="clr-namespace:{{projectName}}.State"
        Title="{{EscapeXml(windowTitle)}}"
        Height="720"
        Width="1180"
        MinHeight="540"
        MinWidth="840">
    <Window.DataContext>
        <state:ShellViewModel/>
    </Window.DataContext>
    <DockPanel Background="#F3F4F6">
        <Border DockPanel.Dock="Top" Background="#111827" Padding="18">
            <StackPanel>
                <TextBlock Text="{Binding WindowTitle}" Foreground="White" FontSize="24" FontWeight="Bold"/>
                <TextBlock Text="{Binding CurrentStatusSummary}" Foreground="#D1D5DB" Margin="0,6,0,0" TextWrapping="Wrap"/>
                <TextBlock Text="{{objectiveLine}}" Foreground="#9CA3AF" Margin="0,8,0,0" TextWrapping="Wrap"/>
            </StackPanel>
        </Border>
        <Grid Margin="20">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="240"/>
                <ColumnDefinition Width="16"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>
            <Border Grid.Column="0" Background="White" BorderBrush="#E5E7EB" BorderThickness="1" CornerRadius="12" Padding="18">
                <StackPanel>
                    <TextBlock Text="Navigation" FontSize="18" FontWeight="SemiBold"/>
                    <TextBlock Text="The generated shell now exposes real state-backed sections with usable bindings and verification-ready navigation."
                               Margin="0,8,0,16"
                               Foreground="#4B5563"
                               TextWrapping="Wrap"/>
                    <ListBox ItemsSource="{Binding State.NavigationItems}"
                             BorderThickness="0"
                             Background="Transparent">
                        <ListBox.ItemTemplate>
                            <DataTemplate>
                                <Border Margin="0,0,0,8" Padding="12" Background="#F9FAFB" CornerRadius="10">
                                    <StackPanel>
                                        <TextBlock Text="{Binding Title}" FontWeight="SemiBold"/>
                                        <TextBlock Text="{Binding RouteKey}" Margin="0,4,0,0" Foreground="#6B7280"/>
                                    </StackPanel>
                                </Border>
                            </DataTemplate>
                        </ListBox.ItemTemplate>
                    </ListBox>
                </StackPanel>
            </Border>
            <Border Grid.Column="2" Background="#FFFFFF" BorderBrush="#E5E7EB" BorderThickness="1" CornerRadius="12" Padding="22">
                <TabControl>
                    <TabItem Header="Dashboard">
                        <Frame Source="Views/DashboardPage.xaml" NavigationUIVisibility="Hidden"/>
                    </TabItem>
                    <TabItem Header="Findings">
                        <Frame Source="Views/FindingsPage.xaml" NavigationUIVisibility="Hidden"/>
                    </TabItem>
                    <TabItem Header="History">
                        <Frame Source="Views/HistoryPage.xaml" NavigationUIVisibility="Hidden"/>
                    </TabItem>
                    <TabItem Header="Settings">
                        <Frame Source="Views/SettingsPage.xaml" NavigationUIVisibility="Hidden"/>
                    </TabItem>
                </TabControl>
            </Border>
        </Grid>
    </DockPanel>
</Window>
""";
    }

    private static string BuildPureXamlPage(string projectName, string pageName, string title)
    {
        return pageName switch
        {
            "DashboardPage" => BuildDashboardPageXaml(title),
            "FindingsPage" => BuildFindingsPageXaml(title),
            "HistoryPage" => BuildHistoryPageXaml(title),
            "SettingsPage" => BuildSettingsPageXaml(title),
            _ => $$"""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Title="{{EscapeXml(title)}}">
    <Grid Background="White">
        <Border Margin="24" Padding="24" BorderBrush="#E5E7EB" BorderThickness="1" CornerRadius="12">
            <StackPanel>
                <TextBlock Text="{{EscapeXml(title)}}" FontSize="22" FontWeight="Bold"/>
                <TextBlock Text="Generated shell surface with bounded layout implementation."
                           Margin="0,10,0,0"
                           TextWrapping="Wrap"/>
            </StackPanel>
        </Border>
    </Grid>
</Page>
"""
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

    private static string BuildCheckRegistryCs(string appName)
    {
        return $$"""
using System;
using System.Collections.Generic;

namespace {{appName}}.Tests;

public static class CheckRegistry
{
    public static IReadOnlyList<string> CreateDefaultChecks()
    {
        return
        [
            "defender",
            "firewall",
            "updates"
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
}
""";
    }

    private static string BuildSnapshotBuilderCs(string appName)
    {
        return $$"""
using System.Collections.Generic;
using System.Text.Json;

namespace {{appName}}.Tests;

public static class SnapshotBuilder
{
    public static string BuildDefaultSnapshotJson()
    {
        return BuildSnapshotJson(BuildDefaultSnapshot());
    }

    public static IReadOnlyDictionary<string,object> BuildDefaultSnapshot()
    {
        return new Dictionary<string,object>
        {
            ["machine"] = "local",
            ["checks"] = new[] { "defender", "firewall", "updates" },
            ["capturedBy"] = "ram"
        };
    }

    public static string BuildSnapshotJson(IReadOnlyDictionary<string,object>? snapshot)
    {
        return JsonSerializer.Serialize(snapshot ?? BuildDefaultSnapshot());
    }
}
""";
    }

    private static string BuildFindingsNormalizerCs(string appName)
    {
        return $$"""
using System;
using System.Collections.Generic;

namespace {{appName}}.Tests;

public static class FindingsNormalizer
{
    public static string NormalizeSeverity(string severity)
    {
        if (string.IsNullOrWhiteSpace(severity))
            return "info";

        return severity.Trim().ToLowerInvariant() switch
        {
            "critical" => "critical",
            "high" => "high",
            "medium" => "medium",
            "low" => "low",
            _ => "info"
        };
    }

    public static string NormalizeStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "unknown";

        return status.Trim().ToLowerInvariant() switch
        {
            "healthy" => "healthy",
            "queued" => "queued",
            "needs review" => "needs_review",
            "resolved" => "resolved",
            _ => "unknown"
        };
    }

    public static IReadOnlyList<string> NormalizeFindings(IEnumerable<string>? findings)
    {
        var normalized = new List<string>();
        if (findings is null)
            return normalized;

        foreach (var finding in findings)
        {
            if (string.IsNullOrWhiteSpace(finding))
                continue;

            normalized.Add(finding.Trim());
        }

        return normalized;
    }
}
""";
    }

    private static string BuildNativeCMakeLists(string appName)
    {
        return $$"""
cmake_minimum_required(VERSION 3.20)
project({{appName}} LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable({{appName}}
    src/main.cpp
    src/AppWindow.cpp
)

target_include_directories({{appName}} PRIVATE include)
""";
    }

    private static string BuildNativeAppWindowHeader()
    {
        return """
#pragma once

#include <windows.h>

class AppWindow
{
public:
    static HWND Create(HINSTANCE instance, int commandShow);
};
""";
    }

    private static string BuildNativeAppWindowSource(string appName)
    {
        return $$"""
#include "AppWindow.h"

namespace
{
    constexpr wchar_t WindowClassName[] = L"{{appName}}WindowClass";

    LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProc(window, message, wParam, lParam);
        }
    }
}

HWND AppWindow::Create(HINSTANCE instance, int commandShow)
{
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProc;
    windowClass.hInstance = instance;
    windowClass.lpszClassName = WindowClassName;
    RegisterClassW(&windowClass);

    auto* title = L"{{appName}}";
    auto window = CreateWindowExW(0, WindowClassName, title, WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 1200, 780, nullptr, nullptr, instance, nullptr);
    ShowWindow(window, commandShow);
    return window;
}
""";
    }

    private static string BuildNativeMainCpp(string appName)
    {
        return $$"""
#include "AppWindow.h"

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int commandShow)
{
    auto window = AppWindow::Create(instance, commandShow);
    if (!window)
        return 1;

    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0))
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }

    return static_cast<int>(message.wParam);
}
""";
    }

    private static string BuildNativeNavigationHeader()
    {
        return """
#pragma once

#include <string>

struct NavigationItem
{
    std::wstring Title;
    std::wstring RouteKey;
};
""";
    }

    private static string BuildNativeAppStateHeader()
    {
        return """
#pragma once

#include <string>
#include <vector>
#include "NavigationItem.h"

struct AppState
{
    std::wstring CurrentRoute = L"dashboard";
    std::vector<NavigationItem> NavigationItems;
};
""";
    }

    private static string BuildNativeSettingsStoreHeader()
    {
        return """
#pragma once

#include <string>

class SettingsStore
{
public:
    std::wstring Load() const;
    void Save(const std::wstring& json) const;
};
""";
    }

    private static string BuildNativeSettingsStoreSource()
    {
        return """
#include "Storage/SettingsStore.h"

#include <filesystem>
#include <fstream>

std::wstring SettingsStore::Load() const
{
    const auto path = std::filesystem::path(L"settings.json");
    if (!std::filesystem::exists(path))
        return L"{}";

    std::wifstream stream(path);
    return std::wstring((std::istreambuf_iterator<wchar_t>(stream)), std::istreambuf_iterator<wchar_t>());
}

void SettingsStore::Save(const std::wstring& json) const
{
    std::wofstream stream(std::filesystem::path(L"settings.json"));
    stream << json;
}
""";
    }

    private static string BuildNativePanelHeader(string className)
    {
        return $$"""
#pragma once

class {{className}}
{
public:
    void RenderPlaceholder() const;
};
""";
    }

    private static string BuildNativeCheckDefinitionHeader()
    {
        return """
#pragma once

#include <string>

struct CheckDefinition
{
    std::wstring CheckId;
    std::wstring DisplayName;
    std::wstring Severity = L"info";
};
""";
    }

    private static string BuildNativeFindingRecordHeader()
    {
        return """
#pragma once

#include <string>

struct FindingRecord
{
    std::wstring FindingId;
    std::wstring Title;
    std::wstring Severity = L"info";
    bool IsResolved = false;
};
""";
    }

    private static bool WorkspaceFileExists(string workspaceRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var fullPath = Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(fullPath);
    }

    private static List<string> ResolveExistingWorkspaceFiles(string workspaceRoot, string pattern)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return [];

        return Directory.EnumerateFiles(workspaceRoot, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ResolveExistingWorkspaceFile(string workspaceRoot, string pattern)
    {
        return ResolveExistingWorkspaceFiles(workspaceRoot, pattern)
            .FirstOrDefault();
    }

    private static string? ResolvePrimaryWorkspaceProjectPath(
        IReadOnlyList<string> projectPaths,
        string appName,
        string canonicalProjectPath = "",
        string canonicalProjectDirectoryHint = "",
        string canonicalRoleHint = "")
    {
        if (projectPaths.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(canonicalProjectPath)
            && IsPrimaryProjectRole(canonicalRoleHint))
        {
            var canonicalExact = projectPaths.FirstOrDefault(path =>
                PathsResolveToSameWorkspaceTarget(path, canonicalProjectPath));
            if (!string.IsNullOrWhiteSpace(canonicalExact))
                return canonicalExact;
        }

        if (!string.IsNullOrWhiteSpace(canonicalProjectDirectoryHint)
            && IsPrimaryProjectRole(canonicalRoleHint))
        {
            var canonicalDirectoryMatch = projectPaths.FirstOrDefault(path =>
                path.StartsWith($"{canonicalProjectDirectoryHint.TrimEnd('/')}/", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(canonicalDirectoryMatch))
                return canonicalDirectoryMatch;
        }

        var exact = projectPaths.FirstOrDefault(path =>
            Path.GetFileNameWithoutExtension(path).Equals(appName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exact))
            return exact;

        var preferred = projectPaths.FirstOrDefault(path =>
            path.StartsWith($"src/{appName}/", StringComparison.OrdinalIgnoreCase)
            && !IsSecondaryWorkspaceProjectPath(path));
        if (!string.IsNullOrWhiteSpace(preferred))
            return preferred;

        if (!string.IsNullOrWhiteSpace(appName))
            return null;

        return projectPaths.FirstOrDefault(path => !IsSecondaryWorkspaceProjectPath(path));
    }

    private static string? ResolveRoleWorkspaceProjectPath(
        IReadOnlyList<string> projectPaths,
        string expectedProjectName,
        string role,
        string canonicalProjectPath = "",
        string canonicalRoleHint = "")
    {
        if (projectPaths.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(canonicalProjectPath)
            && string.Equals(role, canonicalRoleHint, StringComparison.OrdinalIgnoreCase))
        {
            var canonicalExact = projectPaths.FirstOrDefault(path =>
                PathsResolveToSameWorkspaceTarget(path, canonicalProjectPath));
            if (!string.IsNullOrWhiteSpace(canonicalExact))
                return canonicalExact;
        }

        var exact = projectPaths.FirstOrDefault(path =>
            Path.GetFileNameWithoutExtension(path).Equals(expectedProjectName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exact))
            return exact;

        return role switch
        {
            "core" => projectPaths.FirstOrDefault(path =>
                path.Contains(".Core/", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(path).EndsWith(".Core", StringComparison.OrdinalIgnoreCase)),
            "tests" => projectPaths.FirstOrDefault(path =>
                path.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
                || path.Contains(".Tests/", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)),
            _ => null
        };
    }

    private static bool IsPrimaryProjectRole(string roleHint)
    {
        return string.IsNullOrWhiteSpace(roleHint)
            || roleHint.Equals("ui", StringComparison.OrdinalIgnoreCase)
            || roleHint.Equals("state", StringComparison.OrdinalIgnoreCase)
            || roleHint.Equals("views", StringComparison.OrdinalIgnoreCase)
            || roleHint.Equals("storage", StringComparison.OrdinalIgnoreCase)
            || roleHint.Equals("contracts", StringComparison.OrdinalIgnoreCase)
            || roleHint.Equals("models", StringComparison.OrdinalIgnoreCase)
            || roleHint.Equals("repository", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveOwningProjectDirectory(string canonicalTargetPath, string canonicalRoleHint)
    {
        if (string.IsNullOrWhiteSpace(canonicalTargetPath))
            return "";

        var normalized = NormalizeRelativePath(canonicalTargetPath).Trim('/');
        if (normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return NormalizeRelativePath(Path.GetDirectoryName(normalized)?.Replace('\\', Path.DirectorySeparatorChar) ?? "");

        foreach (var segment in new[] { "State", "Storage", "Contracts", "Models", "Views", "ViewModels" })
        {
            var infix = $"/{segment}/";
            var suffix = $"/{segment}";
            var index = normalized.IndexOf(infix, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
                return normalized[..index];
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return normalized[..^suffix.Length];
        }

        if (string.Equals(canonicalRoleHint, "state", StringComparison.OrdinalIgnoreCase)
            || string.Equals(canonicalRoleHint, "storage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(canonicalRoleHint, "contracts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(canonicalRoleHint, "models", StringComparison.OrdinalIgnoreCase)
            || string.Equals(canonicalRoleHint, "views", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeRelativePath(Path.GetDirectoryName(normalized)?.Replace('\\', Path.DirectorySeparatorChar) ?? "");
        }

        return "";
    }

    private static string TrimKnownProjectRoleSuffix(string value)
    {
        var cleaned = CleanIdentifier(value);
        if (string.IsNullOrWhiteSpace(cleaned))
            return "";

        foreach (var suffix in new[] { ".Core", ".Contracts", ".Storage", ".Services", ".Repository", ".Tests", ".Api", ".Worker", ".Console" })
        {
            if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return cleaned[..^suffix.Length];
        }

        return cleaned;
    }

    private static bool IsSecondaryWorkspaceProjectPath(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        return path.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".Tests/", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".Core/", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".Core", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".Storage/", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".Storage", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".Services/", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".Services", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".Repository/", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".Repository", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".Contracts/", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDomainContractsContinuationSourceProjectPath(TaskboardRunWorkItem workItem, string appProjectPath, string testsProjectPath)
    {
        var text = NormalizeTemplateSelectionText(workItem);
        if (!string.IsNullOrWhiteSpace(testsProjectPath)
            && ContainsAny(text, ".tests", " tests ", "test project", "testproject"))
        {
            return testsProjectPath;
        }

        return appProjectPath;
    }

    private static string BuildDomainContractsReferenceDescription(string sourceProjectPath, string testsProjectPath)
    {
        return !string.IsNullOrWhiteSpace(testsProjectPath)
               && PathsResolveToSameWorkspaceTarget(sourceProjectPath, testsProjectPath)
            ? "Add test project reference to core contracts library"
            : "Add app reference to core contracts library";
    }

    private static string BuildDomainContractsReferenceValidationHint(string sourceProjectPath, string testsProjectPath)
    {
        return !string.IsNullOrWhiteSpace(testsProjectPath)
               && PathsResolveToSameWorkspaceTarget(sourceProjectPath, testsProjectPath)
            ? "Test project references the core contracts library."
            : "App project references the core contracts library.";
    }

    private static string NormalizeRelativePath(string path)
    {
        return (path ?? "").Replace('\\', '/');
    }

    private static bool PathsResolveToSameWorkspaceTarget(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            NormalizeRelativePath(left).Trim(),
            NormalizeRelativePath(right).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeXml(string value)
    {
        return (value ?? "")
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    private static string StableId(params string[] values)
    {
        var joined = string.Join("|", values.Select(value => value ?? ""));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static string FormatStackFamily(TaskboardStackFamily stackFamily)
    {
        return stackFamily switch
        {
            TaskboardStackFamily.DotnetDesktop => "dotnet_desktop",
            TaskboardStackFamily.NativeCppDesktop => "native_cpp_desktop",
            TaskboardStackFamily.WebApp => "web_app",
            TaskboardStackFamily.RustApp => "rust_app",
            _ => "unknown"
        };
    }

    private DotnetBuildBootstrapPrerequisite? ResolveDotnetBuildBootstrapPrerequisite(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardDocument activeDocument,
        TaskboardBatch currentBatch,
        TaskboardRunWorkItem currentWorkItem)
    {
        DotnetBuildBootstrapPrerequisite? best = null;
        foreach (var batch in activeDocument.Batches.OrderBy(candidate => candidate.BatchNumber))
        {
            foreach (var candidate in EnumerateDocumentWorkItems(batch))
            {
                if (string.Equals(batch.BatchId, currentBatch.BatchId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.WorkItemId, currentWorkItem.WorkItemId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var phraseResolution = _phraseFamilyFallbackService.Resolve(
                    workspaceRoot,
                    activeImport,
                    activeDocument,
                    batch,
                    candidate);
                var phraseFamily = FirstNonEmpty(phraseResolution.PhraseFamily, candidate.PhraseFamily);
                if (!IsDotnetBootstrapPhraseFamily(phraseFamily))
                    continue;

                var match = new DotnetBuildBootstrapPrerequisite
                {
                    BatchId = batch.BatchId,
                    BatchNumber = batch.BatchNumber,
                    BatchTitle = batch.Title,
                    WorkItemId = candidate.WorkItemId,
                    WorkItemTitle = candidate.Title,
                    WorkItemOrdinal = candidate.Ordinal,
                    PhraseFamily = phraseFamily
                };

                if (best is null
                    || match.BatchNumber < best.BatchNumber
                    || (match.BatchNumber == best.BatchNumber && match.WorkItemOrdinal < best.WorkItemOrdinal))
                {
                    best = match;
                }
            }
        }

        return best;
    }

    private static IEnumerable<TaskboardRunWorkItem> EnumerateDocumentWorkItems(TaskboardBatch batch)
    {
        if (batch.Steps.Count > 0)
        {
            foreach (var step in batch.Steps.OrderBy(step => step.Ordinal))
            {
                yield return new TaskboardRunWorkItem
                {
                    WorkItemId = step.StepId,
                    Ordinal = step.Ordinal,
                    DisplayOrdinal = step.Ordinal.ToString(),
                    Title = step.Title,
                    Summary = BuildSectionSummary(step.Content),
                    PromptText = BuildSectionPromptText(step.Title, step.Content)
                };
            }

            yield break;
        }

        if (batch.Content.Paragraphs.Count > 0
            || batch.Content.NumberedItems.Count > 0
            || batch.Content.Subsections.Count > 0)
        {
            yield break;
        }

        var ordinal = 1;
        foreach (var item in batch.Content.BulletItems.Where(item => !string.IsNullOrWhiteSpace(item)).Take(6))
        {
            yield return new TaskboardRunWorkItem
            {
                WorkItemId = $"{batch.BatchId}:content:{ordinal}",
                Ordinal = ordinal,
                DisplayOrdinal = ordinal.ToString(),
                Title = item,
                Summary = "Parsed from batch content.",
                PromptText = item.Trim()
            };

            ordinal++;
        }
    }

    private static string BuildSectionSummary(TaskboardSectionContent content)
    {
        var firstLine = EnumerateSectionLines(content)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
        firstLine = firstLine.Trim();
        return firstLine.Length <= 140 ? firstLine : firstLine[..140] + "...";
    }

    private static string BuildSectionPromptText(string title, TaskboardSectionContent content)
    {
        if (!string.IsNullOrWhiteSpace(title) && !IsGenericDocumentPromptTitle(title))
            return title.Trim();

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(title))
            parts.Add(title.Trim());

        parts.AddRange(EnumerateSectionLines(content)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .Take(6));

        return string.Join(" ", parts).Trim();
    }

    private static IEnumerable<string> EnumerateSectionLines(TaskboardSectionContent content)
    {
        foreach (var line in content.Paragraphs)
            yield return line;
        foreach (var line in content.BulletItems)
            yield return line;
        foreach (var line in content.NumberedItems)
            yield return line;
        foreach (var subsection in content.Subsections)
        {
            foreach (var line in EnumerateSectionLines(subsection))
                yield return line;
        }
    }

    private static bool IsGenericDocumentPromptTitle(string? title)
    {
        return TaskboardStructuralHeadingService.IsNonActionableHeading(title);
    }

    private static bool IsDotnetBootstrapPhraseFamily(string? phraseFamily)
    {
        return phraseFamily?.Trim() switch
        {
            "solution_scaffold" => true,
            "project_scaffold" => true,
            "build_first_ui_shell" => true,
            "ui_shell_sections" => true,
            _ => false
        };
    }

    private async Task<TemplateSelection> ResolveTemplateSelectionAsync(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string phraseFamily,
        TaskboardBuildProfileResolutionRecord buildProfile,
        AppSettings? settings,
        string endpoint,
        string selectedModel,
        CancellationToken cancellationToken)
    {
        var candidates = ResolveTemplateCandidates(buildProfile.StackFamily, phraseFamily);
        if (candidates.Count == 0)
        {
            return TemplateSelection.Blocked(
                candidates,
                $"Taskboard auto-run paused: no decomposition template exists yet for phrase family `{phraseFamily}` on stack `{FormatStackFamily(buildProfile.StackFamily)}`.");
        }

        if (candidates.Count == 1)
            return TemplateSelection.Deterministic(candidates[0], candidates, $"Deterministic template selection matched `{candidates[0]}`.");

        var deterministicCandidate = ChooseDeterministicTemplateCandidate(workItem, phraseFamily, candidates);

        if (_templateSelectorAgentService is null || settings is null)
        {
            return TemplateSelection.Deterministic(
                deterministicCandidate,
                candidates,
                $"Deterministic template selection preferred `{deterministicCandidate}` from {candidates.Count} candidate template(s).");
        }

        var request = new TemplateSelectorAgentRequestPayload
        {
            PhraseFamily = phraseFamily,
            StackFamily = FormatStackFamily(buildProfile.StackFamily),
            WorkItemTitle = workItem.Title,
            WorkItemSummary = workItem.Summary,
            CandidateTemplateIds = [.. candidates],
            WorkspaceEvidence =
            [
                .. buildProfile.SourceEvidence.Select(evidence => $"{evidence.Source}:{evidence.Code}={evidence.Value}"),
                .. buildProfile.MissingEvidence.Select(missing => $"missing:{missing}")
            ]
        };
        var result = await _templateSelectorAgentService.SelectAsync(
            endpoint,
            selectedModel,
            settings,
            workspaceRoot,
            request,
            cancellationToken);
        if (result.Accepted)
        {
            return TemplateSelection.Accepted(
                result.Payload.TemplateId,
                candidates,
                result.TraceId,
                FirstNonEmpty(
                    string.Join(", ", result.Payload.ReasonCodes.Where(code => !string.IsNullOrWhiteSpace(code))),
                    $"Advisory template selector chose `{result.Payload.TemplateId}`."));
        }

        return TemplateSelection.Deterministic(
            deterministicCandidate,
            candidates,
            $"Deterministic template selection preferred `{deterministicCandidate}` after advisory selection fallback.",
            result.TraceId);
    }

    private static string ChooseDeterministicTemplateCandidate(
        TaskboardRunWorkItem workItem,
        string phraseFamily,
        IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0)
            return "";

        if (candidates.Count == 1)
            return candidates[0];

        if (string.Equals(phraseFamily, "repository_scaffold", StringComparison.OrdinalIgnoreCase))
        {
            var domainContractsCandidate = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate, "dotnet.domain_contracts_scaffold.v1", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(domainContractsCandidate) && LooksLikeCoreContractsContinuation(workItem))
                return domainContractsCandidate;
        }

        return candidates[0];
    }

    private static bool LooksLikeCoreContractsContinuation(TaskboardRunWorkItem workItem)
    {
        var text = NormalizeTemplateSelectionText(workItem);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (ContainsAny(text, "repository", "snapshot", "sqlite", "storage", "settingsstore", "isnapshotrepository"))
            return false;

        if (ContainsAny(text, "createcorelibrary", "attachcorelibrary", "adddomainreference"))
            return true;

        if (ContainsAny(
                text,
                " add project reference ",
                " wire project reference ",
                " attach reference ",
                " reference core library from app ",
                " add app reference to core ",
                " add test project reference ")
            && ContainsAny(text, " core ", " contracts ", " models "))
        {
            return true;
        }

        return ContainsAny(text, ".core", " core ", "contracts", "models", "checkdefinition", "findingrecord", "corelibrary", "contractslibrary");
    }

    private static string NormalizeTemplateSelectionText(TaskboardRunWorkItem workItem)
    {
        return $" {NormalizeSelectionToken(workItem.Title)} {NormalizeSelectionToken(workItem.Summary)} {NormalizeSelectionToken(workItem.PromptText)} {NormalizeSelectionToken(workItem.ExpectedArtifact)} ";
    }

    private static string NormalizeSelectionToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');

        return string.Join(" ", builder
            .ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ResolveTemplateCandidates(TaskboardStackFamily stackFamily, string phraseFamily)
    {
        return stackFamily switch
        {
            TaskboardStackFamily.DotnetDesktop => phraseFamily switch
            {
                "solution_scaffold" => ["dotnet.solution_scaffold.v1"],
                "project_scaffold" => ["dotnet.solution_scaffold.v1"],
                "ui_shell_sections" => ["dotnet.shell_page_set_scaffold.v1"],
                "build_first_ui_shell" => ["dotnet.desktop_shell_scaffold.v1", "dotnet.solution_scaffold.v1"],
                "core_domain_models_contracts" => ["dotnet.domain_contracts_scaffold.v1"],
                "repository_scaffold" => ["dotnet.repository_scaffold.v1", "dotnet.domain_contracts_scaffold.v1"],
                "maintenance_context" => ["artifact_inspection_single_step"],
                "build_failure_repair" => ["repair_execution_chain"],
                "solution_graph_repair" => ["repair_execution_chain"],
                "add_navigation_app_state" => ["dotnet.navigation_wireup.v1"],
                "setup_storage_layer" => ["dotnet.sqlite_storage_bootstrap.v1"],
                "add_settings_page" or "add_history_log_view" or "wire_dashboard" => ["dotnet.page_and_viewmodel_scaffold.v1"],
                "check_runner" => ["dotnet.check_runner_scaffold.v1"],
                "findings_pipeline" => ["dotnet.findings_pipeline_bootstrap.v1", "dotnet.check_runner_scaffold.v1"],
                "build_verify" => ["workspace.build_verify.v1"],
                _ => []
            },
            TaskboardStackFamily.NativeCppDesktop => phraseFamily switch
            {
                "solution_scaffold" => ["cmake.project_bootstrap.v1"],
                "project_scaffold" => ["cmake.project_bootstrap.v1"],
                "native_project_bootstrap" => ["cmake.project_bootstrap.v1"],
                "cmake_bootstrap" => ["cmake.project_bootstrap.v1"],
                "ui_shell_sections" => ["cpp.win32_shell_page_set.v1"],
                "build_first_ui_shell" => ["cpp.win32_shell_scaffold.v1", "cmake.project_bootstrap.v1"],
                "core_domain_models_contracts" => ["cpp.library_scaffold.v1"],
                "repository_scaffold" => ["cpp.library_scaffold.v1"],
                "maintenance_context" => ["artifact_inspection_single_step"],
                "add_navigation_app_state" => ["cpp.win32_shell_scaffold.v1"],
                "setup_storage_layer" => ["cpp.library_scaffold.v1"],
                "add_settings_page" or "add_history_log_view" or "wire_dashboard" => ["cmake.target_attach.v1"],
                "check_runner" => ["cpp.library_scaffold.v1"],
                "findings_pipeline" => ["cpp.library_scaffold.v1"],
                "build_verify" => ["workspace.native_build_verify.v1"],
                _ => []
            },
            _ => []
        };
    }

    private static bool LooksLikeBroadBuilderPhrase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim();
        return (normalized.Contains("build ", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("set up", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("setup ", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("wire ", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("scaffold", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("bootstrap", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("initialize", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("add ", StringComparison.OrdinalIgnoreCase))
            && (normalized.Contains("shell", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("solution", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("navigation", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("storage", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("contract", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("model", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("settings", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("history", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("dashboard", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("runner", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("state", StringComparison.OrdinalIgnoreCase));
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

    private static string DisplayValue(params string?[] values)
    {
        var value = FirstNonEmpty(values);
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static string DisplayJoined(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "(none)" : string.Join(", ", values);
    }

    private sealed class PhraseFamilyResolution
    {
        public bool ShouldDecompose { get; init; }
        public bool IsBlocked { get; init; }
        public string PhraseFamily { get; init; } = "";
        public string Confidence { get; init; } = "";
        public string TraceId { get; init; } = "";
        public string Source { get; init; } = "";
        public string ResolutionSummary { get; init; } = "";
        public List<string> Candidates { get; init; } = [];
        public string DeterministicCandidate { get; init; } = "";
        public string AdvisoryCandidate { get; init; } = "";
        public string BlockerCode { get; init; } = "";
        public string BlockReason { get; init; } = "";
        public string TieBreakRuleId { get; init; } = "";
        public string TieBreakSummary { get; init; } = "";
        public string CanonicalOperationKind { get; init; } = "";
        public string CanonicalTargetPath { get; init; } = "";
        public string CanonicalProjectName { get; init; } = "";
        public string CanonicalTemplateHint { get; init; } = "";
        public string CanonicalRoleHint { get; init; } = "";
        public TaskboardPhraseFamilyResolutionRecord Record { get; init; } = new();

        public static PhraseFamilyResolution FromRecord(TaskboardPhraseFamilyResolutionRecord record)
        {
            return new PhraseFamilyResolution
            {
                ShouldDecompose = record.ShouldDecompose,
                IsBlocked = record.IsBlocked,
                PhraseFamily = record.PhraseFamily,
                Confidence = record.Confidence,
                TraceId = FirstNonEmpty(record.ResolutionId, record.AdvisoryTraceId),
                Source = record.ResolutionSource.ToString().ToLowerInvariant(),
                ResolutionSummary = record.ResolutionSummary,
                Candidates = [.. record.CandidatePhraseFamilies],
                DeterministicCandidate = record.DeterministicCandidate,
                AdvisoryCandidate = record.AdvisoryPhraseFamily,
                BlockerCode = record.BlockerCode.ToString().ToLowerInvariant(),
                BlockReason = record.BlockerMessage,
                TieBreakRuleId = record.TieBreakRuleId,
                TieBreakSummary = record.TieBreakSummary,
                CanonicalOperationKind = record.CanonicalOperationKind,
                CanonicalTargetPath = record.CanonicalTargetPath,
                CanonicalProjectName = record.CanonicalProjectName,
                CanonicalTemplateHint = record.CanonicalTemplateHint,
                CanonicalRoleHint = record.CanonicalRoleHint,
                Record = record
            };
        }
    }

    private sealed class DotnetBuildBootstrapPrerequisite
    {
        public string BatchId { get; init; } = "";
        public int BatchNumber { get; init; }
        public string BatchTitle { get; init; } = "";
        public string WorkItemId { get; init; } = "";
        public int WorkItemOrdinal { get; init; }
        public string WorkItemTitle { get; init; } = "";
        public string PhraseFamily { get; init; } = "";
    }

    private sealed class TemplateSelection
    {
        public bool IsResolved { get; init; }
        public string TemplateId { get; init; } = "";
        public List<string> CandidateTemplateIds { get; init; } = [];
        public string TraceId { get; init; } = "";
        public string Reason { get; init; } = "";

        public static TemplateSelection Deterministic(string templateId, List<string> candidateTemplateIds, string reason, string traceId = "")
        {
            return new TemplateSelection
            {
                IsResolved = true,
                TemplateId = templateId,
                CandidateTemplateIds = [.. candidateTemplateIds],
                TraceId = traceId,
                Reason = reason
            };
        }

        public static TemplateSelection Accepted(string templateId, List<string> candidateTemplateIds, string traceId, string reason)
        {
            return new TemplateSelection
            {
                IsResolved = true,
                TemplateId = templateId,
                CandidateTemplateIds = [.. candidateTemplateIds],
                TraceId = traceId,
                Reason = reason
            };
        }

        public static TemplateSelection Blocked(List<string> candidateTemplateIds, string reason, string traceId = "")
        {
            return new TemplateSelection
            {
                IsResolved = false,
                CandidateTemplateIds = [.. candidateTemplateIds],
                TraceId = traceId,
                Reason = reason
            };
        }
    }
}
