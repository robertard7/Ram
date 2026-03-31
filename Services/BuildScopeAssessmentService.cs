using System.IO;
using RAM.Models;

namespace RAM.Services;

public sealed class BuildScopeAssessmentService
{
    public BuildScopeAssessmentRecord Assess(
        string workspaceRoot,
        WorkspaceBuildProfileRecord? profile,
        string requestedCommandType,
        string resolvedTargetPath,
        string explicitPath,
        string explicitTarget,
        bool verificationMode = false)
    {
        var normalizedTool = NormalizeValue(requestedCommandType);
        var normalizedResolvedTarget = NormalizePath(resolvedTargetPath);
        var normalizedExplicitPath = NormalizePath(explicitPath);
        var normalizedExplicitTarget = NormalizeValue(explicitTarget);
        var buildFamily = profile?.BuildSystemType ?? InferFamilyFromTool(normalizedTool);
        var targetKind = ClassifyTargetKind(buildFamily, normalizedTool, normalizedResolvedTarget);
        var isNative = IsNativeFamily(buildFamily);
        var explicitPathProvided = !string.IsNullOrWhiteSpace(normalizedExplicitPath);
        var explicitTargetProvided = !string.IsNullOrWhiteSpace(normalizedExplicitTarget);

        if (!isNative)
        {
            return BuildAssessment(
                workspaceRoot,
                buildFamily,
                normalizedTool,
                normalizedResolvedTarget,
                targetKind,
                BuildScopeRiskLevel.SafeNarrow,
                "This build family uses the existing narrow project or solution target model.",
                "",
                "",
                liveExecutionAllowed: true,
                explicitPathProvided,
                explicitTargetProvided);
        }

        if (string.Equals(normalizedTool, "cmake_configure", StringComparison.OrdinalIgnoreCase))
        {
            var configureReason = verificationMode
                ? "Verification should not switch into a configure-only step, so this path remains informational only."
                : "CMake configure is the safer first step for a native workspace when the build scope is still unclear.";

            return BuildAssessment(
                workspaceRoot,
                buildFamily,
                normalizedTool,
                NormalizePath(FirstNonEmpty(normalizedResolvedTarget, profile?.ConfigureTargetPath, profile?.BuildDirectoryPath)),
                "configure_target",
                verificationMode ? BuildScopeRiskLevel.MediumNarrowable : BuildScopeRiskLevel.SafeNarrow,
                configureReason,
                verificationMode ? "Use a narrower configured build directory before rerunning verification." : "Run configure first, then rerun a narrower build directory or explicit target.",
                verificationMode ? "" : "cmake_configure",
                liveExecutionAllowed: !verificationMode,
                explicitPathProvided,
                explicitTargetProvided);
        }

        return buildFamily switch
        {
            BuildSystemType.CMake => AssessCMakeBuild(workspaceRoot, normalizedTool, normalizedResolvedTarget, normalizedExplicitPath, normalizedExplicitTarget, profile, verificationMode),
            BuildSystemType.Make => AssessDirectoryBuild(workspaceRoot, buildFamily, normalizedTool, normalizedResolvedTarget, normalizedExplicitPath, normalizedExplicitTarget, profile, verificationMode),
            BuildSystemType.Ninja => AssessDirectoryBuild(workspaceRoot, buildFamily, normalizedTool, normalizedResolvedTarget, normalizedExplicitPath, normalizedExplicitTarget, profile, verificationMode),
            BuildSystemType.Script => AssessScriptBuild(workspaceRoot, normalizedTool, normalizedResolvedTarget, normalizedExplicitPath, verificationMode),
            _ => BuildAssessment(
                workspaceRoot,
                buildFamily,
                normalizedTool,
                normalizedResolvedTarget,
                targetKind,
                BuildScopeRiskLevel.BlockedUnknown,
                "RAM could not classify this native build scope safely.",
                "Inspect build profiles and choose a narrower build directory or tool-specific target first.",
                "list_build_profiles",
                liveExecutionAllowed: false,
                explicitPathProvided,
                explicitTargetProvided)
        };
    }

    public string BuildBlockedMessage(BuildScopeAssessmentRecord assessment)
    {
        var lines = new List<string>
        {
            $"{assessment.RequestedCommandType} blocked: build scope is too broad for safe live execution.",
            $"Build family: {assessment.BuildFamily.ToString().ToLowerInvariant()}",
            $"Target scope: {DisplayValue(assessment.TargetKind)}",
            $"Resolved target: {DisplayValue(assessment.ResolvedTargetPath)}",
            $"Risk: {FormatRiskLevel(assessment.RiskLevel)}",
            $"Reason: {DisplayValue(assessment.Reason)}"
        };

        if (!string.IsNullOrWhiteSpace(assessment.RecommendedSaferAlternative))
            lines.Add($"Safer next step: {assessment.RecommendedSaferAlternative}");

        return string.Join(Environment.NewLine, lines);
    }

    public string BuildGuidance(string workspaceRoot, BuildSystemDetectionResult detection)
    {
        var preferredProfile = detection.PreferredProfile;
        if (preferredProfile is null)
        {
            return "RAM did not detect a single preferred build profile for this workspace."
                + Environment.NewLine
                + "Next: run `show build profiles` or specify a narrower build family explicitly.";
        }

        var assessment = Assess(
            workspaceRoot,
            preferredProfile,
            preferredProfile.BuildToolFamily,
            FirstNonEmpty(preferredProfile.BuildDirectoryPath, preferredProfile.BuildTargetPath, preferredProfile.PrimaryTargetPath),
            "",
            "",
            verificationMode: false);

        var lines = new List<string>
        {
            $"Preferred build family: {preferredProfile.BuildSystemType.ToString().ToLowerInvariant()}",
            $"Preferred target: {DisplayValue(preferredProfile.PrimaryTargetPath)}",
            $"Default live scope: {DisplayValue(assessment.TargetKind)}",
            $"Risk: {FormatRiskLevel(assessment.RiskLevel)}",
            $"Assessment: {DisplayValue(assessment.Reason)}"
        };

        if (!string.IsNullOrWhiteSpace(assessment.RecommendedSaferAlternative))
            lines.Add($"Safer next step: {assessment.RecommendedSaferAlternative}");

        return string.Join(Environment.NewLine, lines);
    }

    private BuildScopeAssessmentRecord AssessCMakeBuild(
        string workspaceRoot,
        string requestedCommandType,
        string resolvedTargetPath,
        string explicitPath,
        string explicitTarget,
        WorkspaceBuildProfileRecord? profile,
        bool verificationMode)
    {
        var targetPath = NormalizePath(FirstNonEmpty(resolvedTargetPath, profile?.BuildDirectoryPath, profile?.BuildTargetPath, "build"));
        var targetKind = ClassifyTargetKind(BuildSystemType.CMake, requestedCommandType, targetPath);
        var explicitDirectoryProvided = !string.IsNullOrWhiteSpace(explicitPath);
        var explicitTargetProvided = !string.IsNullOrWhiteSpace(explicitTarget);

        if (explicitTargetProvided)
        {
            return BuildAssessment(
                workspaceRoot,
                BuildSystemType.CMake,
                requestedCommandType,
                targetPath,
                targetKind,
                BuildScopeRiskLevel.SafeNarrow,
                "An explicit CMake target narrows the live build scope enough to run safely under RAM's native-build limits.",
                "",
                "",
                liveExecutionAllowed: true,
                explicitDirectoryProvided,
                explicitTargetProvided);
        }

        if (LooksLikeNarrowBuildDirectory(targetPath, explicitDirectoryProvided))
        {
            return BuildAssessment(
                workspaceRoot,
                BuildSystemType.CMake,
                requestedCommandType,
                targetPath,
                targetKind,
                BuildScopeRiskLevel.SafeNarrow,
                "The resolved CMake build directory is narrow enough to avoid a broad workspace-root build.",
                "",
                "",
                liveExecutionAllowed: true,
                explicitDirectoryProvided,
                explicitTargetProvided);
        }

        var alternative = verificationMode
            ? "Select a narrower configured build directory such as build/debug before rerunning verification."
            : "Run `configure cmake` first, then rerun `cmake_build` with a narrower build directory such as build/debug or an explicit target.";

        return BuildAssessment(
            workspaceRoot,
            BuildSystemType.CMake,
            requestedCommandType,
            targetPath,
            targetKind,
            BuildScopeRiskLevel.HighBroad,
            "The resolved CMake build directory is the broad default build scope for this workspace.",
            alternative,
            verificationMode ? "" : "cmake_configure",
            liveExecutionAllowed: false,
            explicitDirectoryProvided,
            explicitTargetProvided);
    }

    private BuildScopeAssessmentRecord AssessDirectoryBuild(
        string workspaceRoot,
        BuildSystemType buildFamily,
        string requestedCommandType,
        string resolvedTargetPath,
        string explicitPath,
        string explicitTarget,
        WorkspaceBuildProfileRecord? profile,
        bool verificationMode)
    {
        var targetPath = NormalizePath(FirstNonEmpty(resolvedTargetPath, profile?.BuildTargetPath, profile?.PrimaryTargetPath, "."));
        var targetKind = ClassifyTargetKind(buildFamily, requestedCommandType, targetPath);
        var explicitDirectoryProvided = !string.IsNullOrWhiteSpace(explicitPath);
        var explicitTargetProvided = !string.IsNullOrWhiteSpace(explicitTarget);

        if (explicitTargetProvided)
        {
            return BuildAssessment(
                workspaceRoot,
                buildFamily,
                requestedCommandType,
                targetPath,
                targetKind,
                BuildScopeRiskLevel.SafeNarrow,
                "An explicit build target narrows the native build scope enough to run safely.",
                "",
                "",
                liveExecutionAllowed: true,
                explicitDirectoryProvided,
                explicitTargetProvided);
        }

        if (LooksLikeNarrowDirectory(targetPath, explicitDirectoryProvided))
        {
            return BuildAssessment(
                workspaceRoot,
                buildFamily,
                requestedCommandType,
                targetPath,
                targetKind,
                BuildScopeRiskLevel.MediumNarrowable,
                "The resolved build directory is narrower than the workspace root and is acceptable for live execution.",
                "",
                "",
                liveExecutionAllowed: true,
                explicitDirectoryProvided,
                explicitTargetProvided);
        }

        var familyName = buildFamily.ToString().ToLowerInvariant();
        var alternative = verificationMode
            ? $"Select a smaller {familyName} directory or explicit target before rerunning verification."
            : $"Run {familyName} in a narrower subdirectory or with an explicit target, then retry the build.";

        return BuildAssessment(
            workspaceRoot,
            buildFamily,
            requestedCommandType,
            targetPath,
            targetKind,
            BuildScopeRiskLevel.HighBroad,
            $"The resolved {familyName} directory is still broad enough to cover the workspace root or a default top-level build directory.",
            alternative,
            "list_build_profiles",
            liveExecutionAllowed: false,
            explicitDirectoryProvided,
            explicitTargetProvided);
    }

    private BuildScopeAssessmentRecord AssessScriptBuild(
        string workspaceRoot,
        string requestedCommandType,
        string resolvedTargetPath,
        string explicitPath,
        bool verificationMode)
    {
        var targetPath = NormalizePath(resolvedTargetPath);
        var explicitPathProvided = !string.IsNullOrWhiteSpace(explicitPath);
        var fileName = Path.GetFileName(targetPath);
        var isConfigureScript = fileName.StartsWith("configure", StringComparison.OrdinalIgnoreCase);

        if (isConfigureScript && explicitPathProvided && LooksLikeNarrowDirectory(Path.GetDirectoryName(targetPath) ?? ".", explicitPathProvided))
        {
            return BuildAssessment(
                workspaceRoot,
                BuildSystemType.Script,
                requestedCommandType,
                targetPath,
                "script",
                BuildScopeRiskLevel.MediumNarrowable,
                "This explicit repo-local configure script is narrow enough to run under the strict script safety profile.",
                "",
                "",
                liveExecutionAllowed: !verificationMode,
                explicitPathProvided,
                explicitTargetProvided: false);
        }

        var alternative = verificationMode
            ? "Script-based verification is blocked unless RAM has a narrower non-script target."
            : "Inspect build profiles, prefer configure-first flows, or choose a narrower dedicated native build directory instead of a repo-wide build script.";

        return BuildAssessment(
            workspaceRoot,
            BuildSystemType.Script,
            requestedCommandType,
            targetPath,
            "script",
            explicitPathProvided ? BuildScopeRiskLevel.HighBroad : BuildScopeRiskLevel.BlockedUnknown,
            "Repo-local build scripts are treated as the highest-risk native family and are blocked unless RAM can justify a narrow configure-style script scope.",
            alternative,
            "list_build_profiles",
            liveExecutionAllowed: false,
            explicitPathProvided,
            explicitTargetProvided: false);
    }

    private static BuildScopeAssessmentRecord BuildAssessment(
        string workspaceRoot,
        BuildSystemType buildFamily,
        string requestedCommandType,
        string resolvedTargetPath,
        string targetKind,
        BuildScopeRiskLevel riskLevel,
        string reason,
        string recommendedSaferAlternative,
        string recommendedToolName,
        bool liveExecutionAllowed,
        bool explicitPathProvided,
        bool explicitTargetProvided)
    {
        return new BuildScopeAssessmentRecord
        {
            WorkspaceRoot = workspaceRoot,
            BuildFamily = buildFamily,
            RequestedCommandType = requestedCommandType,
            ResolvedTargetPath = resolvedTargetPath,
            TargetKind = targetKind,
            RiskLevel = riskLevel,
            Reason = reason,
            RecommendedSaferAlternative = recommendedSaferAlternative,
            RecommendedToolName = recommendedToolName,
            LiveExecutionAllowed = liveExecutionAllowed,
            ExplicitPathProvided = explicitPathProvided,
            ExplicitTargetProvided = explicitTargetProvided
        };
    }

    private static BuildSystemType InferFamilyFromTool(string toolName)
    {
        return toolName switch
        {
            "dotnet_build" or "dotnet_test" => BuildSystemType.Dotnet,
            "cmake_configure" or "cmake_build" => BuildSystemType.CMake,
            "make_build" => BuildSystemType.Make,
            "ninja_build" => BuildSystemType.Ninja,
            "run_build_script" => BuildSystemType.Script,
            _ => BuildSystemType.Unknown
        };
    }

    private static bool IsNativeFamily(BuildSystemType buildFamily)
    {
        return buildFamily is BuildSystemType.CMake
            or BuildSystemType.Make
            or BuildSystemType.Ninja
            or BuildSystemType.Script;
    }

    private static string ClassifyTargetKind(BuildSystemType buildFamily, string requestedCommandType, string resolvedTargetPath)
    {
        if (string.IsNullOrWhiteSpace(resolvedTargetPath) || resolvedTargetPath is "." or "./")
            return "workspace_root";

        if (buildFamily == BuildSystemType.Dotnet)
        {
            if (resolvedTargetPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                return "solution";
            if (resolvedTargetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                return "project";
        }

        if (buildFamily == BuildSystemType.Script || string.Equals(requestedCommandType, "run_build_script", StringComparison.OrdinalIgnoreCase))
            return "script";

        if (string.Equals(requestedCommandType, "cmake_build", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestedCommandType, "cmake_configure", StringComparison.OrdinalIgnoreCase))
        {
            return "build_dir";
        }

        return "directory";
    }

    private static bool LooksLikeNarrowBuildDirectory(string path, bool explicitPathProvided)
    {
        if (string.IsNullOrWhiteSpace(path) || path is "." or "./")
            return false;

        var normalized = NormalizePath(path).Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (explicitPathProvided && normalized.Contains('/'))
            return true;

        if (normalized.StartsWith("build/", StringComparison.OrdinalIgnoreCase))
            return true;

        return normalized.Count(ch => ch == '/') >= 1;
    }

    private static bool LooksLikeNarrowDirectory(string path, bool explicitPathProvided)
    {
        if (string.IsNullOrWhiteSpace(path) || path is "." or "./")
            return false;

        var normalized = NormalizePath(path).Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (explicitPathProvided)
            return true;

        return normalized.Count(ch => ch == '/') >= 1;
    }

    private static string NormalizePath(string path)
    {
        var normalized = (path ?? "").Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        return normalized.TrimEnd('/');
    }

    private static string NormalizeValue(string value)
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

    private static string FormatRiskLevel(BuildScopeRiskLevel riskLevel)
    {
        return riskLevel switch
        {
            BuildScopeRiskLevel.SafeNarrow => "safe_narrow",
            BuildScopeRiskLevel.MediumNarrowable => "medium_narrowable",
            BuildScopeRiskLevel.HighBroad => "high_broad",
            _ => "blocked_unknown"
        };
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
