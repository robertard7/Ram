using System.IO;
using RAM.Models;

namespace RAM.Services;

public sealed class AutoValidationPlanner
{
    private readonly AutoValidationPolicyService _autoValidationPolicyService = new();
    private readonly BuildScopeAssessmentService _buildScopeAssessmentService = new();
    private readonly BuildSystemDetectionService _buildSystemDetectionService = new();
    private readonly ExecutionSafetyPolicyService _executionSafetyPolicyService = new();
    private readonly PatchVerificationPlanner _patchVerificationPlanner;
    private readonly WorkspaceBuildIndexService _workspaceBuildIndexService = new();

    public AutoValidationPlanner()
    {
        _patchVerificationPlanner = new PatchVerificationPlanner(
            _buildSystemDetectionService,
            new ArtifactClassificationService(),
            _executionSafetyPolicyService,
            _buildScopeAssessmentService);
    }

    public AutoValidationPlanRecord BuildForPatchApply(
        string workspaceRoot,
        long sourceArtifactId,
        string sourceArtifactType,
        PatchApplyResultRecord applyRecord,
        RepairProposalRecord? proposal,
        WorkspaceExecutionStateRecord state,
        string activeTargetRelativePath)
    {
        var verificationPlan = _patchVerificationPlanner.Build(workspaceRoot, applyRecord, proposal, state, activeTargetRelativePath);
        var buildFamily = FirstNonEmpty(verificationPlan.BuildSystemType, state.LastSelectedBuildProfileType);
        var buildSystemType = ParseBuildSystemType(buildFamily);
        var changedFilePath = NormalizePath(applyRecord.Draft.TargetFilePath);
        var plan = new AutoValidationPlanRecord
        {
            PlanId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            SourceArtifactId = sourceArtifactId,
            SourceArtifactType = sourceArtifactType,
            SourceActionType = "patch_apply",
            ChangedFilePaths = string.IsNullOrWhiteSpace(changedFilePath)
                ? []
                : [changedFilePath],
            BuildFamily = buildFamily,
            SelectedValidationTool = string.Equals(verificationPlan.VerificationTool, "read_only_check", StringComparison.OrdinalIgnoreCase)
                ? ""
                : verificationPlan.VerificationTool,
            SelectedTargetPath = NormalizePath(verificationPlan.TargetPath),
            ValidationReason = verificationPlan.Rationale,
            SafetySummary = verificationPlan.SafetyPolicySummary,
            PolicyMode = FormatPolicyMode(AutoValidationPolicyMode.NotApplicable)
        };

        if (buildSystemType == BuildSystemType.Dotnet)
        {
            var executionAllowed = !string.IsNullOrWhiteSpace(plan.SelectedValidationTool);
            plan.PolicyMode = FormatPolicyMode(executionAllowed ? AutoValidationPolicyMode.AutoAllowed : AutoValidationPolicyMode.NotApplicable);
            plan.ScopeRiskClassification = DetermineScopeRiskClassification(
                workspaceRoot,
                verificationPlan.BuildSystemType,
                verificationPlan.VerificationTool,
                verificationPlan.TargetPath,
                executionAllowed);
            plan.ExecutionAllowed = executionAllowed;
            plan.BlockedReason = executionAllowed ? "" : verificationPlan.Rationale;
            return plan;
        }

        if (!IsNativeBuildSystemType(buildSystemType))
        {
            plan.ScopeRiskClassification = "blocked_unknown";
            plan.ExecutionAllowed = false;
            plan.BlockedReason = verificationPlan.Rationale;
            return plan;
        }

        var assessment = BuildNativeAssessment(
            workspaceRoot,
            buildSystemType,
            plan.SelectedValidationTool,
            plan.SelectedTargetPath);
        var policy = _autoValidationPolicyService.Assess(
            buildSystemType,
            changedFilePath,
            plan.SelectedValidationTool,
            plan.SelectedTargetPath,
            assessment,
            isPatchApply: true);

        ApplyPolicy(plan, policy, assessment);
        if (string.IsNullOrWhiteSpace(plan.ValidationReason))
            plan.ValidationReason = verificationPlan.Rationale;
        if (!plan.ExecutionAllowed)
            plan.BlockedReason = FirstNonEmpty(policy.Reason, verificationPlan.Rationale);

        return plan;
    }

    public AutoValidationPlanRecord BuildForFileChange(
        string workspaceRoot,
        long sourceArtifactId,
        string sourceArtifactType,
        string sourceActionType,
        IReadOnlyList<string> changedFilePaths)
    {
        var normalizedPaths = changedFilePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var plan = CreateBasePlan(workspaceRoot, sourceArtifactId, sourceArtifactType, sourceActionType, normalizedPaths);
        if (normalizedPaths.Count == 0)
            return MarkNotApplicable(plan, "No changed workspace files were available for auto-validation.");

        var relevantPaths = normalizedPaths.Where(IsValidationRelevantPath).ToList();
        if (relevantPaths.Count == 0)
            return MarkNotApplicable(plan, "The changed files are not code or build-system files, so no auto-validation is applicable.");

        var detection = _buildSystemDetectionService.Detect(workspaceRoot);
        var preferredProfile = detection.PreferredProfile;
        if (preferredProfile is null)
        {
            plan.BuildFamily = NormalizeBuildSystemType(detection.DetectedType);
            return MarkBlocked(plan, "RAM could not detect a preferred build profile for this workspace.", BuildScopeRiskLevel.BlockedUnknown);
        }

        plan.BuildFamily = NormalizeBuildSystemType(preferredProfile.BuildSystemType);
        var primaryPath = relevantPaths[0];

        if (preferredProfile.BuildSystemType == BuildSystemType.Dotnet)
            return BuildDotnetPlan(workspaceRoot, plan, primaryPath);

        return BuildNativePlan(workspaceRoot, plan, primaryPath, preferredProfile);
    }

    public bool ShouldSkipDuplicate(
        string workspaceRoot,
        AutoValidationPlanRecord plan,
        RamDbService ramDbService,
        out string reason)
    {
        reason = "";
        if (plan is null)
            return false;

        var artifact = ramDbService.LoadLatestArtifactByType(workspaceRoot, "auto_validation_result");
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.Content))
            return false;

        try
        {
            var result = System.Text.Json.JsonSerializer.Deserialize<AutoValidationResultRecord>(artifact.Content);
            if (result is null)
                return false;

            if (!string.Equals(result.SourceActionType, plan.SourceActionType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (result.OutcomeClassification is not "validated_success"
                and not "scope_blocked"
                and not "not_applicable"
                and not "manual_only")
                return false;

            if ((DateTime.UtcNow - ParseUtc(result.CreatedUtc)).TotalSeconds > 20)
                return false;

            if (string.Equals(plan.PolicyMode, FormatPolicyMode(AutoValidationPolicyMode.ManualOnly), StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(result.OutcomeClassification, "manual_only", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(result.BuildFamily, plan.BuildFamily, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(result.SourceActionType, plan.SourceActionType, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                reason = "Skipped duplicate native auto-validation because the same native change family was already marked manual-only very recently.";
                return true;
            }

            if (!string.Equals(result.ExecutedTool, plan.SelectedValidationTool, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(NormalizePath(result.ResolvedTarget), NormalizePath(plan.SelectedTargetPath), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            reason = "Skipped duplicate auto-validation because the same target was already validated very recently.";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private AutoValidationPlanRecord BuildDotnetPlan(
        string workspaceRoot,
        AutoValidationPlanRecord plan,
        string primaryPath)
    {
        var items = _workspaceBuildIndexService.ListItems(workspaceRoot)
            .Where(item => item.ItemType is "solution" or "project")
            .ToList();
        var target = ResolveNearestDotnetTarget(primaryPath, items);
        if (target is null)
            return MarkBlocked(plan, "RAM could not map the changed file to a local .NET project or solution target.", BuildScopeRiskLevel.BlockedUnknown);

        plan.SelectedValidationTool = target.ItemType == "project" && target.LikelyTestProject
            ? "dotnet_test"
            : "dotnet_build";
        plan.SelectedTargetPath = target.RelativePath;
        plan.ValidationReason = target.ItemType == "project" && target.LikelyTestProject
            ? $"The changed file is inside the likely test target {target.RelativePath}, so the smallest safe validation is dotnet_test."
            : $"The changed file maps to {target.RelativePath}, so the smallest safe validation is {plan.SelectedValidationTool}.";
        plan.PolicyMode = FormatPolicyMode(AutoValidationPolicyMode.AutoAllowed);
        plan.ExecutionAllowed = true;
        plan.SafetySummary = _executionSafetyPolicyService.Describe(_executionSafetyPolicyService.GetPolicy("dotnet"));
        plan.ScopeRiskClassification = "safe_narrow";
        return plan;
    }

    private AutoValidationPlanRecord BuildNativePlan(
        string workspaceRoot,
        AutoValidationPlanRecord plan,
        string primaryPath,
        WorkspaceBuildProfileRecord preferredProfile)
    {
        var toolName = ResolveNativeToolName(primaryPath, preferredProfile);
        var targetPath = ResolveNativeTargetPath(primaryPath, preferredProfile, toolName);
        var assessment = _buildScopeAssessmentService.Assess(
            workspaceRoot,
            preferredProfile,
            toolName,
            targetPath,
            "",
            "",
            verificationMode: false);

        plan.SelectedValidationTool = toolName;
        plan.SelectedTargetPath = NormalizePath(targetPath);
        var policy = _autoValidationPolicyService.Assess(
            preferredProfile.BuildSystemType,
            primaryPath,
            toolName,
            targetPath,
            assessment,
            isPatchApply: false);

        ApplyPolicy(plan, policy, assessment);
        return plan;
    }

    private static AutoValidationPlanRecord CreateBasePlan(
        string workspaceRoot,
        long sourceArtifactId,
        string sourceArtifactType,
        string sourceActionType,
        List<string> changedFilePaths)
    {
        return new AutoValidationPlanRecord
        {
            PlanId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            SourceArtifactId = sourceArtifactId,
            SourceArtifactType = sourceArtifactType ?? "",
            SourceActionType = sourceActionType ?? "unknown",
            ChangedFilePaths = changedFilePaths,
            PolicyMode = FormatPolicyMode(AutoValidationPolicyMode.NotApplicable)
        };
    }

    private static AutoValidationPlanRecord MarkNotApplicable(AutoValidationPlanRecord plan, string reason)
    {
        plan.PolicyMode = FormatPolicyMode(AutoValidationPolicyMode.NotApplicable);
        plan.ValidationReason = reason;
        plan.BlockedReason = reason;
        plan.ScopeRiskClassification = "blocked_unknown";
        plan.ExecutionAllowed = false;
        return plan;
    }

    private static AutoValidationPlanRecord MarkBlocked(AutoValidationPlanRecord plan, string reason, BuildScopeRiskLevel riskLevel)
    {
        plan.PolicyMode = FormatPolicyMode(
            riskLevel is BuildScopeRiskLevel.HighBroad or BuildScopeRiskLevel.MediumNarrowable
                ? AutoValidationPolicyMode.ScopeBlocked
                : AutoValidationPolicyMode.NotApplicable);
        plan.ValidationReason = reason;
        plan.BlockedReason = reason;
        plan.ScopeRiskClassification = FormatScopeRisk(riskLevel);
        plan.ExecutionAllowed = false;
        return plan;
    }

    private static bool IsValidationRelevantPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var fileName = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(relativePath).ToLowerInvariant();
        return extension is ".cs" or ".cpp" or ".c" or ".h" or ".hpp" or ".csproj" or ".sln" or ".props" or ".targets" or ".sh" or ".bat" or ".cmd" or ".ps1"
            || string.Equals(fileName, "CMakeLists.txt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "Makefile", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "build.ninja", StringComparison.OrdinalIgnoreCase);
    }

    private static WorkspaceBuildItem? ResolveNearestDotnetTarget(string changedFilePath, IReadOnlyList<WorkspaceBuildItem> items)
    {
        if (changedFilePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || changedFilePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return items.FirstOrDefault(item => string.Equals(item.RelativePath, changedFilePath, StringComparison.OrdinalIgnoreCase));
        }

        var candidates = items
            .Where(item => item.ItemType == "project")
            .Where(item =>
            {
                var parent = item.ParentDirectory == "." ? "" : NormalizePath(item.ParentDirectory);
                return string.IsNullOrWhiteSpace(parent)
                    || changedFilePath.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(item => item.ParentDirectory?.Length ?? 0)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count > 0)
            return candidates[0];

        var solutions = items.Where(item => item.ItemType == "solution").ToList();
        if (solutions.Count == 1)
            return solutions[0];

        if (items.Count == 1)
            return items[0];

        return null;
    }

    private static string ResolveNativeToolName(string changedFilePath, WorkspaceBuildProfileRecord preferredProfile)
    {
        if (preferredProfile.BuildSystemType == BuildSystemType.CMake
            && string.Equals(Path.GetFileName(changedFilePath), "CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonEmpty(preferredProfile.ConfigureToolFamily, preferredProfile.BuildToolFamily);
        }

        return FirstNonEmpty(preferredProfile.BuildToolFamily, preferredProfile.ConfigureToolFamily);
    }

    private static string ResolveNativeTargetPath(string changedFilePath, WorkspaceBuildProfileRecord preferredProfile, string toolName)
    {
        if (string.Equals(toolName, "cmake_configure", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonEmpty(preferredProfile.ConfigureTargetPath, preferredProfile.BuildDirectoryPath, preferredProfile.BuildTargetPath, preferredProfile.PrimaryTargetPath);
        }

        return FirstNonEmpty(preferredProfile.BuildDirectoryPath, preferredProfile.BuildTargetPath, preferredProfile.PrimaryTargetPath);
    }

    private static string DetermineScopeRiskClassification(
        string workspaceRoot,
        string buildSystemType,
        string verificationTool,
        string targetPath,
        bool executionAllowed)
    {
        if (executionAllowed)
            return "safe_narrow";

        var family = ParseBuildSystemType(buildSystemType);
        if (family is BuildSystemType.CMake or BuildSystemType.Make or BuildSystemType.Ninja or BuildSystemType.Script)
        {
            var assessment = new BuildScopeAssessmentService().Assess(
                workspaceRoot,
                new WorkspaceBuildProfileRecord
                {
                    WorkspaceRoot = workspaceRoot,
                    BuildSystemType = family,
                    BuildToolFamily = verificationTool,
                    BuildTargetPath = targetPath,
                    BuildDirectoryPath = targetPath,
                    PrimaryTargetPath = targetPath
                },
                verificationTool,
                targetPath,
                "",
                "",
                verificationMode: true);
            return FormatScopeRisk(assessment.RiskLevel);
        }

        return "blocked_unknown";
    }

    private static BuildSystemType ParseBuildSystemType(string value)
    {
        return Enum.TryParse<BuildSystemType>(value ?? "", ignoreCase: true, out var parsed)
            ? parsed
            : BuildSystemType.Unknown;
    }

    private static string NormalizeBuildSystemType(BuildSystemType buildSystemType)
    {
        return buildSystemType.ToString().ToLowerInvariant();
    }

    private static string FormatPolicyMode(AutoValidationPolicyMode mode)
    {
        return mode switch
        {
            AutoValidationPolicyMode.AutoAllowed => "auto_allowed",
            AutoValidationPolicyMode.ManualOnly => "manual_only",
            AutoValidationPolicyMode.ScopeBlocked => "scope_blocked",
            AutoValidationPolicyMode.SafetyBlocked => "safety_blocked",
            _ => "not_applicable"
        };
    }

    private static string FormatScopeRisk(BuildScopeRiskLevel riskLevel)
    {
        return riskLevel switch
        {
            BuildScopeRiskLevel.SafeNarrow => "safe_narrow",
            BuildScopeRiskLevel.MediumNarrowable => "medium_narrowable",
            BuildScopeRiskLevel.HighBroad => "high_broad",
            _ => "blocked_unknown"
        };
    }

    private static string NormalizePath(string path)
    {
        return (path ?? "").Replace('\\', '/').Trim();
    }

    private void ApplyPolicy(
        AutoValidationPlanRecord plan,
        AutoValidationPolicyDecision policy,
        BuildScopeAssessmentRecord? assessment)
    {
        plan.PolicyMode = FormatPolicyMode(policy.Mode);
        plan.ValidationReason = policy.Reason;
        plan.RecommendedNextStep = policy.SuggestedNextStep;
        plan.ScopeRiskClassification = assessment is null
            ? "blocked_unknown"
            : FormatScopeRisk(assessment.RiskLevel);

        if (policy.Mode == AutoValidationPolicyMode.AutoAllowed)
        {
            plan.ExecutionAllowed = true;
            plan.BlockedReason = "";
            if (!string.IsNullOrWhiteSpace(plan.SelectedValidationTool))
                plan.SafetySummary = _executionSafetyPolicyService.Describe(_executionSafetyPolicyService.GetPolicy(plan.SelectedValidationTool));
            return;
        }

        plan.ExecutionAllowed = false;
        plan.BlockedReason = policy.Reason;
        plan.SafetySummary = "";
    }

    private BuildScopeAssessmentRecord? BuildNativeAssessment(
        string workspaceRoot,
        BuildSystemType buildSystemType,
        string selectedValidationTool,
        string selectedTargetPath)
    {
        if (string.IsNullOrWhiteSpace(selectedValidationTool))
            return null;

        var normalizedTarget = NormalizePath(selectedTargetPath);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
            return null;

        return _buildScopeAssessmentService.Assess(
            workspaceRoot,
            new WorkspaceBuildProfileRecord
            {
                WorkspaceRoot = workspaceRoot,
                BuildSystemType = buildSystemType,
                BuildToolFamily = selectedValidationTool,
                ConfigureToolFamily = string.Equals(selectedValidationTool, "cmake_configure", StringComparison.OrdinalIgnoreCase)
                    ? selectedValidationTool
                    : "",
                BuildTargetPath = normalizedTarget,
                BuildDirectoryPath = normalizedTarget,
                ConfigureTargetPath = string.Equals(selectedValidationTool, "cmake_configure", StringComparison.OrdinalIgnoreCase)
                    ? normalizedTarget
                    : "",
                PrimaryTargetPath = normalizedTarget
            },
            selectedValidationTool,
            normalizedTarget,
            "",
            "",
            verificationMode: false);
    }

    private static bool IsNativeBuildSystemType(BuildSystemType buildSystemType)
    {
        return buildSystemType is BuildSystemType.CMake
            or BuildSystemType.Make
            or BuildSystemType.Ninja
            or BuildSystemType.Script;
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

    private static DateTime ParseUtc(string value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed
            : DateTime.MinValue;
    }
}
