using System.IO;
using RAM.Models;

namespace RAM.Services;

public sealed class WorkspaceBuildIndexService
{
    private readonly WorkspaceStructuralExclusionPolicyService _exclusionPolicyService = new();
    private readonly FileIdentityService _fileIdentityService = new();

    public IReadOnlyList<WorkspaceBuildItem> ListItems(string workspaceRoot)
    {
        EnsureWorkspaceExists(workspaceRoot);

        var items = new List<WorkspaceBuildItem>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(workspaceRoot));

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (_exclusionPolicyService.ShouldExcludeDirectory(directory))
                    continue;

                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current))
            {
                if (!TryCreateItem(workspaceRoot, file, out var item))
                    continue;

                items.Add(item);
            }
        }

        items.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        return items;
    }

    public WorkspaceBuildResolution ResolveForInspection(string workspaceRoot, string requestedTarget, string activeTargetRelativePath)
    {
        var items = ListBuildableTargets(workspaceRoot);
        if (items.Count == 0)
        {
            return WorkspaceBuildResolution.FailureResult(
                requestedTarget,
                NormalizeInput(requestedTarget),
                BuildFailureMessage(
                    "inspect_project",
                    requestedTarget,
                    NormalizeInput(requestedTarget),
                    "no buildable .sln or .csproj files were found in the current workspace."));
        }

        if (!string.IsNullOrWhiteSpace(requestedTarget))
            return ResolveRequestedTarget("inspect_project", workspaceRoot, requestedTarget, items);

        var activeTargetMatch = FindExactPathMatch(items, activeTargetRelativePath);
        if (activeTargetMatch is not null)
        {
            return WorkspaceBuildResolution.SuccessResult(
                activeTargetMatch,
                requestedTarget,
                NormalizeInput(activeTargetRelativePath),
                $"Using active target: {activeTargetMatch.RelativePath}");
        }

        var solutions = items.Where(item => item.ItemType == "solution").ToList();
        if (solutions.Count == 1)
        {
            return WorkspaceBuildResolution.SuccessResult(
                solutions[0],
                requestedTarget,
                "",
                $"Using the only discovered solution: {solutions[0].RelativePath}");
        }

        if (solutions.Count == 0 && items.Count == 1)
        {
            return WorkspaceBuildResolution.SuccessResult(
                items[0],
                requestedTarget,
                "",
                $"Using the only discovered project: {items[0].RelativePath}");
        }

        var candidates = solutions.Count > 0 ? solutions : items;
        return WorkspaceBuildResolution.FailureResult(
            requestedTarget,
            "",
            BuildFailureMessage(
                "inspect_project",
                requestedTarget,
                "",
                "multiple build targets are available. Say which one to inspect using path=<relative path>.",
                candidates),
            candidates);
    }

    public WorkspaceBuildResolution ResolveForTesting(string workspaceRoot, string requestedTarget, string activeTargetRelativePath)
    {
        var items = ListBuildableTargets(workspaceRoot);
        if (items.Count == 0)
        {
            return WorkspaceBuildResolution.FailureResult(
                requestedTarget,
                NormalizeInput(requestedTarget),
                BuildFailureMessage(
                    "dotnet_test",
                    requestedTarget,
                    NormalizeInput(requestedTarget),
                    "no buildable .sln or .csproj files were found in the current workspace."),
                failureKind: "no_workspace_targets",
                reasonCode: "no_build_targets");
        }

        if (!string.IsNullOrWhiteSpace(requestedTarget))
            return ResolveRequestedTarget("dotnet_test", workspaceRoot, requestedTarget, items);

        var activeTargetMatch = FindExactPathMatch(items, activeTargetRelativePath);
        if (activeTargetMatch is not null && IsPreferredTestTarget(activeTargetMatch))
        {
            return WorkspaceBuildResolution.SuccessResult(
                activeTargetMatch,
                requestedTarget,
                NormalizeInput(activeTargetRelativePath),
                $"Using active test target: {activeTargetMatch.RelativePath}");
        }

        var solutions = items.Where(item => item.ItemType == "solution").ToList();
        if (solutions.Count == 1)
        {
            return WorkspaceBuildResolution.SuccessResult(
                solutions[0],
                requestedTarget,
                "",
                $"Using the only discovered solution: {solutions[0].RelativePath}");
        }

        var testProjects = items.Where(item => item.ItemType == "project" && item.LikelyTestProject).ToList();
        if (testProjects.Count == 1)
        {
            return WorkspaceBuildResolution.SuccessResult(
                testProjects[0],
                requestedTarget,
                "",
                $"Using the only discovered test project: {testProjects[0].RelativePath}");
        }

        if (activeTargetMatch is not null && activeTargetMatch.ItemType == "project" && !activeTargetMatch.LikelyTestProject)
        {
            return WorkspaceBuildResolution.FailureResult(
                requestedTarget,
                NormalizeInput(activeTargetRelativePath),
                BuildFailureMessage(
                    "dotnet_test",
                    requestedTarget,
                    NormalizeInput(activeTargetRelativePath),
                    $"active target '{activeTargetMatch.RelativePath}' is not recognized as a likely test project, and no single solution or test project could be selected.",
                    BuildPreferredTestCandidates(items)),
                BuildPreferredTestCandidates(items),
                failureKind: "undiscovered_target",
                reasonCode: "active_target_not_test");
        }

        var candidates = BuildPreferredTestCandidates(items);
        var reason = candidates.Count == 0
            ? "no likely test solution or project was discovered in the current workspace."
            : "multiple test targets are available. Say which one to test using project=<relative path>.";

        return WorkspaceBuildResolution.FailureResult(
            requestedTarget,
            "",
            BuildFailureMessage("dotnet_test", requestedTarget, "", reason, candidates),
            candidates,
            failureKind: candidates.Count == 0 ? "missing_target" : "ambiguous_target",
            reasonCode: candidates.Count == 0 ? "no_test_targets_discovered" : "multiple_test_targets",
            prerequisiteRequired: candidates.Count == 0);
    }

    public WorkspaceBuildResolution ResolveForBuild(string workspaceRoot, string requestedTarget, string activeTargetRelativePath)
    {
        var items = ListBuildableTargets(workspaceRoot);
        if (items.Count == 0)
        {
            return WorkspaceBuildResolution.FailureResult(
                requestedTarget,
                NormalizeInput(requestedTarget),
                BuildFailureMessage(
                    "dotnet_build",
                    requestedTarget,
                    NormalizeInput(requestedTarget),
                    "no buildable .sln or .csproj files were found in the current workspace."));
        }

        if (!string.IsNullOrWhiteSpace(requestedTarget))
        {
            var requestedResolution = ResolveRequestedTarget("dotnet_build", workspaceRoot, requestedTarget, items);
            if (requestedResolution.Success)
                return requestedResolution;

            if (TryResolveDeterministicBuildFallback(items, activeTargetRelativePath, out var fallbackItem, out var fallbackReason))
            {
                return WorkspaceBuildResolution.SuccessResult(
                    fallbackItem,
                    requestedTarget,
                    NormalizeInput(requestedTarget),
                    $"Requested build target `{NormalizeInput(requestedTarget)}` was not found; {fallbackReason}");
            }

            return requestedResolution;
        }

        if (TryResolveDeterministicBuildFallback(items, activeTargetRelativePath, out var selectedItem, out var reason))
        {
            return WorkspaceBuildResolution.SuccessResult(
                selectedItem,
                requestedTarget,
                NormalizeInput(activeTargetRelativePath),
                reason);
        }

        var solutions = items.Where(item => item.ItemType == "solution").ToList();
        var candidates = solutions.Count > 0 ? solutions : items;
        return WorkspaceBuildResolution.FailureResult(
            requestedTarget,
            NormalizeInput(activeTargetRelativePath),
            BuildFailureMessage(
                "dotnet_build",
                requestedTarget,
                NormalizeInput(activeTargetRelativePath),
                "multiple build targets are available. Say which one to build using project=<relative path>.",
                candidates),
            candidates);
    }

    public WorkspaceBuildResolution ResolveRequestedTarget(string toolName, string workspaceRoot, string requestedTarget, IReadOnlyList<WorkspaceBuildItem>? items = null)
    {
        EnsureWorkspaceExists(workspaceRoot);

        var index = items?.ToList() ?? ListBuildableTargets(workspaceRoot);
        var rawTarget = requestedTarget ?? "";
        var normalizedTarget = NormalizeInput(rawTarget);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return WorkspaceBuildResolution.FailureResult(
                rawTarget,
                normalizedTarget,
                BuildFailureMessage(toolName, rawTarget, normalizedTarget, "no target was provided."),
                failureKind: "missing_target",
                reasonCode: "missing_target_argument");
        }

        if (Path.IsPathRooted(normalizedTarget))
        {
            var fullPath = Path.GetFullPath(normalizedTarget);
            if (!IsInsideWorkspace(workspaceRoot, fullPath))
            {
                return WorkspaceBuildResolution.FailureResult(
                    rawTarget,
                    normalizedTarget,
                    BuildFailureMessage(toolName, rawTarget, normalizedTarget, "requested path is outside the active workspace.", index),
                    index,
                    failureKind: "outside_workspace",
                    reasonCode: "target_outside_workspace");
            }

            normalizedTarget = NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, fullPath));
        }
        else
        {
            normalizedTarget = NormalizeRelativePath(normalizedTarget);
        }

        var exactMatch = FindExactPathMatch(index, normalizedTarget);
        if (exactMatch is not null)
        {
            return WorkspaceBuildResolution.SuccessResult(
                exactMatch,
                rawTarget,
                normalizedTarget,
                $"Resolved explicit target: {exactMatch.RelativePath}");
        }

        var pathLikeTarget = LooksLikePathOrKnownBuildFile(normalizedTarget);
        var nameMatches = FindNameMatches(index, normalizedTarget);
        if (nameMatches.Count == 1)
        {
            return WorkspaceBuildResolution.SuccessResult(
                nameMatches[0],
                rawTarget,
                normalizedTarget,
                $"Resolved unique target name: {nameMatches[0].RelativePath}");
        }

        if (nameMatches.Count > 1)
        {
            return WorkspaceBuildResolution.FailureResult(
                rawTarget,
                normalizedTarget,
                BuildFailureMessage(
                    toolName,
                    rawTarget,
                    normalizedTarget,
                    "target matched multiple workspace items.",
                    nameMatches),
                nameMatches,
                failureKind: "ambiguous_target",
                reasonCode: "multiple_target_matches");
        }

        var reason = pathLikeTarget
            ? "requested path does not exist under the current workspace."
            : "target name did not match any discovered workspace item.";

        return WorkspaceBuildResolution.FailureResult(
            rawTarget,
            normalizedTarget,
            BuildFailureMessage(toolName, rawTarget, normalizedTarget, reason, index),
            index.ToList(),
            failureKind: pathLikeTarget ? "missing_target" : "undiscovered_target",
            reasonCode: pathLikeTarget ? "requested_target_missing" : "target_name_not_found",
            prerequisiteRequired: string.Equals(toolName, "dotnet_test", StringComparison.OrdinalIgnoreCase)
                && pathLikeTarget
                && normalizedTarget.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPreferredTestTarget(WorkspaceBuildItem item)
    {
        return item.ItemType == "solution"
            || (item.ItemType == "project" && item.LikelyTestProject);
    }

    private static bool TryResolveDeterministicBuildFallback(
        IReadOnlyList<WorkspaceBuildItem> items,
        string activeTargetRelativePath,
        out WorkspaceBuildItem selectedItem,
        out string reason)
    {
        var activeTargetMatch = FindExactPathMatch(items, activeTargetRelativePath);
        if (activeTargetMatch is not null)
        {
            selectedItem = activeTargetMatch;
            reason = $"re-resolved to active target: {activeTargetMatch.RelativePath}";
            return true;
        }

        var solutions = items.Where(item => item.ItemType == "solution").ToList();
        if (solutions.Count == 1)
        {
            selectedItem = solutions[0];
            reason = $"re-resolved to the only discovered solution: {solutions[0].RelativePath}";
            return true;
        }

        if (solutions.Count == 0 && items.Count == 1)
        {
            selectedItem = items[0];
            reason = $"re-resolved to the only discovered project: {items[0].RelativePath}";
            return true;
        }

        selectedItem = new WorkspaceBuildItem();
        reason = "";
        return false;
    }

    private List<WorkspaceBuildItem> ListBuildableTargets(string workspaceRoot)
    {
        return ListItems(workspaceRoot)
            .Where(item => item.ItemType is "solution" or "project")
            .ToList();
    }

    private bool TryCreateItem(string workspaceRoot, string fullPath, out WorkspaceBuildItem item)
    {
        var fileName = Path.GetFileName(fullPath);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();

        item = new WorkspaceBuildItem();
        if (string.Equals(fileName, "Directory.Build.props", StringComparison.OrdinalIgnoreCase))
        {
            item.ItemType = "directory_build_props";
            item.LanguageHint = ".NET build configuration";
        }
        else if (string.Equals(fileName, "Directory.Build.targets", StringComparison.OrdinalIgnoreCase))
        {
            item.ItemType = "directory_build_targets";
            item.LanguageHint = ".NET build configuration";
        }
        else if (extension == ".sln")
        {
            item.ItemType = "solution";
            item.LanguageHint = ".NET solution";
        }
        else if (extension == ".csproj")
        {
            item.ItemType = "project";
            item.LanguageHint = "C# / .NET";
        }
        else
        {
            return false;
        }

        var relativePath = NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, fullPath));
        var parentDirectory = Path.GetDirectoryName(relativePath);
        var identity = _fileIdentityService.Identify(relativePath);

        item.RelativePath = relativePath;
        item.FileName = Path.GetFileName(relativePath);
        item.ParentDirectory = string.IsNullOrWhiteSpace(parentDirectory)
            ? "."
            : NormalizeRelativePath(parentDirectory);
        item.LikelyTestProject = IsLikelyTestProject(relativePath, identity);
        item.Identity = identity;
        return true;
    }

    private static bool IsLikelyTestProject(string relativePath, FileIdentityRecord identity)
    {
        if (string.Equals(identity.Role, "tests", StringComparison.OrdinalIgnoreCase)
            || string.Equals(identity.FileType, "test_project", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var normalized = relativePath.Replace('\\', '/').ToLowerInvariant();
        return normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(".test.", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(".tests.", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("integrationtest", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("integrationtests", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("spec", StringComparison.OrdinalIgnoreCase);
    }

    private static List<WorkspaceBuildItem> FindNameMatches(IEnumerable<WorkspaceBuildItem> items, string normalizedTarget)
    {
        var targetKey = ToLooseKey(normalizedTarget);
        return items
            .Where(item =>
                string.Equals(item.RelativePath, normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.FileName, normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(item.FileName), normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ToLooseKey(item.FileName), targetKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ToLooseKey(Path.GetFileNameWithoutExtension(item.FileName)), targetKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static WorkspaceBuildItem? FindExactPathMatch(IEnumerable<WorkspaceBuildItem> items, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var normalized = NormalizeRelativePath(relativePath);
        return items.FirstOrDefault(item =>
            string.Equals(item.RelativePath, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static List<WorkspaceBuildItem> BuildPreferredTestCandidates(IEnumerable<WorkspaceBuildItem> items)
    {
        return items
            .Where(IsPreferredTestTarget)
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildFailureMessage(
        string toolName,
        string rawTarget,
        string normalizedTarget,
        string reason,
        IEnumerable<WorkspaceBuildItem>? candidates = null)
    {
        var lines = new List<string>
        {
            $"{toolName} failed:",
            $"Raw request target: {DisplayValue(rawTarget)}",
            $"Normalized target: {DisplayValue(normalizedTarget)}",
            $"Resolution: {reason}"
        };

        var boundedCandidates = candidates?
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList() ?? [];

        if (boundedCandidates.Count > 0)
        {
            lines.Add("Discovered local alternatives:");
            foreach (var candidate in boundedCandidates)
            {
                var testLabel = candidate.LikelyTestProject ? ", test" : "";
                var typeLabel = string.IsNullOrWhiteSpace(candidate.Identity.FileType)
                    ? candidate.ItemType
                    : candidate.Identity.FileType;
                var roleLabel = string.IsNullOrWhiteSpace(candidate.Identity.Role)
                    ? ""
                    : $", role={candidate.Identity.Role}";
                lines.Add($"- {candidate.RelativePath} [{typeLabel}{testLabel}{roleLabel}]");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static bool LooksLikePathOrKnownBuildFile(string value)
    {
        return value.Contains('/', StringComparison.OrdinalIgnoreCase)
            || value.Contains('\\', StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeInput(string value)
    {
        var normalized = (value ?? "").Trim().Trim('"', '\'');
        return normalized.Replace('\\', '/');
    }

    private static string NormalizeRelativePath(string path)
    {
        return (path ?? "").Replace('\\', '/');
    }

    private static string ToLooseKey(string value)
    {
        var chars = (value ?? "")
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();

        return new string(chars);
    }

    private static void EnsureWorkspaceExists(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace not found: {workspaceRoot}");
    }

    private static bool IsInsideWorkspace(string workspaceRoot, string path)
    {
        var fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        var fullPath = Path.GetFullPath(path);
        var workspacePrefix = fullWorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return string.Equals(fullPath, fullWorkspaceRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase);
    }

}
