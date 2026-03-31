using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class DeterministicPhraseFamilyFallbackService
{
    public const string ResolverContractVersion = "deterministic_phrase_family_fallback.v6";

    private readonly CommandCanonicalizationService _commandCanonicalizationService = new();
    private readonly FileIdentityService _fileIdentityService = new();

    private static readonly string[] BroadBuilderVerbs =
    [
        "build",
        "create",
        "setup",
        "set up",
        "wire",
        "scaffold",
        "bootstrap",
        "initialize",
        "add",
        "attach",
        "include",
        "register",
        "reference",
        "verify",
        "validate",
        "run",
        "rerun",
        "repair",
        "fix",
        "reconcile",
        "write",
        "generate",
        "implement",
        "connect",
        "continue",
        "defer",
        "make",
        "confirm",
        "ensure",
        "complete"
    ];

    public TaskboardPhraseFamilyResolutionRecord Resolve(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardDocument activeDocument,
        TaskboardBatch batch,
        TaskboardRunWorkItem workItem)
    {
        var rawPhraseText = FirstNonEmpty(
            CombineRawText(
                workItem.Title,
                workItem.Summary,
                workItem.PromptText),
            batch.Title);
        var normalizedPhraseText = NormalizeCombinedText(rawPhraseText);
        var stackHintText = NormalizeText(
            workItem.Title,
            workItem.Summary,
            workItem.PromptText,
            batch.Title,
            activeDocument.Title,
            activeDocument.ObjectiveText);

        var resolution = CreateBaseRecord(workspaceRoot, activeImport, batch, workItem);
        resolution.RawPhraseText = rawPhraseText;
        resolution.NormalizedPhraseText = normalizedPhraseText;
        resolution.NormalizationSummary = BuildNormalizationSummary(rawPhraseText, normalizedPhraseText);

        var canonicalization = _commandCanonicalizationService.Canonicalize(
            rawPhraseText,
            workspaceRoot,
            activeImport.ImportId,
            batch.BatchId,
            workItem.WorkItemId,
            workItem.Title);
        resolution.CanonicalOperationKind = canonicalization.NormalizedOperationKind;
        resolution.CanonicalTargetPath = canonicalization.NormalizedTargetPath;
        resolution.CanonicalProjectName = canonicalization.NormalizedProjectName;
        resolution.CanonicalTemplateHint = canonicalization.NormalizedTemplateHint;
        resolution.CanonicalRoleHint = canonicalization.TargetRoleHint;
        resolution.CanonicalizationTrace = canonicalization.NormalizationTrace;

        var canonicalFamily = ResolveCanonicalOperationFamily(canonicalization, stackHintText);
        var operationFamily = ResolveOperationKindFamily(workItem.OperationKind, workItem.TargetStack);
        if (!string.IsNullOrWhiteSpace(operationFamily)
            && !ShouldPreferCanonicalFamily(workItem, operationFamily, canonicalFamily, canonicalization))
        {
            resolution.ShouldDecompose = true;
            resolution.PhraseFamily = operationFamily;
            resolution.Confidence = "high";
            resolution.ResolutionSource = TaskboardPhraseFamilyResolutionSource.OperationKind;
            resolution.ResolutionSummary = $"Resolved phrase family `{operationFamily}` from operation_kind={NormalizeValue(workItem.OperationKind)}. {resolution.NormalizationSummary}";
            resolution.CandidatePhraseFamilies = [operationFamily];
            resolution.DeterministicCandidate = operationFamily;
            resolution.DeterministicConfidence = "high";
            resolution.DeterministicReason = "operation_kind";
            resolution.ClosestKnownFamilyGroup = operationFamily;
            ApplyTrace(resolution, "operation_kind_short_circuit", "builder_operation_pending_downstream", "lane_resolution_pending_downstream");
            return resolution;
        }

        if (!string.IsNullOrWhiteSpace(canonicalFamily))
        {
            resolution.ShouldDecompose = true;
            resolution.PhraseFamily = canonicalFamily;
            resolution.Confidence = "high";
            resolution.ResolutionSource = TaskboardPhraseFamilyResolutionSource.CommandCanonicalization;
            resolution.ResolutionSummary = $"Resolved phrase family `{canonicalFamily}` from canonical operation `{canonicalization.NormalizedOperationKind}`. {BuildPhraseTraceSummary(rawPhraseText, normalizedPhraseText, canonicalFamily)}";
            resolution.CandidatePhraseFamilies = [canonicalFamily];
            resolution.DeterministicCandidate = canonicalFamily;
            resolution.DeterministicConfidence = "high";
            resolution.DeterministicReason = FirstNonEmpty(canonicalization.NormalizedOperationKind, "command_canonicalization");
            resolution.ClosestKnownFamilyGroup = canonicalFamily;
            ApplyTrace(resolution, "canonical_operation_resolved", "builder_operation_pending_downstream", "lane_resolution_pending_downstream");
            return resolution;
        }

        if (!string.IsNullOrWhiteSpace(operationFamily))
        {
            resolution.ShouldDecompose = true;
            resolution.PhraseFamily = operationFamily;
            resolution.Confidence = "high";
            resolution.ResolutionSource = TaskboardPhraseFamilyResolutionSource.OperationKind;
            resolution.ResolutionSummary = $"Resolved phrase family `{operationFamily}` from operation_kind={NormalizeValue(workItem.OperationKind)} after canonical fallback produced no stronger deterministic family. {resolution.NormalizationSummary}";
            resolution.CandidatePhraseFamilies = [operationFamily];
            resolution.DeterministicCandidate = operationFamily;
            resolution.DeterministicConfidence = "high";
            resolution.DeterministicReason = "operation_kind";
            resolution.ClosestKnownFamilyGroup = operationFamily;
            ApplyTrace(resolution, "operation_kind_short_circuit", "builder_operation_pending_downstream", "lane_resolution_pending_downstream");
            return resolution;
        }

        var matches = BuildMatches(normalizedPhraseText, stackHintText);
        var closestKnownFamilyGroup = InferClosestKnownFamilyGroup(normalizedPhraseText, stackHintText);
        resolution.ClosestKnownFamilyGroup = closestKnownFamilyGroup;

        if (matches.Count == 0 && !LooksLikeBroadBuilderPhrase(normalizedPhraseText))
        {
            resolution.ShouldDecompose = false;
            resolution.BlockerCode = TaskboardPhraseFamilyBlockerCode.NotBroadBuilderPhrase;
            resolution.BlockerMessage = $"Work item does not require broad builder phrase decomposition. {BuildPhraseTraceSummary(rawPhraseText, normalizedPhraseText, closestKnownFamilyGroup)}";
            resolution.ResolutionSummary = $"Phrase-family decomposition was not required for this work item. {BuildPhraseTraceSummary(rawPhraseText, normalizedPhraseText, closestKnownFamilyGroup)}";
            ApplyTrace(resolution, "not_broad_builder_phrase", "builder_operation_not_reached", "lane_resolution_not_reached");
            return resolution;
        }

        if (matches.Count == 0)
        {
            resolution.ShouldDecompose = false;
            resolution.IsBlocked = true;
            resolution.BlockerCode = TaskboardPhraseFamilyBlockerCode.NoDeterministicRule;
            resolution.BlockerMessage = BuildNoRuleMessage(rawPhraseText, normalizedPhraseText, workItem.TargetStack, stackHintText, closestKnownFamilyGroup);
            resolution.ResolutionSummary = resolution.BlockerMessage;
            ApplyTrace(resolution, "deterministic_fallback_no_rule", "builder_operation_not_reached_due_to_phrase_family_block", "lane_resolution_not_reached_due_to_phrase_family_block");
            return resolution;
        }

        var ordered = matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.PhraseFamily, StringComparer.OrdinalIgnoreCase)
            .ToList();
        resolution.CandidatePhraseFamilies = ordered
            .Select(match => match.PhraseFamily)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var winner = ordered[0];
        resolution.DeterministicCandidate = winner.PhraseFamily;
        resolution.DeterministicConfidence = winner.Confidence;
        resolution.DeterministicReason = winner.Reason;
        resolution.ClosestKnownFamilyGroup = FirstNonEmpty(closestKnownFamilyGroup, winner.PhraseFamily);

        if (ordered.Count > 1
            && ordered[1].Score == winner.Score
            && !string.Equals(ordered[1].PhraseFamily, winner.PhraseFamily, StringComparison.OrdinalIgnoreCase))
        {
            resolution.ShouldDecompose = false;
            resolution.IsBlocked = true;
            resolution.BlockerCode = TaskboardPhraseFamilyBlockerCode.DeterministicRuleConflict;
            resolution.BlockerMessage = $"Multiple phrase families matched equally ({winner.PhraseFamily}, {ordered[1].PhraseFamily}) and need a deterministic or advisory tie-break. {BuildPhraseTraceSummary(rawPhraseText, normalizedPhraseText, resolution.ClosestKnownFamilyGroup)}";
            resolution.ResolutionSummary = $"Deterministic phrase-family fallback found multiple equally strong matches. {BuildPhraseTraceSummary(rawPhraseText, normalizedPhraseText, resolution.ClosestKnownFamilyGroup)}";
            ApplyTrace(resolution, "deterministic_rule_conflict", "builder_operation_not_reached_due_to_phrase_family_block", "lane_resolution_not_reached_due_to_phrase_family_block");
            return resolution;
        }

        resolution.ShouldDecompose = true;
        resolution.PhraseFamily = winner.PhraseFamily;
        resolution.Confidence = winner.Confidence;
        resolution.ResolutionSource = TaskboardPhraseFamilyResolutionSource.DeterministicFallback;
        resolution.ResolutionSummary = $"Resolved phrase family `{winner.PhraseFamily}` from deterministic fallback rules via {winner.Reason}. {BuildPhraseTraceSummary(rawPhraseText, normalizedPhraseText, resolution.ClosestKnownFamilyGroup)}";
        ApplyTrace(resolution, "deterministic_fallback_resolved", "builder_operation_pending_downstream", "lane_resolution_pending_downstream");
        return resolution;
    }

    private static TaskboardPhraseFamilyResolutionRecord CreateBaseRecord(
        string workspaceRoot,
        TaskboardImportRecord activeImport,
        TaskboardBatch batch,
        TaskboardRunWorkItem workItem)
    {
        return new TaskboardPhraseFamilyResolutionRecord
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            PlanImportId = activeImport.ImportId,
            BatchId = batch.BatchId,
            WorkItemId = workItem.WorkItemId,
            WorkItemTitle = workItem.Title,
            ResolutionPathTrace = "taskboard_run_projection>auto_run>decomposition>phrase_family",
            TerminalResolverStage = "phrase_family_pending",
            BuilderOperationResolutionStatus = "builder_operation_not_reached",
            LaneResolutionStatus = "lane_resolution_not_reached",
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private static void ApplyTrace(
        TaskboardPhraseFamilyResolutionRecord resolution,
        string terminalResolverStage,
        string builderOperationResolutionStatus,
        string laneResolutionStatus)
    {
        resolution.TerminalResolverStage = terminalResolverStage;
        resolution.BuilderOperationResolutionStatus = builderOperationResolutionStatus;
        resolution.LaneResolutionStatus = laneResolutionStatus;
        resolution.ResolutionPathTrace =
            $"taskboard_run_projection>auto_run>decomposition>phrase_family terminal_stage={DisplayValue(terminalResolverStage)} builder_operation={DisplayValue(builderOperationResolutionStatus)} lane_resolution={DisplayValue(laneResolutionStatus)}";
    }

    private static string ResolveOperationKindFamily(string operationKind, string targetStack)
    {
        if (string.IsNullOrWhiteSpace(operationKind))
            return "";

        return NormalizeValue(operationKind) switch
        {
            "shell_section_create" or "shell_behavior_extend" or "shell_layout_update" => "ui_shell_sections",
            "create_solution" => "solution_scaffold",
            "create_project" or "create_test_project" or "create_core_library" => string.Equals(NormalizeValue(targetStack), "native_cpp_desktop", StringComparison.OrdinalIgnoreCase)
                ? "native_project_bootstrap"
                : "project_scaffold",
            "add_project_to_solution" or "attach_test_project" => string.Equals(NormalizeValue(targetStack), "native_cpp_desktop", StringComparison.OrdinalIgnoreCase)
                ? "native_project_bootstrap"
                : "solution_scaffold",
            "write_shell_layout" or "write_page" or "write_app_window_header" or "write_app_window_source" or "write_main_cpp" => "build_first_ui_shell",
            "write_navigation_item" or "write_app_state" or "write_navigation_header" or "write_shell_registration" => "add_navigation_app_state",
            "write_shell_viewmodel" => "wire_dashboard",
            "write_storage_contract" or "write_storage_impl" or "write_storage_header" or "write_storage_source" => "setup_storage_layer",
            "inspect_context_artifacts" => "maintenance_context",
            "write_repository_contract" or "write_repository_impl" => "repository_scaffold",
            "write_contract_file" or "write_domain_model_file" or "write_contract_header" or "write_domain_model_header" => "repository_scaffold",
            "write_check_registry" or "write_snapshot_builder" or "write_findings_normalizer" => "findings_pipeline",
            "configure_cmake" => "cmake_bootstrap",
            "build_solution" or "build_native_workspace" => "build_verify",
            "run_test_project" => "check_runner",
            "inspect_solution_wiring" or "inspect_project_reference_graph" or "repair_project_attachment" or "repair_generated_build_targets" => "solution_graph_repair",
            _ => ""
        };
    }

    private static bool ShouldPreferCanonicalFamily(
        TaskboardRunWorkItem workItem,
        string operationFamily,
        string canonicalFamily,
        CommandCanonicalizationRecord canonicalization)
    {
        if (string.IsNullOrWhiteSpace(canonicalFamily)
            || string.Equals(operationFamily, canonicalFamily, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var operationKind = NormalizeValue(workItem.OperationKind);
        if (operationKind is "build_solution" or "build_native_workspace" or "run_test_project" or "create_project" or "add_project_to_solution" or "add_project_reference")
            return true;

        var canonicalOperation = NormalizeValue(canonicalization.NormalizedOperationKind);
        return canonicalOperation.StartsWith("filesystem.", StringComparison.OrdinalIgnoreCase)
            || canonicalOperation.StartsWith("dotnet.create_", StringComparison.OrdinalIgnoreCase)
            || canonicalOperation is "dotnet.add_project_to_solution" or "dotnet.add_project_reference";
    }

    private string ResolveCanonicalOperationFamily(CommandCanonicalizationRecord canonicalization, string stackHintText)
    {
        return NormalizeValue(canonicalization.NormalizedOperationKind) switch
        {
            "filesystem.create_directory" => ResolveDirectoryFamily(canonicalization, stackHintText),
            "dotnet.create_solution" or "dotnet.add_project_to_solution" => "solution_scaffold",
            "dotnet.create_project.xunit" => "project_scaffold",
            "dotnet.test" => "check_runner",
            "dotnet.create_project.wpf" or "dotnet.create_project.classlib" or "dotnet.create_project.console" or "dotnet.create_project.worker" or "dotnet.create_project.webapi" or "dotnet.create_project" => "project_scaffold",
            "dotnet.add_project_reference" => "solution_scaffold",
            "dotnet.build" => "build_verify",
            "file.write" => ResolveFileWriteFamily(canonicalization, stackHintText),
            _ => ""
        };
    }

    private string ResolveDirectoryFamily(CommandCanonicalizationRecord canonicalization, string stackHintText)
    {
        var identity = _fileIdentityService.Identify(canonicalization.NormalizedTargetPath);
        return identity.Role switch
        {
            "state" => "add_navigation_app_state",
            "storage" => "setup_storage_layer",
            "contracts" or "models" or "core" => "core_domain_models_contracts",
            "views" or "ui" => "build_first_ui_shell",
            "tests" => "check_runner",
            _ when MentionsAny(stackHintText, "cmake", "native", "cpp") => "native_project_bootstrap",
            _ when MentionsAny(canonicalization.NormalizedPhraseText, "directory", "folder", "state", "storage", "contracts", "models", "views") => InferClosestKnownFamilyGroup(canonicalization.NormalizedPhraseText, stackHintText),
            _ => ""
        };
    }

    private string ResolveFileWriteFamily(CommandCanonicalizationRecord canonicalization, string stackHintText)
    {
        var identity = _fileIdentityService.Identify(canonicalization.NormalizedTargetPath);
        return identity.Role switch
        {
            "storage" => "setup_storage_layer",
            "contracts" or "models" or "core" => "core_domain_models_contracts",
            "state" => "add_navigation_app_state",
            "views" or "ui" => "build_first_ui_shell",
            "tests" => "findings_pipeline",
            _ => InferClosestKnownFamilyGroup(canonicalization.NormalizedPhraseText, stackHintText)
        };
    }

    private static List<PhraseFamilyMatch> BuildMatches(string combinedText, string stackHintText)
    {
        var matches = new List<PhraseFamilyMatch>();
        var prefersNative = MentionsAny(stackHintText, "c++", "cpp", "cmake", "win32", "native");

        AddMatch(matches, "solution_scaffold", 18, combinedText,
            "create solution",
            "create dotnet solution",
            "scaffold solution",
            "initialize solution",
            "make solution",
            "create sln",
            "setup solution",
            "bootstrap solution",
            "solution scaffold");
        AddMatch(matches, "solution_scaffold", 18, combinedText,
            "add project to solution",
            "attach project to solution",
            "include project in solution",
            "wire project into solution",
            "register project in solution",
            "add app project to solution",
            "add test project to solution");

        AddMatch(matches, "project_scaffold", 18, combinedText,
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
            "project scaffold",
            "project bootstrap",
            "desktop app project scaffold",
            "create project bootstrap");
        AddMatch(matches, "project_scaffold", 22, combinedText,
            "create dotnet project xunit",
            "create dotnet xunit project",
            "create dotnet test project");
        AddMatch(matches, "project_scaffold", 18, combinedText,
            "create class library",
            "create core project",
            "create contracts library",
            "create storage project",
            "create repository project");
        AddMatch(matches, prefersNative ? "native_project_bootstrap" : "project_scaffold", 18, combinedText,
            "native project bootstrap",
            "bootstrap native project",
            "create native project");
        AddMatch(matches, "project_scaffold", 18, combinedText,
            "create xunit project",
            "create test project",
            "scaffold tests",
            "add test project");
        AddMatch(matches, "cmake_bootstrap", 18, combinedText,
            "cmake bootstrap",
            "cmake project bootstrap",
            "bootstrap cmake",
            "create cmake project");

        AddMatch(matches, "ui_shell_sections", 20, combinedText,
            "required ui shell sections",
            "ui shell sections",
            "initial pages",
            "top level shell pages",
            "required shell pages",
            "top level app pages",
            "required shell behaviors",
            "shell behaviors",
            "shell behavior requirements");
        if (HasGroupedShellSectionsPageList(combinedText))
        {
            matches.Add(new PhraseFamilyMatch
            {
                PhraseFamily = "ui_shell_sections",
                Score = 18,
                Confidence = "high",
                Reason = "page_list"
            });
        }

        AddMatch(matches, "build_first_ui_shell", 18, combinedText,
            "build first ui shell",
            "first ui shell",
            "desktop app shell scaffold",
            "app shell scaffold",
            "shell scaffold",
            "desktop shell",
            "main window shell",
            "ui shell",
            "write shell",
            "create page",
            "create xaml page",
            "write page",
            "connect page to shell");
        AddMatch(matches, "core_domain_models_contracts", 18, combinedText,
            "core domain",
            "domain models",
            "models and contracts",
            "contracts and models",
            "domain contracts",
            "core contracts");
        AddMatch(matches, "core_domain_models_contracts", 18, combinedText,
            "required remediation types",
            "remediation types",
            "remediation models",
            "remediation contracts");
        AddMatch(matches, "core_domain_models_contracts", 18, combinedText,
            "required rules",
            "rule specifications",
            "policy contracts",
            "rules contract",
            "rules specification");
        if (LooksLikeCoreProjectReferenceScaffold(combinedText))
        {
            matches.Add(new PhraseFamilyMatch
            {
                PhraseFamily = "core_domain_models_contracts",
                Score = 22,
                Confidence = "high",
                Reason = "core_project_reference_scaffold"
            });
        }
        AddMatch(matches, "maintenance_context", 20, combinedText,
            "required context",
            "required baseline context",
            "required prior run data",
            "maintenance context packet",
            "context packet",
            "existing project intake context",
            "reuse prior run data",
            "baseline context");
        AddMatch(matches, "maintenance_context", 18, combinedText,
            "run summary",
            "summary artifact",
            "normalized run",
            "repair context",
            "prior run data",
            "build target evidence",
            "existing project");
        if (HasMaintenanceFeatureUpdateScope(combinedText))
        {
            matches.Add(new PhraseFamilyMatch
            {
                PhraseFamily = "maintenance_context",
                Score = 18,
                Confidence = "high",
                Reason = "maintenance_feature_scope"
            });
        }

        if (HasMaintenanceVerificationDiscipline(combinedText))
        {
            matches.Add(new PhraseFamilyMatch
            {
                PhraseFamily = "build_verify",
                Score = 18,
                Confidence = "high",
                Reason = "maintenance_verification_discipline"
            });
        }

        if (HasMaintenanceDataDisciplineScope(combinedText))
        {
            matches.Add(new PhraseFamilyMatch
            {
                PhraseFamily = "maintenance_context",
                Score = 18,
                Confidence = "high",
                Reason = "maintenance_data_discipline"
            });
        }

        AddMatch(matches, "repository_scaffold", 20, combinedText,
            "add project reference",
            "wire project reference",
            "attach reference",
            "add dependency reference",
            "reference core library from app",
            "add app reference to core");
        AddMatch(matches, "add_navigation_app_state", 18, combinedText,
            "notification event state",
            "notification event definitions",
            "notification handler registration",
            "event handler registrations",
            "guard policy wiring",
            "notification policy wiring");
        AddMatch(matches, "repository_scaffold", 18, combinedText,
            "repository scaffold",
            "repository interface",
            "repository implementation",
            "repository wiring",
            "write contract",
            "create contract",
            "write model",
            "create model",
            "write repository",
            "create repository implementation",
            "wire repository consumer",
            "complete workflow feature");
        AddMatch(matches, "add_navigation_app_state", 18, combinedText,
            "navigation",
            "app state",
            "viewmodel wiring",
            "wire navigation",
            "create viewmodel",
            "write viewmodel",
            "bind shell to state",
            "bind shell viewmodel",
            "bind missing viewmodel surface");
        AddMatch(matches, "setup_storage_layer", 18, combinedText,
            "storage layer",
            "sqlite storage boundary",
            "storage boundary",
            "local settings",
            "cached snapshots",
            "sqlite",
            "write store",
            "register service",
            "add missing registration",
            "settings persistence flow",
            "wire settings store into consumer");
        AddMatch(matches, "add_settings_page", 16, combinedText, "settings page");
        AddMatch(matches, "add_history_log_view", 16, combinedText, "history view", "log view", "history log", "history page", "log page");
        AddMatch(matches, "wire_dashboard", 16, combinedText, "wire dashboard", "dashboard wireup", "dashboard");
        AddMatch(matches, "check_runner", 18, combinedText,
            "check runner",
            "test runner",
            "check framework",
            "runner framework",
            "run dotnet test",
            "run test project",
            "verify tests",
            "validate tests",
            "rerun tests",
            "execute tests",
            "run solution tests",
            "run direct test target",
            "verify direct test target",
            "continue with missing target creation",
            "defer into prerequisite work",
            "create attach test",
            "rerun after prerequisite");
        AddMatch(matches, "findings_pipeline", 18, combinedText,
            "findings pipeline",
            "snapshot builder",
            "normalize findings",
            "findings normalization",
            "write check registry",
            "write findings normalizer");
        AddMatch(matches, "build_verify", 18, combinedText,
            "evidence of success",
            "proof of completion",
            "validation evidence",
            "acceptance evidence",
            "success evidence",
            "proof of success");
        AddMatch(matches, "build_verify", 18, combinedText,
            "required result artifacts",
            "result artifacts",
            "required output artifacts",
            "output artifacts",
            "generated outputs",
            "required deliverables",
            "completion artifacts",
            "result package",
            "result proof");
        AddMatch(matches, "build_verify", 18, combinedText,
            "build verify",
            "validate build",
            "run build",
            "build and test",
            "verify workspace",
            "confirm build output",
            "run dotnet build",
            "build solution",
            "verify build",
            "run workspace build verification",
            "rerun build",
            "check build",
            "ensure solution builds",
            "validate feature build",
            "validate page build",
            "validate shell build",
            "confirm settings store build");
        AddMatch(matches, "solution_graph_repair", 20, combinedText,
            "repair circular build dependency",
            "solution graph repair",
            "project graph repair",
            "repair build failure",
            "fix build failure",
            "inspect wiring and repair build failure",
            "repair test failure",
            "fix missing symbol",
            "reconcile generated symbol",
            "rerun verification after repair");
        AddMatch(matches, "solution_graph_repair", 18, combinedText,
            "msb4006",
            "circular dependency",
            "target dependency graph",
            "nuget targets");
        AddMatch(matches, "core_domain_models_contracts", 18, combinedText,
            "required future placeholders only",
            "future placeholders only",
            "template only future work",
            "stub only future work",
            "deferred spec only work");

        return matches
            .GroupBy(match => match.PhraseFamily, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.PhraseFamily, StringComparer.OrdinalIgnoreCase)
                .First())
            .ToList();
    }

    private static bool HasGroupedShellSectionsPageList(string text)
    {
        if (!MentionsAny(text, "shell", "page", "pages", "section", "sections", "screen", "screens"))
            return false;

        var pageCount = 0;
        if (MentionsAny(text, "dashboard"))
            pageCount++;
        if (MentionsAny(text, "findings"))
            pageCount++;
        if (MentionsAny(text, "history"))
            pageCount++;
        if (MentionsAny(text, "settings"))
            pageCount++;

        return pageCount >= 3;
    }

    private static bool HasMaintenanceFeatureUpdateScope(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasFeatureScopeTitle = MentionsAny(
            text,
            "allowed feature classes for this project",
            "allowed feature classes",
            "allowed feature class");
        var hasFlowTitle = MentionsAny(
            text,
            "required flow",
            "feature update flow",
            "patch repair flow",
            "bounded feature update flow");
        if (!hasFeatureScopeTitle && !hasFlowTitle)
            return false;

        if (hasFeatureScopeTitle)
        {
            return MentionsAny(
                text,
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
                "maintenance loop");
        }

        return MentionsAny(
            text,
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

    private static bool HasMaintenanceVerificationDiscipline(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasVerificationTitle = MentionsAny(
            text,
            "required checkpoints",
            "required checkpoint",
            "evidence of success",
            "proof of completion",
            "validation evidence",
            "acceptance evidence");
        var hasVerificationRule = MentionsAny(text, "required rule")
            && MentionsAny(
                text,
                "post mutation verification",
                "feature or repair is considered successful",
                "verification passes");
        if (!hasVerificationTitle && !hasVerificationRule)
            return false;

        return MentionsAny(
            text,
            "post mutation verification discipline",
            "after every patch update pass",
            "rerun and verify",
            "solution still builds",
            "desktop app still launches",
            "shell still loads",
            "key sections still render",
            "storage still initializes safely",
            "check runner still executes deterministically",
            "findings/history still persist and render",
            "warnings are tracked",
            "truthful terminal summary");
    }

    private static bool HasMaintenanceDataDisciplineScope(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (!MentionsAny(text, "required behavior", "required behaviors", "required outputs", "required output"))
            return false;

        return MentionsAny(
            text,
            "file touch tracking",
            "repeated touches",
            "skip with proof",
            "never skip repair",
            "terminal run summary",
            "normalized run record",
            "file touch rollup",
            "patch update artifacts",
            "warning metrics",
            "corpus ready export",
            "embedder ready index export");
    }

    private static void AddMatch(List<PhraseFamilyMatch> matches, string phraseFamily, int score, string text, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            if (!MatchesPattern(text, pattern))
                continue;

            matches.Add(new PhraseFamilyMatch
            {
                PhraseFamily = phraseFamily,
                Score = score,
                Confidence = score >= 16 ? "high" : "medium",
                Reason = $"keyword:{NormalizeCombinedText(pattern)}"
            });
            return;
        }
    }

    private static bool LooksLikeBroadBuilderPhrase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasVerb = BroadBuilderVerbs.Any(verb => MatchesPattern(text, verb));
        if (!hasVerb)
            return false;

        return MentionsAny(
            text,
            "shell",
            "solution",
            "project",
            "bootstrap",
            "navigation",
            "storage",
            "contract",
            "model",
            "settings",
            "history",
            "dashboard",
            "runner",
            "state",
            "sqlite",
            "cmake",
            "evidence",
            "proof",
            "validation",
            "artifact",
            "reference",
            "directory",
            "folder",
            "file",
            "page",
            "viewmodel",
            "test",
            "workflow",
            "consumer",
            "registration",
            "binding");
    }

    private static bool MentionsAny(string text, params string[] patterns)
    {
        return patterns.Any(pattern => MatchesPattern(text, pattern));
    }

    private static bool LooksLikeCoreProjectReferenceScaffold(string text)
    {
        if (MentionsAny(text, "repository", "snapshot", "sqlite", "storage"))
            return false;

        return MentionsAny(
                   text,
                   "add project reference",
                   "wire project reference",
                   "attach reference",
                   "reference core library from app",
                   "add app reference to core",
                   "add dependency reference")
               && MentionsAny(text, "core", "contracts", "models");
    }

    private static bool MatchesPattern(string text, string pattern)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(pattern))
            return false;

        var normalizedPattern = NormalizeCombinedText(pattern);
        var tokens = normalizedPattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 0 && ContainsOrderedTokens(text, tokens);
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

    private static string InferClosestKnownFamilyGroup(string normalizedPhraseText, string stackHintText)
    {
        if (MentionsAny(normalizedPhraseText, "solution", "sln"))
            return "solution_scaffold";
        if (MentionsAny(normalizedPhraseText, "project", "wpf", "class library", "client project", "desktop app", "window app"))
            return MentionsAny(normalizedPhraseText, "test", "xunit") ? "check_runner" : "project_scaffold";
        if (MentionsAny(normalizedPhraseText, "reference", "dependency"))
            return LooksLikeCoreProjectReferenceScaffold(normalizedPhraseText)
                ? "core_domain_models_contracts"
                : "repository_scaffold";
        if (MentionsAny(normalizedPhraseText, "build", "compile", "restore", "validate build", "verify build"))
            return "build_verify";
        if (MentionsAny(normalizedPhraseText, "test", "runner", "verify tests", "validate tests"))
            return "check_runner";
        if (MentionsAny(normalizedPhraseText, "repair", "fix", "missing symbol", "reconcile"))
            return "solution_graph_repair";
        if (MentionsAny(normalizedPhraseText, "storage", "sqlite", "store", "settings persistence"))
            return "setup_storage_layer";
        if (MentionsAny(normalizedPhraseText, "repository", "contract", "model"))
            return "repository_scaffold";
        if (MentionsAny(normalizedPhraseText, "viewmodel", "app state", "navigation", "binding"))
            return "add_navigation_app_state";
        if (MentionsAny(normalizedPhraseText, "page", "shell", "dashboard", "history", "settings"))
            return "build_first_ui_shell";
        if (MentionsAny(stackHintText, "cmake", "native", "cpp"))
            return "native_project_bootstrap";

        return "";
    }

    private static string BuildNoRuleMessage(string rawPhraseText, string normalizedPhraseText, string targetStack, string stackHintText, string closestKnownFamilyGroup)
    {
        var stack = FirstNonEmpty(NormalizeValue(targetStack), InferStackLabel(stackHintText), "unknown");
        return $"No deterministic phrase-family rule exists yet for stack={stack}. {BuildPhraseTraceSummary(rawPhraseText, normalizedPhraseText, closestKnownFamilyGroup)}";
    }

    private static string BuildPhraseTraceSummary(string rawPhraseText, string normalizedPhraseText, string closestKnownFamilyGroup)
    {
        return $"raw={DisplayValue(rawPhraseText)} normalized={DisplayValue(normalizedPhraseText)} closest={DisplayValue(FirstNonEmpty(closestKnownFamilyGroup, "(none)"))}";
    }

    private static string InferStackLabel(string text)
    {
        if (MentionsAny(text, "c++", "cpp", "cmake", "win32", "native"))
            return "native_cpp_desktop";
        if (MentionsAny(text, "wpf", "dotnet", "windows app sdk", "xaml", "csharp", "c#"))
            return "dotnet_desktop";
        if (MentionsAny(text, "ansi c", " c ", " c,", " c."))
            return "c_app";
        return "";
    }

    private static string CombineRawText(params string?[] parts)
    {
        return Regex.Replace(
                string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part))),
                "\\s+",
                " ")
            .Trim();
    }

    private static string NormalizeText(params string?[] parts)
    {
        return NormalizeCombinedText(CombineRawText(parts));
    }

    private static string NormalizeCombinedText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value.ToLowerInvariant();
        normalized = normalized.Replace('\\', '/');
        normalized = normalized.Replace("c++", "cpp");
        normalized = normalized.Replace("c#", "csharp");
        normalized = Regex.Replace(normalized, @"\b[a-z0-9_.-]+\.sln\b", " solution ");
        normalized = Regex.Replace(normalized, @"\b[a-z0-9_.-]+\.csproj\b", " project ");
        normalized = Regex.Replace(normalized, @"\b[a-z0-9_.-]+\.(xaml|cs|cpp|h)\b", " file ");
        normalized = normalized.Replace("-", " ");
        normalized = normalized.Replace("_", " ");
        normalized = normalized.Replace("/", " ");
        normalized = Regex.Replace(normalized, @"[""']", " ");
        normalized = Regex.Replace(normalized, @"[,:;!?\(\)\[\]\{\}]", " ");
        normalized = Regex.Replace(normalized, @"\b(?:the|a|an|please|now)\b", " ");
        normalized = Regex.Replace(normalized, @"\bnew\b", " create ");
        normalized = Regex.Replace(normalized, "\\s+", " ");
        return normalized.Trim();
    }

    private static string NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return Regex.Replace(value, "\\s+", " ").Trim().ToLowerInvariant();
    }

    private static string BuildNormalizationSummary(string rawPhraseText, string normalizedPhraseText)
    {
        return $"normalized_phrase={DisplayValue(normalizedPhraseText)} raw_phrase={DisplayValue(rawPhraseText)}";
    }

    private static string DisplayValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(none)";

        const int maxLength = 140;
        return value.Length <= maxLength
            ? $"`{value}`"
            : $"`{value[..(maxLength - 3)]}...`";
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

    private sealed class PhraseFamilyMatch
    {
        public string PhraseFamily { get; init; } = "";
        public int Score { get; init; }
        public string Confidence { get; init; } = "";
        public string Reason { get; init; } = "";
    }
}
