using System.Text;
using RAM.Models;

namespace RAM.Services;

public static class TaskboardStructuralHeadingService
{
    private static readonly Dictionary<string, (string CanonicalAlias, TaskboardHeadingClass HeadingClass, TaskboardHeadingTreatment Treatment)> ExactAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["goal"] = ("goal_scope", TaskboardHeadingClass.StructuralSupport, TaskboardHeadingTreatment.Covered),
            ["rerun goal"] = ("goal_scope", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),
            ["objective"] = ("goal_scope", TaskboardHeadingClass.StructuralSupport, TaskboardHeadingTreatment.Covered),
            ["purpose"] = ("goal_scope", TaskboardHeadingClass.StructuralSupport, TaskboardHeadingTreatment.Covered),
            ["intent"] = ("goal_scope", TaskboardHeadingClass.StructuralSupport, TaskboardHeadingTreatment.Covered),
            ["scope"] = ("goal_scope", TaskboardHeadingClass.StructuralSupport, TaskboardHeadingTreatment.Covered),
            ["overview"] = ("goal_scope", TaskboardHeadingClass.StructuralSupport, TaskboardHeadingTreatment.Covered),
            ["summary"] = ("goal_scope", TaskboardHeadingClass.StructuralSupport, TaskboardHeadingTreatment.Covered),
            ["outcome"] = ("goal_scope", TaskboardHeadingClass.StructuralSupport, TaskboardHeadingTreatment.Covered),
            ["expected result"] = ("goal_scope", TaskboardHeadingClass.StructuralSupport, TaskboardHeadingTreatment.Covered),

            ["required context"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["project context"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["context"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["current context"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["required baseline context"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["required prior run data"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["maintenance context packet"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["context packet"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["existing project intake context"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["reuse prior run data"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["baseline context"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["background"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["current state"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["known issues"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["dependencies"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["inputs"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["assumptions"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["environment"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),
            ["reference"] = ("context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly),

            ["required rules"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["required rule"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["rules required"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["project rules"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["rules for this project"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["constraints"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["restrictions"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["guardrails"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["allowed actions"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["disallowed actions"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["safety"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["boundaries"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["acceptance criteria"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["allowed feature classes for this project"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["allowed feature classes"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),
            ["allowed feature class"] = ("constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly),

            ["evidence of success"] = ("validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly),
            ["success evidence"] = ("validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly),
            ["proof of success"] = ("validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly),
            ["proof of completion"] = ("validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly),
            ["success criteria"] = ("validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly),
            ["required outputs"] = ("validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly),
            ["expected outputs"] = ("validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly),
            ["output requirements"] = ("validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly),
            ["deliverables"] = ("validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly),
            ["validation"] = ("validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly),
            ["verification"] = ("validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly),
            ["proof"] = ("validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly),
            ["definition of done"] = ("validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly),
            ["completion conditions"] = ("validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly),

            ["required flow"] = ("conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),
            ["execution flow"] = ("conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),
            ["expected flow"] = ("conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),
            ["flow"] = ("conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),
            ["required behavior"] = ("conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),
            ["required behaviors"] = ("conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),
            ["required checkpoints"] = ("conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),
            ["required checkpoint"] = ("conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),
            ["next steps"] = ("conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),
            ["follow up"] = ("conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),
            ["followup"] = ("conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),
            ["fallback"] = ("conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),
            ["recovery"] = ("conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),
            ["rerun"] = ("conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly),

            ["required models"] = ("scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered),
            ["required model"] = ("scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered),
            ["required interfaces contracts"] = ("scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered),
            ["required interfaces"] = ("scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered),
            ["required contracts"] = ("scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered),
            ["required enum rules"] = ("scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered),
            ["required remediation types"] = ("scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered),
            ["required files"] = ("scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered),
            ["required folders"] = ("scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered),
            ["required services"] = ("scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered),
            ["required storage"] = ("scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered),
            ["required ui"] = ("scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered),
            ["required classes"] = ("scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered),
            ["required types"] = ("scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered),

            ["allowed repair classes for this phase"] = ("repair_support", TaskboardHeadingClass.RepairSupport, TaskboardHeadingTreatment.Covered),
            ["allowed repair classes"] = ("repair_support", TaskboardHeadingClass.RepairSupport, TaskboardHeadingTreatment.Covered),
            ["repair target"] = ("repair_support", TaskboardHeadingClass.RepairSupport, TaskboardHeadingTreatment.Covered),
            ["patch scope"] = ("repair_support", TaskboardHeadingClass.RepairSupport, TaskboardHeadingTreatment.Covered),
            ["mutation scope"] = ("repair_support", TaskboardHeadingClass.RepairSupport, TaskboardHeadingTreatment.Covered),
            ["affected files"] = ("repair_support", TaskboardHeadingClass.RepairSupport, TaskboardHeadingTreatment.Covered),
            ["files to inspect"] = ("repair_support", TaskboardHeadingClass.RepairSupport, TaskboardHeadingTreatment.Covered),
            ["files to modify"] = ("repair_support", TaskboardHeadingClass.RepairSupport, TaskboardHeadingTreatment.Covered),
            ["repair constraints"] = ("repair_support", TaskboardHeadingClass.RepairSupport, TaskboardHeadingTreatment.Covered)
        };

    public static TaskboardHeadingPolicyRecord Classify(string? title)
    {
        var originalTitle = title ?? "";
        var normalized = NormalizeHeadingTitle(title);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new TaskboardHeadingPolicyRecord
            {
                OriginalTitle = originalTitle,
                NormalizedTitle = "",
                CanonicalAlias = "",
                HeadingClass = TaskboardHeadingClass.Unknown,
                Treatment = TaskboardHeadingTreatment.Unknown,
                ReasonCode = "heading_missing",
                IsKnownAliasOrPattern = false
            };
        }

        if (ExactAliases.TryGetValue(normalized, out var exact))
            return BuildPolicyRecord(originalTitle, normalized, exact.CanonicalAlias, exact.HeadingClass, exact.Treatment, true);

        var tokens = BuildTokenSet(normalized);
        if (StartsWithActionableVerb(normalized))
            return BuildPolicyRecord(originalTitle, normalized, "actionable", TaskboardHeadingClass.Actionable, TaskboardHeadingTreatment.Actionable, false);

        if (MatchesScaffoldSupport(normalized, tokens))
            return BuildPolicyRecord(originalTitle, normalized, "scaffold_support", TaskboardHeadingClass.ScaffoldSupport, TaskboardHeadingTreatment.Covered, true);

        if (MatchesRepairSupport(normalized, tokens))
            return BuildPolicyRecord(originalTitle, normalized, "repair_support", TaskboardHeadingClass.RepairSupport, TaskboardHeadingTreatment.Covered, true);

        if (MatchesContextReference(normalized, tokens))
            return BuildPolicyRecord(originalTitle, normalized, "context_reference", TaskboardHeadingClass.ContextReference, TaskboardHeadingTreatment.ContextOnly, true);

        if (MatchesConstraintPolicy(normalized, tokens))
            return BuildPolicyRecord(originalTitle, normalized, "constraint_policy", TaskboardHeadingClass.ConstraintPolicy, TaskboardHeadingTreatment.ConstraintOnly, true);

        if (MatchesValidationProof(normalized, tokens))
            return BuildPolicyRecord(originalTitle, normalized, "validation_proof", TaskboardHeadingClass.ValidationProof, TaskboardHeadingTreatment.ProofOnly, true);

        if (MatchesConditionalFollowup(normalized, tokens))
            return BuildPolicyRecord(originalTitle, normalized, "conditional_followup", TaskboardHeadingClass.ConditionalFollowup, TaskboardHeadingTreatment.ConditionalOnly, true);

        if (MatchesStructuralSupport(normalized, tokens))
            return BuildPolicyRecord(originalTitle, normalized, "goal_scope", TaskboardHeadingClass.StructuralSupport, TaskboardHeadingTreatment.Covered, true);

        return BuildPolicyRecord(originalTitle, normalized, "actionable", TaskboardHeadingClass.Actionable, TaskboardHeadingTreatment.Actionable, false);
    }

    public static bool IsMaintenanceStructuralSupportTitle(string? title)
    {
        return IsStructuralSupportTitle(title);
    }

    public static bool IsScaffoldStructuralSupportTitle(string? title)
    {
        return IsStructuralSupportTitle(title);
    }

    public static bool IsStructuralSupportTitle(string? title)
    {
        return IsNonActionableHeading(Classify(title));
    }

    public static bool IsNonActionableHeading(string? title)
    {
        return IsNonActionableHeading(Classify(title));
    }

    public static bool IsNonActionableHeading(TaskboardHeadingPolicyRecord policy)
    {
        return policy.IsNonActionableSupport;
    }

    public static string BuildSupportCoverageReason(string? title)
    {
        return BuildSupportCoverageReason(Classify(title));
    }

    public static string BuildSupportCoverageReason(TaskboardHeadingPolicyRecord policy)
    {
        var label = string.IsNullOrWhiteSpace(policy.OriginalTitle) ? "(unknown heading)" : policy.OriginalTitle.Trim();
        var normalized = FirstNonEmpty(policy.NormalizedTitle, "(unknown)");
        var headingClass = FormatHeadingClass(policy.HeadingClass);
        var treatment = FormatHeadingTreatment(policy.Treatment);

        var detail = policy.HeadingClass switch
        {
            TaskboardHeadingClass.ConstraintPolicy =>
                "rules, allowed scope, and guard boundaries remain enforced without standalone execution.",
            TaskboardHeadingClass.ContextReference =>
                "baseline, retrieval, dependencies, and prior-run context remain covered by the parent actionable flow.",
            TaskboardHeadingClass.ValidationProof =>
                "verification, proof, and completion expectations remain covered by the strongest actionable execution evidence.",
            TaskboardHeadingClass.ConditionalFollowup =>
                "this section shapes conditional branching or rerun expectations and does not execute as standalone work.",
            TaskboardHeadingClass.ScaffoldSupport =>
                "scaffold structure and contract expectations remain covered without redundant make_dir, attach, or write churn.",
            TaskboardHeadingClass.RepairSupport =>
                "repair scope, affected-file constraints, and mutation boundaries remain covered without standalone filler execution.",
            TaskboardHeadingClass.StructuralSupport =>
                "this section is structural guidance covered by the parent executable flow.",
            _ =>
                "it is non-actionable support guidance covered by the parent runtime path."
        };

        return $"covered_support_heading: normalized={normalized} class={headingClass} treatment={treatment} Folded heading `{label}` into the active executable flow; {detail}";
    }

    public static string BuildActionableReason(string? title)
    {
        return BuildActionableReason(Classify(title));
    }

    public static string BuildActionableReason(TaskboardHeadingPolicyRecord policy)
    {
        var label = string.IsNullOrWhiteSpace(policy.OriginalTitle) ? "(unknown heading)" : policy.OriginalTitle.Trim();
        var normalized = FirstNonEmpty(policy.NormalizedTitle, "(unknown)");
        return $"actionable_heading: normalized={normalized} class={FormatHeadingClass(policy.HeadingClass)} treatment={FormatHeadingTreatment(policy.Treatment)} Heading `{label}` remains executable work in the unified C# runtime.";
    }

    public static string BuildRuntimeBanner(TaskboardHeadingPolicyRecord policy)
    {
        if (policy.HeadingClass == TaskboardHeadingClass.Unknown && string.IsNullOrWhiteSpace(policy.OriginalTitle))
            return "";

        return $"Heading policy: title={FirstNonEmpty(policy.OriginalTitle, "(none)")} normalized={FirstNonEmpty(policy.NormalizedTitle, "(none)")} alias={FirstNonEmpty(policy.CanonicalAlias, "(none)")} class={FormatHeadingClass(policy.HeadingClass)} treatment={FormatHeadingTreatment(policy.Treatment)} reason={FirstNonEmpty(policy.ReasonCode, "(none)")}";
    }

    public static string ResolveActionableFollowupPhraseFamily(
        string? title,
        string? summary = null,
        string? promptText = null)
    {
        var policy = Classify(title);
        if (policy.IsActionable)
            return "";

        var combined = NormalizeHeadingTitle(string.Join(" ", new[] { title ?? "", summary ?? "", promptText ?? "" }));
        if (string.IsNullOrWhiteSpace(combined))
            return "";

        var tokens = BuildTokenSet(combined);
        var driverHeading = policy.HeadingClass is TaskboardHeadingClass.ConstraintPolicy
            or TaskboardHeadingClass.ContextReference
            or TaskboardHeadingClass.ValidationProof
            or TaskboardHeadingClass.ConditionalFollowup
            or TaskboardHeadingClass.ScaffoldSupport;
        var explicitNamedPage = ContainsAny(tokens, "dashboard", "findings", "history", "log", "settings");
        if (!driverHeading && !(policy.HeadingClass == TaskboardHeadingClass.StructuralSupport && explicitNamedPage))
            return "";

        if (ContainsAny(tokens, "test", "tests", "check", "registry", "assert"))
            return "check_runner";

        if (ContainsAny(tokens, "snapshot", "normalizer", "finding", "findings", "pipeline"))
            return "findings_pipeline";

        if (ContainsAny(tokens, "repository", "contract", "contracts", "model", "models")
            && !ContainsAny(tokens, "ui", "page", "view", "dashboard", "history", "settings", "finding", "findings"))
        {
            return "repository_scaffold";
        }

        if (ContainsAny(tokens, "storage", "sqlite", "persistence", "database", "db"))
            return "setup_storage_layer";

        if (ContainsAny(tokens, "state", "bucket", "viewmodel", "viewmodels", "navigation", "shellbehavior", "shellbehaviors")
            || (ContainsAny(tokens, "shell") && ContainsAny(tokens, "behavior", "behaviors")))
        {
            return "add_navigation_app_state";
        }

        var touchesUiSurface = ContainsAny(tokens, "dashboard", "findings", "history", "log", "settings", "shell", "page", "view", "viewmodel", "window", "mainwindow", "navigation", "state", "ui");
        if (!touchesUiSurface)
            return "";

        if (ContainsAny(tokens, "dashboard") && !ContainsAny(tokens, "findings", "history", "log", "settings"))
            return "wire_dashboard";

        if (ContainsAny(tokens, "history", "log") && !ContainsAny(tokens, "dashboard", "findings", "settings"))
            return "add_history_log_view";

        if (ContainsAny(tokens, "settings") && !ContainsAny(tokens, "dashboard", "findings", "history", "log"))
            return "add_settings_page";

        return "ui_shell_sections";
    }

    private static TaskboardHeadingPolicyRecord BuildPolicyRecord(
        string originalTitle,
        string normalizedTitle,
        string canonicalAlias,
        TaskboardHeadingClass headingClass,
        TaskboardHeadingTreatment treatment,
        bool isKnownAliasOrPattern)
    {
        return new TaskboardHeadingPolicyRecord
        {
            OriginalTitle = originalTitle,
            NormalizedTitle = normalizedTitle,
            CanonicalAlias = canonicalAlias,
            HeadingClass = headingClass,
            Treatment = treatment,
            ReasonCode = headingClass == TaskboardHeadingClass.Actionable ? "actionable_heading" : "covered_support_heading",
            IsKnownAliasOrPattern = isKnownAliasOrPattern
        };
    }

    private static bool MatchesStructuralSupport(string normalizedTitle, IReadOnlySet<string> tokens)
    {
        return IsPhrase(normalizedTitle, "recommended implementation detail")
               || IsPhrase(normalizedTitle, "recommended detail")
               || IsPhrase(normalizedTitle, "recommended follow up detail")
               || IsPhrase(normalizedTitle, "recommended followup detail")
               || ContainsAny(tokens, "goal", "objective", "purpose", "intent", "scope", "overview", "summary", "outcome")
               || IsPhrase(normalizedTitle, "expected result");
    }

    private static bool MatchesConstraintPolicy(string normalizedTitle, IReadOnlySet<string> tokens)
    {
        return IsPhrase(normalizedTitle, "acceptance criteria")
               || IsPhrase(normalizedTitle, "allowed actions")
               || IsPhrase(normalizedTitle, "disallowed actions")
               || IsPhrase(normalizedTitle, "allowed feature classes")
               || IsPhrase(normalizedTitle, "allowed feature classes for this project")
               || (ContainsAny(tokens, "rule", "policy", "guard", "constraint", "restriction", "boundary", "guardrail", "safety")
                   && !ContainsAny(tokens, "repair", "patch", "mutation", "file", "model", "interface", "contract", "enum", "type", "class"));
    }

    private static bool MatchesContextReference(string normalizedTitle, IReadOnlySet<string> tokens)
    {
        return IsPhrase(normalizedTitle, "project context")
               || IsPhrase(normalizedTitle, "current context")
               || IsPhrase(normalizedTitle, "baseline context")
               || IsPhrase(normalizedTitle, "context packet")
               || IsPhrase(normalizedTitle, "current state")
               || IsPhrase(normalizedTitle, "known issues")
               || ContainsAny(tokens, "context", "background", "dependency", "input", "assumption", "environment", "reference")
               || (ContainsAny(tokens, "prior", "reuse", "baseline", "existing", "maintenance")
                   && ContainsAny(tokens, "context", "data", "packet", "project", "state"));
    }

    private static bool MatchesValidationProof(string normalizedTitle, IReadOnlySet<string> tokens)
    {
        return IsPhrase(normalizedTitle, "evidence of success")
               || IsPhrase(normalizedTitle, "success evidence")
               || IsPhrase(normalizedTitle, "proof of success")
               || IsPhrase(normalizedTitle, "proof of completion")
               || IsPhrase(normalizedTitle, "definition of done")
               || IsPhrase(normalizedTitle, "completion conditions")
               || IsPhrase(normalizedTitle, "output requirements")
               || IsPhrase(normalizedTitle, "expected outputs")
               || IsPhrase(normalizedTitle, "required outputs")
               || IsPhrase(normalizedTitle, "success criteria")
               || ContainsAny(tokens, "evidence", "proof", "verification", "validation", "deliverable", "artifact", "output")
                   && !ContainsAny(tokens, "repair", "patch", "mutation", "file", "modify", "inspect");
    }

    private static bool MatchesConditionalFollowup(string normalizedTitle, IReadOnlySet<string> tokens)
    {
        return IsPhrase(normalizedTitle, "required flow")
               || IsPhrase(normalizedTitle, "execution flow")
               || IsPhrase(normalizedTitle, "expected flow")
               || IsPhrase(normalizedTitle, "required checkpoints")
               || IsPhrase(normalizedTitle, "required checkpoint")
               || IsPhrase(normalizedTitle, "next steps")
               || IsPhrase(normalizedTitle, "follow up")
               || IsPhrase(normalizedTitle, "followup")
               || IsPhrase(normalizedTitle, "rerun goal")
               || normalizedTitle.StartsWith("if ", StringComparison.OrdinalIgnoreCase)
               || ContainsAny(tokens, "rerun", "fallback", "recovery", "checkpoint")
               || (((tokens.Count <= 2 && ContainsAny(tokens, "flow"))
                    || ContainsAny(tokens, "behavior", "step"))
                   && !ContainsAny(tokens, "build", "repair", "patch", "scaffold", "write", "create", "wire"));
    }

    private static bool MatchesScaffoldSupport(string normalizedTitle, IReadOnlySet<string> tokens)
    {
        if (IsPhrase(normalizedTitle, "required interfaces contracts")
            || IsPhrase(normalizedTitle, "required enum rules")
            || IsPhrase(normalizedTitle, "required remediation types")
            || IsPhrase(normalizedTitle, "required models")
            || IsPhrase(normalizedTitle, "required files")
            || IsPhrase(normalizedTitle, "required folders")
            || IsPhrase(normalizedTitle, "required services")
            || IsPhrase(normalizedTitle, "required storage")
            || IsPhrase(normalizedTitle, "required ui")
            || IsPhrase(normalizedTitle, "required classes")
            || IsPhrase(normalizedTitle, "required types")
            || IsPhrase(normalizedTitle, "remediation types")
            || IsPhrase(normalizedTitle, "remediation models")
            || IsPhrase(normalizedTitle, "remediation contracts"))
        {
            return true;
        }

        return (ContainsAny(tokens, "required", "expected")
                && ContainsAny(tokens, "model", "interface", "contract", "enum", "file", "folder", "service", "storage", "ui", "class", "type", "remediation"))
               || (ContainsAny(tokens, "model", "interface", "contract", "enum", "class", "type")
                   && ContainsAny(tokens, "required", "support")
                   && !ContainsAny(tokens, "repair", "patch", "mutation"));
    }

    private static bool MatchesRepairSupport(string normalizedTitle, IReadOnlySet<string> tokens)
    {
        return IsPhrase(normalizedTitle, "allowed repair classes")
               || IsPhrase(normalizedTitle, "allowed repair classes for this phase")
               || IsPhrase(normalizedTitle, "repair target")
               || IsPhrase(normalizedTitle, "patch scope")
               || IsPhrase(normalizedTitle, "mutation scope")
               || IsPhrase(normalizedTitle, "affected files")
               || IsPhrase(normalizedTitle, "files to inspect")
               || IsPhrase(normalizedTitle, "files to modify")
               || IsPhrase(normalizedTitle, "repair constraints")
               || (ContainsAny(tokens, "repair", "patch", "mutation")
                   && ContainsAny(tokens, "scope", "target", "constraint", "file", "class", "affected", "modify", "inspect"));
    }

    private static string NormalizeHeadingTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";

        var builder = new StringBuilder(title.Length);
        var previousWasSpace = false;
        foreach (var character in title.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static HashSet<string> BuildTokenSet(string normalizedTitle)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result.Add(token);
            var singular = Singularize(token);
            if (!string.Equals(singular, token, StringComparison.OrdinalIgnoreCase))
                result.Add(singular);
        }

        return result;
    }

    private static string Singularize(string token)
    {
        if (token.Length <= 3)
            return token;

        if (token.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && token.Length > 4)
            return token[..^3] + "y";

        if (token.EndsWith("es", StringComparison.OrdinalIgnoreCase) && token.Length > 4)
            return token[..^2];

        if (token.EndsWith("s", StringComparison.OrdinalIgnoreCase) && token.Length > 3)
            return token[..^1];

        return token;
    }

    private static bool ContainsAny(IReadOnlySet<string> tokens, params string[] candidates)
    {
        return candidates.Any(tokens.Contains);
    }

    private static bool IsPhrase(string normalizedTitle, string phrase)
    {
        return string.Equals(normalizedTitle, phrase, StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith(phrase + " ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.EndsWith(" " + phrase, StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.Contains(" " + phrase + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWithActionableVerb(string normalizedTitle)
    {
        return normalizedTitle.StartsWith("build ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("create ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("add ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("write ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("run ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("repair ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("implement ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("wire ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("update ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("patch ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("apply ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("attach ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("generate ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("bootstrap ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("set up ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("setup ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("configure ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("collect ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("inspect ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("verify ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("validate ", StringComparison.OrdinalIgnoreCase)
               || normalizedTitle.StartsWith("fix ", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatHeadingClass(TaskboardHeadingClass headingClass)
    {
        return headingClass switch
        {
            TaskboardHeadingClass.Actionable => "actionable",
            TaskboardHeadingClass.StructuralSupport => "structural_support",
            TaskboardHeadingClass.ConstraintPolicy => "constraint_policy",
            TaskboardHeadingClass.ContextReference => "context_reference",
            TaskboardHeadingClass.ValidationProof => "validation_proof",
            TaskboardHeadingClass.ConditionalFollowup => "conditional_followup",
            TaskboardHeadingClass.ScaffoldSupport => "scaffold_support",
            TaskboardHeadingClass.RepairSupport => "repair_support",
            _ => "unknown"
        };
    }

    private static string FormatHeadingTreatment(TaskboardHeadingTreatment treatment)
    {
        return treatment switch
        {
            TaskboardHeadingTreatment.Actionable => "actionable",
            TaskboardHeadingTreatment.Covered => "covered",
            TaskboardHeadingTreatment.ConstraintOnly => "constraint_only",
            TaskboardHeadingTreatment.ContextOnly => "context_only",
            TaskboardHeadingTreatment.ProofOnly => "proof_only",
            TaskboardHeadingTreatment.ConditionalOnly => "conditional_only",
            _ => "unknown"
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
}
