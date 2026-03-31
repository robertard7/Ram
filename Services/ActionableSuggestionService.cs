using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class ActionableSuggestionService
{
    private readonly ArtifactClassificationService _artifactClassificationService = new();
    private readonly BuildSystemDetectionService _buildSystemDetectionService = new();
    private readonly LatestActionableStateService _latestActionableStateService;
    private readonly NextActionSuggestionService _nextActionSuggestionService = new();

    public ActionableSuggestionService()
    {
        _latestActionableStateService = new LatestActionableStateService(_artifactClassificationService);
    }

    public List<ActionableSuggestionRecord> BuildSuggestions(
        string workspaceRoot,
        RamDbService ramDbService,
        ToolChainRecord? chain = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return [];

        var snapshot = _latestActionableStateService.GetLatestState(workspaceRoot, ramDbService);
        return BuildSuggestions(workspaceRoot, snapshot, ramDbService, chain);
    }

    public List<ActionableSuggestionRecord> BuildSuggestions(
        string workspaceRoot,
        LatestActionableStateRecord snapshot,
        RamDbService ramDbService,
        ToolChainRecord? chain = null)
    {
        var context = LoadContext(workspaceRoot, ramDbService);
        var suggestions = new List<ActionableSuggestionRecord>();

        AddReadySuggestionsFromState(suggestions, workspaceRoot, snapshot, chain);
        AddFailureChainSuggestions(suggestions, snapshot, context, chain);
        AddWorkspaceBootstrapSuggestions(suggestions, snapshot, context, chain);
        AddAutoValidationSuggestions(suggestions, snapshot, context, chain);
        AddSuccessContextSuggestions(suggestions, snapshot, chain);

        return suggestions
            .GroupBy(BuildDeduplicationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(suggestion => suggestion.Priority)
                .ThenBy(suggestion => ReadinessOrder(suggestion.Readiness))
                .First())
            .OrderBy(suggestion => ReadinessOrder(suggestion.Readiness))
            .ThenBy(suggestion => suggestion.Priority)
            .ThenBy(suggestion => suggestion.Title, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private void AddReadySuggestionsFromState(
        List<ActionableSuggestionRecord> suggestions,
        string workspaceRoot,
        LatestActionableStateRecord snapshot,
        ToolChainRecord? chain)
    {
        var legacySuggestions = _nextActionSuggestionService.Suggest(workspaceRoot, snapshot.ExecutionState);
        var priority = 10;
        foreach (var suggestion in legacySuggestions)
        {
            AddSuggestion(
                suggestions,
                chain,
                snapshot.WorkspaceRoot,
                "tool",
                suggestion.Title,
                suggestion.SuggestedPrompt,
                suggestion.ToolName,
                "",
                suggestion.Reason,
                SuggestionReadiness.ReadyNow,
                "",
                manualOnly: false,
                priority: priority);
            priority += 10;
        }
    }

    private void AddFailureChainSuggestions(
        List<ActionableSuggestionRecord> suggestions,
        LatestActionableStateRecord snapshot,
        SuggestionContext context,
        ToolChainRecord? chain)
    {
        if (!snapshot.HasFailureContext)
        {
            var reason = snapshot.LatestResultKind == "success"
                ? "The latest recorded build or test result succeeded, so no failure chain exists yet."
                : snapshot.LatestResultKind == "safety_abort"
                    ? "The latest recorded run was safety-aborted before producing a repairable failure chain."
                    : "No recorded build or test failure is currently stored for this workspace.";

            AddSuggestion(
                suggestions,
                chain,
                snapshot.WorkspaceRoot,
                "inspection",
                "Show me the broken file",
                "show me the broken file",
                "open_failure_context",
                "repair_single_step",
                "Open the top failure file when a real failure chain exists.",
                SuggestionReadiness.Blocked,
                reason,
                manualOnly: false,
                priority: 210);
            AddSuggestion(
                suggestions,
                chain,
                snapshot.WorkspaceRoot,
                "chain",
                "How should I fix this",
                "how should I fix this",
                "plan_repair",
                "repair_single_step",
                "Generate a targeted repair plan from a recorded failure.",
                SuggestionReadiness.Blocked,
                reason,
                manualOnly: false,
                priority: 220);
            AddSuggestion(
                suggestions,
                chain,
                snapshot.WorkspaceRoot,
                "chain",
                "Show me the patch",
                "show me the patch",
                "preview_patch_draft",
                "repair_preview_chain",
                "Preview a patch draft after RAM has a repair proposal.",
                SuggestionReadiness.Blocked,
                reason,
                manualOnly: false,
                priority: 230);
            return;
        }

        AddSuggestion(
            suggestions,
            chain,
            snapshot.WorkspaceRoot,
            "inspection",
            "Show me the broken file",
            "show me the broken file",
            "open_failure_context",
            "repair_single_step",
            "Open the highest-confidence failure file from the current repair context.",
            SuggestionReadiness.ReadyNow,
            "",
            manualOnly: false,
            priority: 20);
        AddSuggestion(
            suggestions,
            chain,
            snapshot.WorkspaceRoot,
            "chain",
            "How should I fix this",
            "how should I fix this",
            "plan_repair",
            "repair_single_step",
            "Build a targeted repair plan from the current failure chain.",
            SuggestionReadiness.ReadyNow,
            "",
            manualOnly: false,
            priority: 30);

        var previewReadiness = context.LatestPatchDraft is not null || context.LatestRepairProposal is not null
            ? SuggestionReadiness.ReadyNow
            : SuggestionReadiness.NeedsPrerequisite;
        var previewReason = previewReadiness == SuggestionReadiness.ReadyNow
            ? ""
            : "Generate a repair proposal first so RAM has a patch draft to preview.";

        AddSuggestion(
            suggestions,
            chain,
            snapshot.WorkspaceRoot,
            "chain",
            "Show me the patch",
            "show me the patch",
            "preview_patch_draft",
            "repair_preview_chain",
            "Preview the current repair patch draft.",
            previewReadiness,
            previewReason,
            manualOnly: false,
            priority: 40);

        if (context.LatestPatchDraft is not null && context.LatestPatchDraft.CanApplyLocally)
        {
            AddSuggestion(
                suggestions,
                chain,
                snapshot.WorkspaceRoot,
                "chain",
                "Apply the fix",
                "apply the fix",
                "apply_patch_draft",
                "repair_single_step",
                "Apply the current locally applicable patch draft.",
                SuggestionReadiness.ReadyNow,
                "",
                manualOnly: false,
                priority: 50);
        }
        else if (context.LatestPatchDraft is not null)
        {
            AddSuggestion(
                suggestions,
                chain,
                snapshot.WorkspaceRoot,
                "manual_hint",
                "Apply the fix",
                "apply the fix",
                "apply_patch_draft",
                "repair_single_step",
                "The current patch draft exists, but RAM cannot apply it locally.",
                SuggestionReadiness.Blocked,
                "The latest patch draft requires manual review or model-assisted editing before it can be applied.",
                manualOnly: true,
                priority: 60);
        }
        else
        {
            AddSuggestion(
                suggestions,
                chain,
                snapshot.WorkspaceRoot,
                "chain",
                "Apply the fix",
                "apply the fix",
                "apply_patch_draft",
                "repair_single_step",
                "Apply a patch after RAM has created a patch draft.",
                SuggestionReadiness.NeedsPrerequisite,
                "Generate and preview a patch draft first.",
                manualOnly: false,
                priority: 60);
        }

        if (context.LatestPatchApplyResult is not null)
        {
            AddSuggestion(
                suggestions,
                chain,
                snapshot.WorkspaceRoot,
                "chain",
                "Did that fix it",
                "did that fix it",
                "verify_patch_draft",
                "verification",
                "Verify the latest applied patch against the recorded failure target.",
                SuggestionReadiness.ReadyNow,
                "",
                manualOnly: false,
                priority: 70);
        }
        else
        {
            AddSuggestion(
                suggestions,
                chain,
                snapshot.WorkspaceRoot,
                "chain",
                "Did that fix it",
                "did that fix it",
                "verify_patch_draft",
                "verification",
                "Verification becomes available after a patch apply result exists.",
                SuggestionReadiness.NeedsPrerequisite,
                "No patch has been applied yet for this repair chain.",
                manualOnly: false,
                priority: 70);
        }
    }

    private void AddWorkspaceBootstrapSuggestions(
        List<ActionableSuggestionRecord> suggestions,
        LatestActionableStateRecord snapshot,
        SuggestionContext context,
        ToolChainRecord? chain)
    {
        var detectionUnknown = context.DetectionResult?.DetectedType is null or BuildSystemType.Unknown;
        var noPreferredProfile = context.DetectionResult?.PreferredProfile is null;
        var shouldOfferBootstrap = snapshot.LatestResultKind == "none"
            || string.Equals(snapshot.LatestBuildFamily, "unknown", StringComparison.OrdinalIgnoreCase)
            || detectionUnknown
            || noPreferredProfile
            || (context.AutoValidationResult is not null
                && string.Equals(context.AutoValidationResult.OutcomeClassification, "manual_only", StringComparison.OrdinalIgnoreCase));

        if (!shouldOfferBootstrap)
            return;

        AddSuggestion(
            suggestions,
            chain,
            snapshot.WorkspaceRoot,
            "tool",
            "Detect build system",
            "detect build system",
            "detect_build_system",
            "build_execution_single_step",
            "Refresh RAM's local build-family detection for this workspace.",
            SuggestionReadiness.ReadyNow,
            "",
            manualOnly: false,
            priority: 80);
        AddSuggestion(
            suggestions,
            chain,
            snapshot.WorkspaceRoot,
            "tool",
            "List build profiles",
            "list build profiles",
            "list_build_profiles",
            "build_profile_chain",
            "Show the known build profiles RAM can use safely.",
            SuggestionReadiness.ReadyNow,
            "",
            manualOnly: false,
            priority: 90);
        AddSuggestion(
            suggestions,
            chain,
            snapshot.WorkspaceRoot,
            "inspection",
            "What safe build target can I run",
            "what safe build target can I run",
            "",
            "",
            "Ask RAM for the safest currently justifiable build target.",
            SuggestionReadiness.ReadyNow,
            "",
            manualOnly: false,
            priority: 100);

        if (noPreferredProfile)
        {
            AddSuggestion(
                suggestions,
                chain,
                snapshot.WorkspaceRoot,
                "tool",
                "Run build",
                "run build",
                "run_build",
                "build_execution_single_step",
                "Run a workspace build after RAM can justify a safe target.",
                SuggestionReadiness.Blocked,
                "No preferred build profile or safe narrow build target is currently recorded for this workspace.",
                manualOnly: false,
                priority: 240);
        }
    }

    private void AddAutoValidationSuggestions(
        List<ActionableSuggestionRecord> suggestions,
        LatestActionableStateRecord snapshot,
        SuggestionContext context,
        ToolChainRecord? chain)
    {
        var result = context.AutoValidationResult;
        if (result is null)
            return;

        if (string.Equals(result.OutcomeClassification, "manual_only", StringComparison.OrdinalIgnoreCase))
        {
            AddSuggestion(
                suggestions,
                chain,
                snapshot.WorkspaceRoot,
                "manual_hint",
                "Native validation stayed manual-only",
                "",
                "",
                "",
                FirstNonEmpty(
                    result.SuggestedNextStep,
                    "RAM recorded the change but did not auto-run native validation because no tiny safe target was proven."),
                SuggestionReadiness.ManualOnly,
                "Native post-change validation requires a narrow manual target selection first.",
                manualOnly: true,
                priority: 110);
        }
        else if (string.Equals(result.OutcomeClassification, "scope_blocked", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.OutcomeClassification, "safety_blocked", StringComparison.OrdinalIgnoreCase))
        {
            AddSuggestion(
                suggestions,
                chain,
                snapshot.WorkspaceRoot,
                "manual_hint",
                "Auto-validation was blocked",
                "",
                "",
                "",
                FirstNonEmpty(
                    result.SuggestedNextStep,
                    "RAM blocked automatic validation because the inferred validation target was too broad or unsafe."),
                SuggestionReadiness.Blocked,
                FirstNonEmpty(result.Explanation, "The most likely validation target was not safe to run automatically."),
                manualOnly: false,
                priority: 120);
        }
    }

    private void AddSuccessContextSuggestions(
        List<ActionableSuggestionRecord> suggestions,
        LatestActionableStateRecord snapshot,
        ToolChainRecord? chain)
    {
        if (snapshot.LatestResultKind != "success")
            return;

        AddSuggestion(
            suggestions,
            chain,
            snapshot.WorkspaceRoot,
            "manual_hint",
            "Continue editing",
            "",
            "",
            "",
            "The latest recorded workspace action completed successfully.",
            SuggestionReadiness.InformationalOnly,
            "",
            manualOnly: false,
            priority: 300);
    }

    private static void AddSuggestion(
        List<ActionableSuggestionRecord> suggestions,
        ToolChainRecord? chain,
        string workspaceRoot,
        string suggestionKind,
        string title,
        string promptText,
        string targetToolName,
        string targetChainTemplate,
        string rationale,
        SuggestionReadiness readiness,
        string blockedReason,
        bool manualOnly,
        int priority)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(promptText))
            return;

        suggestions.Add(new ActionableSuggestionRecord
        {
            SuggestionId = Guid.NewGuid().ToString("N"),
            SourceChainId = chain?.ChainId ?? "",
            WorkspaceRoot = workspaceRoot,
            SuggestionKind = suggestionKind,
            Title = title,
            PromptText = promptText,
            TargetToolName = targetToolName,
            TargetChainTemplate = targetChainTemplate,
            ShortRationale = rationale,
            Readiness = readiness,
            BlockedReason = blockedReason,
            ManualOnly = manualOnly || readiness == SuggestionReadiness.ManualOnly,
            Priority = priority
        });
    }

    private SuggestionContext LoadContext(string workspaceRoot, RamDbService ramDbService)
    {
        BuildSystemDetectionResult? detection = null;
        try
        {
            detection = _buildSystemDetectionService.Detect(workspaceRoot);
        }
        catch
        {
            detection = null;
        }

        return new SuggestionContext
        {
            DetectionResult = detection,
            LatestRepairProposal = LoadArtifactPayload<RepairProposalRecord>(ramDbService, workspaceRoot, "repair_proposal"),
            LatestPatchDraft = LoadArtifactPayload<PatchDraftRecord>(ramDbService, workspaceRoot, "patch_draft"),
            LatestPatchApplyResult = LoadArtifactPayload<PatchApplyResultRecord>(ramDbService, workspaceRoot, "patch_apply_result"),
            AutoValidationResult = LoadArtifactPayload<AutoValidationResultRecord>(ramDbService, workspaceRoot, "auto_validation_result")
        };
    }

    private static T? LoadArtifactPayload<T>(RamDbService ramDbService, string workspaceRoot, string artifactType)
    {
        var artifact = ramDbService.LoadLatestArtifactByType(workspaceRoot, artifactType);
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.Content))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(artifact.Content);
        }
        catch
        {
            return default;
        }
    }

    private static string BuildDeduplicationKey(ActionableSuggestionRecord suggestion)
    {
        return string.Join(
            "|",
            suggestion.Readiness,
            suggestion.Title,
            suggestion.PromptText,
            suggestion.TargetToolName,
            suggestion.TargetChainTemplate);
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

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private sealed class SuggestionContext
    {
        public BuildSystemDetectionResult? DetectionResult { get; init; }
        public RepairProposalRecord? LatestRepairProposal { get; init; }
        public PatchDraftRecord? LatestPatchDraft { get; init; }
        public PatchApplyResultRecord? LatestPatchApplyResult { get; init; }
        public AutoValidationResultRecord? AutoValidationResult { get; init; }
    }
}
