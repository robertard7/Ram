using System.IO;
using RAM.Models;
using System.Text.Json;

namespace RAM.Services;

public sealed class PatchVerificationPlanner
{
    private readonly ArtifactClassificationService _artifactClassificationService;
    private readonly BuildScopeAssessmentService _buildScopeAssessmentService;
    private readonly BuildSystemDetectionService _buildSystemDetectionService;
    private readonly ExecutionSafetyPolicyService _executionSafetyPolicyService;

    public PatchVerificationPlanner(
        BuildSystemDetectionService buildSystemDetectionService,
        ArtifactClassificationService artifactClassificationService,
        ExecutionSafetyPolicyService executionSafetyPolicyService,
        BuildScopeAssessmentService buildScopeAssessmentService)
    {
        _buildSystemDetectionService = buildSystemDetectionService;
        _artifactClassificationService = artifactClassificationService;
        _executionSafetyPolicyService = executionSafetyPolicyService;
        _buildScopeAssessmentService = buildScopeAssessmentService;
    }

    public VerificationPlanRecord Build(
        string workspaceRoot,
        PatchApplyResultRecord applyRecord,
        RepairProposalRecord? proposal,
        WorkspaceExecutionStateRecord state,
        string activeTargetRelativePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (applyRecord is null)
            throw new ArgumentNullException(nameof(applyRecord));

        var failureKind = FirstNonEmpty(
            applyRecord.Draft.FailureKind,
            proposal?.FailureKind,
            state.LastFailureOutcomeType,
            "unknown");

        var profile = ResolvePreferredProfile(
            workspaceRoot,
            applyRecord.Draft,
            proposal,
            state,
            activeTargetRelativePath);
        var verificationTool = ResolveVerificationTool(failureKind, profile);
        var targetPath = ResolvePreferredTargetPath(failureKind, profile, applyRecord.Draft, proposal, state, activeTargetRelativePath);

        var plan = new VerificationPlanRecord
        {
            VerificationPlanId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            ModificationIntent = FirstNonEmpty(applyRecord.Draft.ModificationIntent, ""),
            SourcePatchDraftId = applyRecord.Draft.DraftId,
            SourceRepairProposalId = FirstNonEmpty(
                applyRecord.Draft.SourceProposalId,
                proposal?.ProposalId),
            FailureKind = failureKind,
            BuildSystemType = NormalizeBuildSystemType(profile?.BuildSystemType ?? BuildSystemType.Unknown),
            VerificationTool = verificationTool,
            TargetPath = targetPath,
            TargetSurfaceType = FirstNonEmpty(applyRecord.Draft.TargetSurfaceType, ""),
            TargetFiles = BuildTargetFiles(applyRecord.Draft),
            SafetyPolicySummary = verificationTool == "read_only_check"
                ? ""
                : _executionSafetyPolicyService.Describe(_executionSafetyPolicyService.GetPolicy(verificationTool))
        };

        if (verificationTool == "read_only_check")
        {
            plan.Confidence = "low";
            plan.Rationale = profile is null
                ? "RAM can apply this patch locally, but it does not have a detected build profile or safe deterministic verification target."
                : $"RAM can apply this patch locally, but it does not have a safe {DisplayValue(profile.BuildSystemType.ToString())} verification step for this failure.";
            return plan;
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            plan.VerificationTool = "read_only_check";
            plan.Confidence = "low";
            plan.Rationale = $"RAM could not justify a safe {verificationTool.Replace('_', ' ')} target from the patch draft, repair proposal, build profile, or active target.";
            return plan;
        }

        if (profile is not null && IsNativeVerificationTool(verificationTool))
        {
            var assessment = _buildScopeAssessmentService.Assess(
                workspaceRoot,
                profile,
                verificationTool,
                targetPath,
                "",
                "",
                verificationMode: true);
            if (!assessment.LiveExecutionAllowed)
            {
                plan.VerificationTool = "read_only_check";
                plan.Confidence = "low";
                plan.SafetyPolicySummary = "";
                plan.Rationale = string.IsNullOrWhiteSpace(assessment.RecommendedSaferAlternative)
                    ? assessment.Reason
                    : assessment.Reason + Environment.NewLine + $"Safer next step: {assessment.RecommendedSaferAlternative}";
                return plan;
            }
        }

        plan.Confidence = DetermineConfidence(applyRecord.Draft, proposal, state, targetPath);
        plan.Rationale = verificationTool switch
        {
            "dotnet_build" => $"Verify the applied patch by rebuilding the narrowest known target: {targetPath}.",
            "dotnet_test" => $"Verify the applied patch by rerunning tests on the narrowest known target: {targetPath}.",
            "cmake_build" => $"Verify the applied patch by rebuilding the selected CMake build directory: {targetPath} with RAM's safety-bounded cmake_build policy.",
            "make_build" => $"Verify the applied patch by rerunning make in the selected directory: {targetPath} with RAM's safety-bounded make_build policy.",
            "ninja_build" => $"Verify the applied patch by rerunning ninja in the selected build directory: {targetPath} with RAM's safety-bounded ninja_build policy.",
            "run_build_script" => $"Verify the applied patch by rerunning the selected repo-local build script: {targetPath} with RAM's strict script safety policy.",
            _ => "RAM could not determine a safe executable verification step."
        };
        return plan;
    }

    private WorkspaceBuildProfileRecord? ResolvePreferredProfile(
        string workspaceRoot,
        PatchDraftRecord draft,
        RepairProposalRecord? proposal,
        WorkspaceExecutionStateRecord state,
        string activeTargetRelativePath)
    {
        if (_artifactClassificationService.IsBuildOrTestTargetPath(draft.TargetProjectPath))
            return BuildDotnetProfile(workspaceRoot, draft.TargetProjectPath);

        if (_artifactClassificationService.IsBuildOrTestTargetPath(proposal?.TargetProjectPath ?? ""))
            return BuildDotnetProfile(workspaceRoot, proposal!.TargetProjectPath);

        if (_artifactClassificationService.IsBuildOrTestTargetPath(state.LastFailureTargetPath))
            return BuildDotnetProfile(workspaceRoot, state.LastFailureTargetPath);

        if (_artifactClassificationService.IsBuildOrTestTargetPath(activeTargetRelativePath))
            return BuildDotnetProfile(workspaceRoot, activeTargetRelativePath);

        var persistedProfile = DeserializeProfile(state.LastSelectedBuildProfileJson);
        if (persistedProfile is not null)
            return persistedProfile;

        return _buildSystemDetectionService.GetPreferredProfile(workspaceRoot);
    }

    private static string ResolveVerificationTool(string failureKind, WorkspaceBuildProfileRecord? profile)
    {
        if (profile is null)
            return "read_only_check";

        if (string.Equals(failureKind, "test_failure", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(profile.TestToolFamily) ? "read_only_check" : profile.TestToolFamily;

        if (string.Equals(failureKind, "build_failure", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(profile.BuildToolFamily) ? "read_only_check" : profile.BuildToolFamily;

        return "read_only_check";
    }

    private static string ResolvePreferredTargetPath(
        string failureKind,
        WorkspaceBuildProfileRecord? profile,
        PatchDraftRecord draft,
        RepairProposalRecord? proposal,
        WorkspaceExecutionStateRecord state,
        string activeTargetRelativePath)
    {
        if (profile is null)
            return "";

        if (profile.BuildSystemType == BuildSystemType.Dotnet)
        {
            var proposedTarget = NormalizeRelativePath(FirstNonEmpty(
                draft.TargetProjectPath,
                proposal?.TargetProjectPath,
                state.LastFailureTargetPath));

            if (IsProjectTarget(proposedTarget))
                return proposedTarget;

            var activeTarget = NormalizeRelativePath(activeTargetRelativePath);
            if (IsProjectTarget(activeTarget) && MatchesRepairSubtree(activeTarget, draft.TargetFilePath, proposedTarget))
                return activeTarget;

            if (IsProjectTarget(activeTarget) && string.IsNullOrWhiteSpace(proposedTarget))
                return activeTarget;

            return string.Equals(failureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
                ? NormalizeRelativePath(profile.TestTargetPath)
                : NormalizeRelativePath(profile.BuildTargetPath);
        }

        return string.Equals(failureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
            ? NormalizeRelativePath(profile.TestTargetPath)
            : FirstNonEmpty(
                NormalizeRelativePath(profile.BuildDirectoryPath),
                NormalizeRelativePath(profile.BuildTargetPath),
                NormalizeRelativePath(profile.PrimaryTargetPath));
    }

    private static string DetermineConfidence(
        PatchDraftRecord draft,
        RepairProposalRecord? proposal,
        WorkspaceExecutionStateRecord state,
        string targetPath)
    {
        if (string.Equals(NormalizeRelativePath(draft.TargetProjectPath), targetPath, StringComparison.OrdinalIgnoreCase))
            return "high";

        if (string.Equals(NormalizeRelativePath(proposal?.TargetProjectPath ?? ""), targetPath, StringComparison.OrdinalIgnoreCase))
            return "high";

        if (string.Equals(NormalizeRelativePath(state.LastFailureTargetPath), targetPath, StringComparison.OrdinalIgnoreCase))
            return "medium";

        return "medium";
    }

    private static List<string> BuildTargetFiles(PatchDraftRecord draft)
    {
        var values = new List<string>();
        AddIfMeaningful(values, draft.TargetFilePath);
        foreach (var supportingFile in draft.SupportingFiles)
            AddIfMeaningful(values, supportingFile);
        return values;
    }

    private static bool MatchesRepairSubtree(string activeTargetRelativePath, string targetFilePath, string proposedTargetPath)
    {
        if (!string.IsNullOrWhiteSpace(proposedTargetPath))
        {
            var proposedDirectory = NormalizeDirectory(Path.GetDirectoryName(proposedTargetPath));
            if (!string.IsNullOrWhiteSpace(proposedDirectory)
                && activeTargetRelativePath.StartsWith(proposedDirectory + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(targetFilePath))
            return false;

        var fileDirectory = NormalizeDirectory(Path.GetDirectoryName(NormalizeRelativePath(targetFilePath)));
        if (string.IsNullOrWhiteSpace(fileDirectory))
            return false;

        var activeDirectory = NormalizeDirectory(Path.GetDirectoryName(activeTargetRelativePath));
        return string.Equals(activeDirectory, fileDirectory, StringComparison.OrdinalIgnoreCase)
            || fileDirectory.StartsWith(activeDirectory + "/", StringComparison.OrdinalIgnoreCase)
            || activeDirectory.StartsWith(fileDirectory + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProjectTarget(string path)
    {
        var normalized = NormalizeRelativePath(path);
        return normalized.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNativeVerificationTool(string toolName)
    {
        return string.Equals(toolName, "cmake_build", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "make_build", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "ninja_build", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "run_build_script", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIfMeaningful(List<string> values, string candidate)
    {
        var normalized = NormalizeRelativePath(candidate);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (values.Any(current => string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase)))
            return;

        values.Add(normalized);
    }

    private static string NormalizeDirectory(string? path)
    {
        return NormalizeRelativePath(path ?? "").TrimEnd('/');
    }

    private static string NormalizeRelativePath(string path)
    {
        return (path ?? "").Replace('\\', '/');
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

    private static WorkspaceBuildProfileRecord BuildDotnetProfile(string workspaceRoot, string targetPath)
    {
        return new WorkspaceBuildProfileRecord
        {
            WorkspaceRoot = workspaceRoot,
            BuildSystemType = BuildSystemType.Dotnet,
            PrimaryTargetPath = NormalizeRelativePath(targetPath),
            BuildToolFamily = "dotnet_build",
            TestToolFamily = "dotnet_test",
            BuildTargetPath = NormalizeRelativePath(targetPath),
            TestTargetPath = NormalizeRelativePath(targetPath),
            Confidence = "high"
        };
    }

    private static WorkspaceBuildProfileRecord? DeserializeProfile(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<WorkspaceBuildProfileRecord>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeBuildSystemType(BuildSystemType buildSystemType)
    {
        return buildSystemType.ToString().ToLowerInvariant();
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "workspace" : value;
    }
}
