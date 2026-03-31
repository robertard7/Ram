using System.IO;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class BuildOperationResolutionService
{
    private readonly BuildSystemDetectionService _buildSystemDetectionService = new();
    private static readonly Regex InspectTargetPattern = new(
        @"^\s*(?:inspect|look\s+at|show\s+build\s+targets\s+for)\s+(?<target>.+?)[.?!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DotnetTestOnPattern = new(
        @"^\s*(?:run\s+)?dotnet\s+test\s+on\s+(?<target>.+?)[.?!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RunTestsOnPattern = new(
        @"^\s*(?:run\s+)?tests?\s+on\s+(?<target>.+?)[.?!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RepairPlanTargetPattern = new(
        @"\b(?:for|in|on)\s+(?<target>(""[^""]+""|'[^']+'|[^.?!]+?))(?:[.?!]?$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public ResolvedUserIntent? Resolve(string prompt, string activeTargetRelativePath)
    {
        return Resolve(prompt, activeTargetRelativePath, "");
    }

    public ResolvedUserIntent? Resolve(string prompt, string activeTargetRelativePath, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        var normalized = Normalize(prompt);

        if (IsDetectBuildSystemRequest(normalized))
            return BuildResolution("detect_build_system", "Resolved build-system detection request locally.");

        if (IsListBuildProfilesRequest(normalized))
            return BuildResolution("list_build_profiles", "Resolved build-profile listing request locally.");

        if (TryResolveFamilySpecificBuildRequest(normalized, out var familySpecificResolution))
            return familySpecificResolution;

        if (TryResolveGenericBuildRequest(normalized, workspaceRoot, out var genericBuildResolution))
            return genericBuildResolution;

        if (IsListProjectsRequest(normalized, out var kind))
            return BuildResolution("list_projects", $"Resolved list_projects request locally ({kind}).", ("kind", kind));

        if (IsInspectProjectRequest(normalized))
        {
            var target = GetPreferredProjectTarget(activeTargetRelativePath);
            return string.IsNullOrWhiteSpace(target)
                ? BuildResolution("inspect_project", "Resolved inspect_project request locally using workspace discovery.")
                : BuildResolution("inspect_project", $"Resolved inspect_project request locally for {target}.", ("path", target));
        }

        if (TryResolveInspectTarget(prompt, activeTargetRelativePath, out var inspectResolution))
            return inspectResolution;

        if (TryResolveDotnetTest(prompt, activeTargetRelativePath, workspaceRoot, out var dotnetTestResolution))
            return dotnetTestResolution;

        if (TryResolveRepairPlanRequest(prompt, activeTargetRelativePath, out var repairPlanResolution))
            return repairPlanResolution;

        if (TryResolvePatchPreviewRequest(prompt, activeTargetRelativePath, out var patchPreviewResolution))
            return patchPreviewResolution;

        if (TryResolvePatchApplyRequest(prompt, activeTargetRelativePath, out var patchApplyResolution))
            return patchApplyResolution;

        if (TryResolvePatchVerificationRequest(prompt, activeTargetRelativePath, out var patchVerificationResolution))
            return patchVerificationResolution;

        if (TryResolveFailureContextRequest(normalized, out var failureContextResolution))
            return failureContextResolution;

        if (IsGitDiffRequest(normalized))
            return BuildResolution("git_diff", "Resolved git_diff request locally.");

        return null;
    }

    private bool TryResolveGenericBuildRequest(string normalizedPrompt, string workspaceRoot, out ResolvedUserIntent? resolution)
    {
        if (!ContainsGenericBuildPhrase(normalizedPrompt))
        {
            resolution = null;
            return false;
        }

        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            resolution = BuildResolution("detect_build_system", "Resolved build request locally by detecting the workspace build system first.");
            return true;
        }

        var preferredProfile = _buildSystemDetectionService.GetPreferredProfile(workspaceRoot);
        if (preferredProfile is null || string.IsNullOrWhiteSpace(preferredProfile.BuildToolFamily))
        {
            resolution = BuildResolution("detect_build_system", "Resolved build request locally by detecting the workspace build system first.");
            return true;
        }

        var toolName = preferredProfile.BuildToolFamily;
        if (preferredProfile.BuildSystemType == BuildSystemType.CMake
            && !IsCMakeConfigured(workspaceRoot, preferredProfile.BuildDirectoryPath))
        {
            toolName = "cmake_configure";
        }

        var reason = preferredProfile.BuildSystemType is BuildSystemType.CMake or BuildSystemType.Make or BuildSystemType.Ninja or BuildSystemType.Script
            ? "Resolved build request locally using the preferred workspace build profile. Native target scope will be checked before execution."
            : "Resolved build request locally using the preferred workspace build profile.";
        resolution = BuildProfileResolution(preferredProfile, toolName, reason);
        return true;
    }

    private bool TryResolveFamilySpecificBuildRequest(string normalizedPrompt, out ResolvedUserIntent? resolution)
    {
        if (normalizedPrompt.Contains("configure cmake", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("run cmake configure", StringComparison.OrdinalIgnoreCase))
        {
            resolution = BuildResolution("cmake_configure", "Resolved CMake configure request locally.");
            return true;
        }

        if (normalizedPrompt.Contains("run cmake build", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("build with cmake", StringComparison.OrdinalIgnoreCase))
        {
            resolution = BuildResolution("cmake_build", "Resolved CMake build request locally.");
            return true;
        }

        if (normalizedPrompt.Contains("run make", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt == "make")
        {
            resolution = BuildResolution("make_build", "Resolved make build request locally.");
            return true;
        }

        if (normalizedPrompt.Contains("run ninja", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt == "ninja")
        {
            resolution = BuildResolution("ninja_build", "Resolved ninja build request locally.");
            return true;
        }

        resolution = null;
        return false;
    }

    private static bool IsListProjectsRequest(string normalizedPrompt, out string kind)
    {
        if (normalizedPrompt.Contains("show test projects", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what tests are here", StringComparison.OrdinalIgnoreCase))
        {
            kind = "test_projects";
            return true;
        }

        if (normalizedPrompt.Contains("find solution files", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("show solutions", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("list solutions", StringComparison.OrdinalIgnoreCase))
        {
            kind = "solutions";
            return true;
        }

        if (normalizedPrompt.Contains("list projects", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("show projects", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what can i build here", StringComparison.OrdinalIgnoreCase))
        {
            kind = "all";
            return true;
        }

        kind = "";
        return false;
    }

    private static bool IsInspectProjectRequest(string normalizedPrompt)
    {
        return normalizedPrompt == "inspect this project"
            || normalizedPrompt == "inspect project"
            || normalizedPrompt == "inspect the solution"
            || normalizedPrompt == "show build targets";
    }

    private static bool TryResolveInspectTarget(string prompt, string activeTargetRelativePath, out ResolvedUserIntent? resolution)
    {
        var match = InspectTargetPattern.Match(prompt ?? "");
        if (!match.Success)
        {
            resolution = null;
            return false;
        }

        var target = CleanTarget(match.Groups["target"].Value);
        if (string.IsNullOrWhiteSpace(target))
        {
            resolution = null;
            return false;
        }

        if (!LooksLikeProjectTarget(target))
        {
            resolution = null;
            return false;
        }

        var preferredTarget = IsCurrentProjectReference(target)
            ? GetPreferredProjectTarget(activeTargetRelativePath)
            : target;

        if (string.IsNullOrWhiteSpace(preferredTarget))
        {
            resolution = null;
            return false;
        }

        resolution = BuildResolution(
            "inspect_project",
            $"Resolved inspect_project request locally for {preferredTarget}.",
            ("path", preferredTarget));
        return true;
    }

    private bool TryResolveDotnetTest(string prompt, string activeTargetRelativePath, string workspaceRoot, out ResolvedUserIntent? resolution)
    {
        var normalized = Normalize(prompt);

        var explicitMatch = DotnetTestOnPattern.Match(prompt ?? "");
        if (!explicitMatch.Success)
            explicitMatch = RunTestsOnPattern.Match(prompt ?? "");

        if (explicitMatch.Success)
        {
            var target = CleanTarget(explicitMatch.Groups["target"].Value);
            if (string.IsNullOrWhiteSpace(target))
            {
                resolution = null;
                return false;
            }

            resolution = BuildResolution(
                "dotnet_test",
                $"Resolved dotnet_test request locally for {target}.",
                ("project", target));
            return true;
        }

        if (normalized.Contains("run tests", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("run the tests", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("dotnet test", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("test the solution", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(workspaceRoot))
            {
                var preferredProfile = _buildSystemDetectionService.GetPreferredProfile(workspaceRoot);
                if (preferredProfile is not null && !string.IsNullOrWhiteSpace(preferredProfile.TestToolFamily))
                {
                    resolution = BuildProfileResolution(preferredProfile, preferredProfile.TestToolFamily, "Resolved test request locally using the preferred workspace build profile.");
                    return true;
                }
            }

            var preferredTarget = GetPreferredTestTarget(activeTargetRelativePath);
            resolution = string.IsNullOrWhiteSpace(preferredTarget)
                ? BuildResolution("dotnet_test", "Resolved dotnet_test request locally using workspace discovery.")
                : BuildResolution("dotnet_test", $"Resolved dotnet_test request locally for {preferredTarget}.", ("project", preferredTarget));
            return true;
        }

        resolution = null;
        return false;
    }

    private static bool TryResolveRepairPlanRequest(string prompt, string activeTargetRelativePath, out ResolvedUserIntent? resolution)
    {
        var normalized = Normalize(prompt);
        if (!ContainsRepairPlanPhrase(normalized))
        {
            resolution = null;
            return false;
        }

        var scope = DetectRepairScope(normalized);
        var target = ExtractRepairTarget(prompt, activeTargetRelativePath);

        var arguments = new List<(string Key, string Value)>();
        if (!string.IsNullOrWhiteSpace(scope))
            arguments.Add(("scope", scope));
        if (!string.IsNullOrWhiteSpace(target))
            arguments.Add(("path", target));

        var reason = string.IsNullOrWhiteSpace(target)
            ? "Resolved plan_repair request locally using current workspace context."
            : $"Resolved plan_repair request locally using current workspace context and target {target}.";

        resolution = BuildResolution("plan_repair", reason, arguments.ToArray());
        return true;
    }

    private static bool TryResolvePatchPreviewRequest(string prompt, string activeTargetRelativePath, out ResolvedUserIntent? resolution)
    {
        var normalized = Normalize(prompt);
        if (!ContainsPatchPreviewPhrase(normalized))
        {
            resolution = null;
            return false;
        }

        var scope = DetectRepairScope(normalized);
        var target = FirstNonEmpty(
            ExtractRepairTarget(prompt, activeTargetRelativePath),
            GetPreferredFileTarget(activeTargetRelativePath));
        var arguments = BuildPatchArguments(scope, target);
        var reason = string.IsNullOrWhiteSpace(target)
            ? "Resolved preview_patch_draft request locally using current workspace context."
            : $"Resolved preview_patch_draft request locally using current workspace context and target {target}.";

        resolution = BuildResolution("preview_patch_draft", reason, arguments.ToArray());
        return true;
    }

    private static bool TryResolvePatchApplyRequest(string prompt, string activeTargetRelativePath, out ResolvedUserIntent? resolution)
    {
        var normalized = Normalize(prompt);
        if (!ContainsPatchApplyPhrase(normalized))
        {
            resolution = null;
            return false;
        }

        var target = FirstNonEmpty(
            ExtractRepairTarget(prompt, activeTargetRelativePath),
            GetPreferredFileTarget(activeTargetRelativePath));
        var reason = string.IsNullOrWhiteSpace(target)
            ? "Resolved apply_patch_draft request locally using current workspace context."
            : $"Resolved apply_patch_draft request locally using current workspace context and target {target}.";

        resolution = BuildResolution(
            "apply_patch_draft",
            reason,
            string.IsNullOrWhiteSpace(target) ? [] : [("path", target)]);
        return true;
    }

    private static bool TryResolvePatchVerificationRequest(string prompt, string activeTargetRelativePath, out ResolvedUserIntent? resolution)
    {
        var normalized = Normalize(prompt);
        if (!ContainsPatchVerificationPhrase(normalized))
        {
            resolution = null;
            return false;
        }

        var target = FirstNonEmpty(
            ExtractRepairTarget(prompt, activeTargetRelativePath),
            GetPreferredFileTarget(activeTargetRelativePath));
        var reason = string.IsNullOrWhiteSpace(target)
            ? "Resolved verify_patch_draft request locally using current workspace context."
            : $"Resolved verify_patch_draft request locally using current workspace context and target {target}.";

        resolution = BuildResolution(
            "verify_patch_draft",
            reason,
            string.IsNullOrWhiteSpace(target) ? [] : [("path", target)]);
        return true;
    }

    private static bool IsGitDiffRequest(string normalizedPrompt)
    {
        return normalizedPrompt.Contains("show git diff", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("git diff", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt == "what changed"
            || normalizedPrompt.Contains("what changed", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt == "show diff";
    }

    private static bool IsDetectBuildSystemRequest(string normalizedPrompt)
    {
        return normalizedPrompt.Contains("what build system is this", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("detect build system", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("how do i build this repo", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what can ram use here", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsListBuildProfilesRequest(string normalizedPrompt)
    {
        return normalizedPrompt.Contains("list build profiles", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("show build profiles", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("which build profile", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what safe build target can i run", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveFailureContextRequest(string normalizedPrompt, out ResolvedUserIntent? resolution)
    {
        if (normalizedPrompt.Contains("show failing test file", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("open failing test file", StringComparison.OrdinalIgnoreCase))
        {
            resolution = BuildResolution("open_failure_context", "Resolved failure navigation request locally using stored failure context for the failing test file.", ("scope", "test"));
            return true;
        }

        if (normalizedPrompt.Contains("take me to the first error", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("open the error file", StringComparison.OrdinalIgnoreCase))
        {
            resolution = BuildResolution("open_failure_context", "Resolved failure navigation request locally using stored failure context for the first build error.", ("scope", "build"));
            return true;
        }

        if (normalizedPrompt.Contains("show me the broken file", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("open what broke", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what file should i check", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("where is the error", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what file is broken", StringComparison.OrdinalIgnoreCase))
        {
            resolution = BuildResolution("open_failure_context", "Resolved failure navigation request locally using current workspace context.");
            return true;
        }

        resolution = null;
        return false;
    }

    private static bool ContainsRepairPlanPhrase(string normalizedPrompt)
    {
        return normalizedPrompt.Contains("how should i fix this", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("suggest a fix", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what change should i make", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("make a repair plan", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("plan the fix", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("repair plan", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsPatchPreviewPhrase(string normalizedPrompt)
    {
        return normalizedPrompt.Contains("show me the patch", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("preview the fix", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what would you change", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("show the edit", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("draft the patch", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("preview patch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsPatchApplyPhrase(string normalizedPrompt)
    {
        return normalizedPrompt.Contains("apply the fix", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("apply that patch", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("make the change", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("use the draft", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("apply patch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsPatchVerificationPhrase(string normalizedPrompt)
    {
        return normalizedPrompt.Contains("did that fix it", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("verify the patch", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("verify the fix", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("check if it works now", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("did the fix help", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("rerun the test", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("rerun the build", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("verify patch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsGenericBuildPhrase(string normalizedPrompt)
    {
        return normalizedPrompt == "run build"
            || normalizedPrompt == "build this"
            || normalizedPrompt == "build this repo"
            || normalizedPrompt == "build the repo"
            || normalizedPrompt == "build project"
            || normalizedPrompt == "compile this";
    }

    private static string DetectRepairScope(string normalizedPrompt)
    {
        if (normalizedPrompt.Contains("test", StringComparison.OrdinalIgnoreCase))
            return "test";

        if (normalizedPrompt.Contains("build", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("compiler", StringComparison.OrdinalIgnoreCase))
        {
            return "build";
        }

        return "";
    }

    private static string ExtractRepairTarget(string prompt, string activeTargetRelativePath)
    {
        var match = RepairPlanTargetPattern.Match(prompt ?? "");
        if (!match.Success)
            return "";

        var target = CleanTarget(match.Groups["target"].Value);
        if (string.IsNullOrWhiteSpace(target))
            return "";

        if (IsCurrentFileReference(target))
            return activeTargetRelativePath;

        return LooksLikePathTarget(target)
            ? target
            : "";
    }

    private static List<(string Key, string Value)> BuildPatchArguments(string scope, string target)
    {
        var arguments = new List<(string Key, string Value)>();
        if (!string.IsNullOrWhiteSpace(scope))
            arguments.Add(("scope", scope));
        if (!string.IsNullOrWhiteSpace(target))
            arguments.Add(("path", target));
        return arguments;
    }

    private static bool LooksLikeProjectTarget(string value)
    {
        var normalized = Normalize(value);
        return normalized.Contains(".sln", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(".csproj", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(" sln", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(" csproj", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("solution", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("project", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurrentProjectReference(string value)
    {
        var normalized = Normalize(value);
        return normalized.Contains("this project", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("that project", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("the solution", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("this solution", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurrentFileReference(string value)
    {
        var normalized = Normalize(value);
        return normalized.Contains("this file", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("that file", StringComparison.OrdinalIgnoreCase)
            || normalized == "it";
    }

    private static bool LooksLikePathTarget(string value)
    {
        return value.Contains('/', StringComparison.OrdinalIgnoreCase)
            || value.Contains('\\', StringComparison.OrdinalIgnoreCase)
            || value.Contains('.', StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPreferredProjectTarget(string activeTargetRelativePath)
    {
        return LooksLikeResolvedProjectPath(activeTargetRelativePath)
            ? activeTargetRelativePath
            : "";
    }

    private static string GetPreferredFileTarget(string activeTargetRelativePath)
    {
        return LooksLikePathTarget(activeTargetRelativePath)
            ? activeTargetRelativePath
            : "";
    }

    private static string GetPreferredTestTarget(string activeTargetRelativePath)
    {
        if (string.IsNullOrWhiteSpace(activeTargetRelativePath))
            return "";

        if (activeTargetRelativePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return activeTargetRelativePath;

        return LooksLikeTestProjectPath(activeTargetRelativePath)
            ? activeTargetRelativePath
            : "";
    }

    private static bool LooksLikeResolvedProjectPath(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && (value.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeTestProjectPath(string value)
    {
        var normalized = Normalize(value);
        return normalized.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("test", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("spec", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanTarget(string value)
    {
        return (value ?? "").Trim().Trim('"', '\'').TrimEnd('.', '?', '!', ';', ':', ',');
    }

    private static string Normalize(string value)
    {
        return (value ?? "").Trim().ToLowerInvariant();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static ResolvedUserIntent BuildResolution(
        string toolName,
        string reason,
        params (string Key, string Value)[] arguments)
    {
        var request = new ToolRequest
        {
            ToolName = toolName,
            Reason = reason
        };

        foreach (var (key, value) in arguments)
        {
            request.Arguments[key] = value;
        }

        return new ResolvedUserIntent
        {
            ToolRequest = request,
            ResolutionReason = reason
        };
    }

    private static ResolvedUserIntent BuildProfileResolution(
        WorkspaceBuildProfileRecord profile,
        string toolName,
        string reason)
    {
        var arguments = new List<(string Key, string Value)>();

        if (string.Equals(toolName, "dotnet_build", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "dotnet_test", StringComparison.OrdinalIgnoreCase))
        {
            var targetPath = string.Equals(toolName, "dotnet_test", StringComparison.OrdinalIgnoreCase)
                ? FirstNonEmpty(profile.TestTargetPath, profile.BuildTargetPath)
                : profile.BuildTargetPath;

            if (!string.IsNullOrWhiteSpace(targetPath))
                arguments.Add(("project", targetPath));
        }
        else if (string.Equals(toolName, "cmake_build", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(profile.BuildDirectoryPath))
                arguments.Add(("build_dir", profile.BuildDirectoryPath));
        }
        else if (string.Equals(toolName, "cmake_configure", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(profile.ConfigureTargetPath))
                arguments.Add(("source_dir", profile.ConfigureTargetPath));
            if (!string.IsNullOrWhiteSpace(profile.BuildDirectoryPath))
                arguments.Add(("build_dir", profile.BuildDirectoryPath));
        }
        else if (string.Equals(toolName, "make_build", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "ninja_build", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(profile.BuildTargetPath))
                arguments.Add(("directory", profile.BuildTargetPath));
        }
        else if (string.Equals(toolName, "run_build_script", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(profile.BuildTargetPath))
                arguments.Add(("path", profile.BuildTargetPath));
        }

        return BuildResolution(toolName, reason, arguments.ToArray());
    }

    private static bool IsCMakeConfigured(string workspaceRoot, string buildDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return false;

        var normalizedBuildDirectory = string.IsNullOrWhiteSpace(buildDirectory) ? "build" : buildDirectory;
        var fullBuildDirectory = Path.GetFullPath(Path.Combine(workspaceRoot, normalizedBuildDirectory.Replace('/', Path.DirectorySeparatorChar)));
        var cachePath = Path.Combine(fullBuildDirectory, "CMakeCache.txt");
        return File.Exists(cachePath);
    }
}
