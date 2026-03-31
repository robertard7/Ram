using System.IO;
using RAM.Models;

namespace RAM.Services;

public sealed class BuildSystemDetectionService
{
    public BuildSystemDetectionResult Detect(string workspaceRoot)
    {
        EnsureWorkspaceExists(workspaceRoot);

        var signals = EnumerateSignals(workspaceRoot);
        var profiles = BuildProfiles(workspaceRoot, signals);
        var preferredProfile = SelectPreferredProfile(profiles);

        if (preferredProfile is not null)
            preferredProfile.PreferredProfile = true;

        return new BuildSystemDetectionResult
        {
            WorkspaceRoot = workspaceRoot,
            DetectedType = DetermineDetectedType(profiles),
            Confidence = DetermineDetectionConfidence(profiles, preferredProfile),
            Summary = BuildSummary(profiles, preferredProfile),
            Signals = signals,
            Profiles = profiles,
            PreferredProfile = preferredProfile
        };
    }

    public IReadOnlyList<WorkspaceBuildProfileRecord> ListProfiles(string workspaceRoot)
    {
        return Detect(workspaceRoot).Profiles;
    }

    public WorkspaceBuildProfileRecord? GetPreferredProfile(string workspaceRoot)
    {
        return Detect(workspaceRoot).PreferredProfile;
    }

    public WorkspaceBuildProfileRecord? ResolveProfile(
        string workspaceRoot,
        BuildSystemType requestedType,
        string requestedPath,
        out string message)
    {
        var detection = Detect(workspaceRoot);
        var profiles = detection.Profiles;

        if (profiles.Count == 0)
        {
            message = "No supported build system was detected in the current workspace.";
            return null;
        }

        if (requestedType != BuildSystemType.Unknown)
        {
            var matchingTypeProfiles = profiles
                .Where(profile => profile.BuildSystemType == requestedType)
                .ToList();

            if (matchingTypeProfiles.Count == 0)
            {
                message = $"No {requestedType} build profile was detected in the current workspace.";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                var normalizedRequestedPath = NormalizePath(requestedPath);
                var matchingPathProfile = matchingTypeProfiles.FirstOrDefault(profile =>
                    string.Equals(profile.PrimaryTargetPath, normalizedRequestedPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(profile.BuildTargetPath, normalizedRequestedPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(profile.ConfigureTargetPath, normalizedRequestedPath, StringComparison.OrdinalIgnoreCase));

                if (matchingPathProfile is not null)
                {
                    message = $"Resolved {requestedType} build profile for {matchingPathProfile.PrimaryTargetPath}.";
                    return matchingPathProfile;
                }

                if (matchingTypeProfiles.Count == 1 && AllowsExplicitTargetOverride(requestedType))
                {
                    message = $"Resolved {requestedType} build profile for explicit target {normalizedRequestedPath}.";
                    return matchingTypeProfiles[0];
                }

                message = $"No {requestedType} build profile matched {normalizedRequestedPath}.";
                return null;
            }

            message = $"Resolved {requestedType} build profile.";
            return matchingTypeProfiles.OrderByDescending(ScoreProfile).First();
        }

        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            var normalizedRequestedPath = NormalizePath(requestedPath);
            var matchingPathProfiles = profiles
                .Where(profile =>
                    string.Equals(profile.PrimaryTargetPath, normalizedRequestedPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(profile.BuildTargetPath, normalizedRequestedPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(profile.ConfigureTargetPath, normalizedRequestedPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matchingPathProfiles.Count == 1)
            {
                message = $"Resolved build profile for {matchingPathProfiles[0].PrimaryTargetPath}.";
                return matchingPathProfiles[0];
            }

            if (matchingPathProfiles.Count > 1)
            {
                message = $"Multiple build profiles matched {normalizedRequestedPath}.";
                return null;
            }
        }

        if (detection.PreferredProfile is not null)
        {
            message = $"Using preferred build profile: {detection.PreferredProfile.BuildSystemType}.";
            return detection.PreferredProfile;
        }

        message = "RAM could not select a single preferred build profile for this workspace.";
        return null;
    }

    private static List<BuildDetectionSignalRecord> EnumerateSignals(string workspaceRoot)
    {
        var signals = new List<BuildDetectionSignalRecord>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(workspaceRoot));

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            foreach (var directory in Directory.EnumerateDirectories(current).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (IsIgnoredDirectory(directory))
                    continue;

                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (TryCreateSignal(workspaceRoot, file, out var signal))
                    signals.Add(signal);
            }
        }

        return signals
            .OrderBy(signal => signal.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(signal => signal.SignalType, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<WorkspaceBuildProfileRecord> BuildProfiles(
        string workspaceRoot,
        IReadOnlyList<BuildDetectionSignalRecord> signals)
    {
        var profiles = new List<WorkspaceBuildProfileRecord>();

        var dotnetSignals = signals.Where(signal => signal.BuildSystemType == BuildSystemType.Dotnet).ToList();
        if (dotnetSignals.Count > 0)
            profiles.Add(BuildDotnetProfile(workspaceRoot, dotnetSignals));

        var cmakeSignals = signals.Where(signal => signal.BuildSystemType == BuildSystemType.CMake).ToList();
        if (cmakeSignals.Count > 0)
            profiles.Add(BuildCMakeProfile(workspaceRoot, cmakeSignals));

        var cmakeBuildDirectories = GetGeneratedCMakeBuildDirectories(cmakeSignals);

        var makeSignals = signals
            .Where(signal => signal.BuildSystemType == BuildSystemType.Make)
            .Where(signal => !IsGeneratedCMakeBuildSignal(signal, cmakeBuildDirectories))
            .ToList();
        if (makeSignals.Count > 0)
            profiles.Add(BuildMakeProfile(workspaceRoot, makeSignals));

        var ninjaSignals = signals
            .Where(signal => signal.BuildSystemType == BuildSystemType.Ninja)
            .Where(signal => !IsGeneratedCMakeBuildSignal(signal, cmakeBuildDirectories))
            .ToList();
        if (ninjaSignals.Count > 0)
            profiles.Add(BuildNinjaProfile(workspaceRoot, ninjaSignals));

        var scriptSignals = signals.Where(signal => signal.BuildSystemType == BuildSystemType.Script).ToList();
        if (scriptSignals.Count > 0)
            profiles.Add(BuildScriptProfile(workspaceRoot, scriptSignals));

        return profiles
            .OrderByDescending(ScoreProfile)
            .ThenBy(profile => profile.BuildSystemType.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static WorkspaceBuildProfileRecord BuildDotnetProfile(string workspaceRoot, IReadOnlyList<BuildDetectionSignalRecord> signals)
    {
        var solutions = signals.Where(signal => signal.RelativePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)).ToList();
        var projects = signals.Where(signal => signal.RelativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
        var primarySignal = PickPrimarySignal(solutions.Count > 0 ? solutions : projects);
        var primaryTarget = primarySignal?.RelativePath ?? "";
        var confidence = primarySignal?.IsRootSignal == true || solutions.Count == 1 || projects.Count == 1 ? "high" : "medium";

        return new WorkspaceBuildProfileRecord
        {
            WorkspaceRoot = workspaceRoot,
            BuildSystemType = BuildSystemType.Dotnet,
            PrimaryTargetPath = primaryTarget,
            ConfigureToolFamily = "",
            BuildToolFamily = "dotnet_build",
            TestToolFamily = "dotnet_test",
            BuildTargetPath = primaryTarget,
            TestTargetPath = primaryTarget,
            Confidence = confidence,
            DetectionSignals = signals.Select(FormatSignal).ToList()
        };
    }

    private static WorkspaceBuildProfileRecord BuildCMakeProfile(string workspaceRoot, IReadOnlyList<BuildDetectionSignalRecord> signals)
    {
        var primarySignal = PickPrimarySignal(signals.Where(signal =>
            string.Equals(Path.GetFileName(signal.RelativePath), "CMakeLists.txt", StringComparison.OrdinalIgnoreCase)).ToList());
        var sourceDirectory = NormalizeDirectory(primarySignal?.RelativePath);
        var buildDirectory = string.IsNullOrWhiteSpace(sourceDirectory) || sourceDirectory == "."
            ? "build"
            : $"{sourceDirectory}/build";

        return new WorkspaceBuildProfileRecord
        {
            WorkspaceRoot = workspaceRoot,
            BuildSystemType = BuildSystemType.CMake,
            PrimaryTargetPath = primarySignal?.RelativePath ?? "",
            ConfigureToolFamily = "cmake_configure",
            BuildToolFamily = "cmake_build",
            TestToolFamily = "",
            ConfigureTargetPath = sourceDirectory,
            BuildTargetPath = buildDirectory,
            BuildDirectoryPath = buildDirectory,
            Confidence = primarySignal?.IsRootSignal == true ? "high" : "medium",
            DetectionSignals = signals.Select(FormatSignal).ToList()
        };
    }

    private static WorkspaceBuildProfileRecord BuildMakeProfile(string workspaceRoot, IReadOnlyList<BuildDetectionSignalRecord> signals)
    {
        var primarySignal = PickPrimarySignal(signals);
        var directory = NormalizeDirectory(primarySignal?.RelativePath);

        return new WorkspaceBuildProfileRecord
        {
            WorkspaceRoot = workspaceRoot,
            BuildSystemType = BuildSystemType.Make,
            PrimaryTargetPath = primarySignal?.RelativePath ?? "",
            BuildToolFamily = "make_build",
            BuildTargetPath = directory,
            Confidence = primarySignal?.IsRootSignal == true ? "high" : "medium",
            DetectionSignals = signals.Select(FormatSignal).ToList()
        };
    }

    private static WorkspaceBuildProfileRecord BuildNinjaProfile(string workspaceRoot, IReadOnlyList<BuildDetectionSignalRecord> signals)
    {
        var primarySignal = PickPrimarySignal(signals);
        var directory = NormalizeDirectory(primarySignal?.RelativePath);

        return new WorkspaceBuildProfileRecord
        {
            WorkspaceRoot = workspaceRoot,
            BuildSystemType = BuildSystemType.Ninja,
            PrimaryTargetPath = primarySignal?.RelativePath ?? "",
            BuildToolFamily = "ninja_build",
            BuildTargetPath = directory,
            BuildDirectoryPath = directory,
            Confidence = primarySignal?.IsRootSignal == true ? "high" : "medium",
            DetectionSignals = signals.Select(FormatSignal).ToList()
        };
    }

    private static WorkspaceBuildProfileRecord BuildScriptProfile(string workspaceRoot, IReadOnlyList<BuildDetectionSignalRecord> signals)
    {
        var configureSignals = signals.Where(signal =>
            Path.GetFileName(signal.RelativePath).StartsWith("configure", StringComparison.OrdinalIgnoreCase)).ToList();
        var buildSignals = signals.Where(signal =>
            Path.GetFileName(signal.RelativePath).StartsWith("build", StringComparison.OrdinalIgnoreCase)).ToList();
        var primarySignal = PickPrimarySignal(buildSignals.Count > 0 ? buildSignals : signals);

        return new WorkspaceBuildProfileRecord
        {
            WorkspaceRoot = workspaceRoot,
            BuildSystemType = BuildSystemType.Script,
            PrimaryTargetPath = primarySignal?.RelativePath ?? "",
            ConfigureToolFamily = configureSignals.Count > 0 ? "run_build_script" : "",
            BuildToolFamily = "run_build_script",
            TestToolFamily = "",
            ConfigureTargetPath = PickPrimarySignal(configureSignals)?.RelativePath ?? "",
            BuildTargetPath = primarySignal?.RelativePath ?? "",
            Confidence = primarySignal?.IsRootSignal == true ? "medium" : "low",
            DetectionSignals = signals.Select(FormatSignal).ToList()
        };
    }

    private static BuildSystemType DetermineDetectedType(IReadOnlyList<WorkspaceBuildProfileRecord> profiles)
    {
        return profiles.Count switch
        {
            0 => BuildSystemType.Unknown,
            1 => profiles[0].BuildSystemType,
            _ => BuildSystemType.Mixed
        };
    }

    private static string DetermineDetectionConfidence(
        IReadOnlyList<WorkspaceBuildProfileRecord> profiles,
        WorkspaceBuildProfileRecord? preferredProfile)
    {
        if (profiles.Count == 0)
            return "none";

        if (profiles.Count == 1)
            return profiles[0].Confidence;

        return preferredProfile?.Confidence is "high"
            ? "medium"
            : "low";
    }

    private static string BuildSummary(
        IReadOnlyList<WorkspaceBuildProfileRecord> profiles,
        WorkspaceBuildProfileRecord? preferredProfile)
    {
        if (profiles.Count == 0)
            return "No supported build system was detected in the current workspace.";

        if (profiles.Count == 1)
            return $"Detected {profiles[0].BuildSystemType} build profile.";

        return preferredProfile is null
            ? $"Detected {profiles.Count} build profiles with no single preferred profile."
            : $"Detected mixed build profiles. Preferred profile: {preferredProfile.BuildSystemType}.";
    }

    private static WorkspaceBuildProfileRecord? SelectPreferredProfile(IReadOnlyList<WorkspaceBuildProfileRecord> profiles)
    {
        return profiles
            .OrderByDescending(ScoreProfile)
            .ThenBy(profile => profile.PrimaryTargetPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static int ScoreProfile(WorkspaceBuildProfileRecord profile)
    {
        var baseScore = profile.BuildSystemType switch
        {
            BuildSystemType.CMake => 120,
            BuildSystemType.Dotnet => 110,
            BuildSystemType.Ninja => 100,
            BuildSystemType.Make => 95,
            BuildSystemType.Script => 70,
            _ => 0
        };

        if (string.Equals(profile.Confidence, "high", StringComparison.OrdinalIgnoreCase))
            baseScore += 20;
        else if (string.Equals(profile.Confidence, "medium", StringComparison.OrdinalIgnoreCase))
            baseScore += 10;

        var depth = ComputeDepth(profile.PrimaryTargetPath);
        return baseScore - (depth * 3);
    }

    private static bool AllowsExplicitTargetOverride(BuildSystemType buildSystemType)
    {
        return buildSystemType is BuildSystemType.CMake
            or BuildSystemType.Make
            or BuildSystemType.Ninja
            or BuildSystemType.Script;
    }

    private static bool TryCreateSignal(string workspaceRoot, string fullPath, out BuildDetectionSignalRecord signal)
    {
        var relativePath = NormalizePath(Path.GetRelativePath(workspaceRoot, fullPath));
        var fileName = Path.GetFileName(fullPath);
        var isRootSignal = ComputeDepth(relativePath) == 0;

        if (string.Equals(fileName, "CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
        {
            signal = CreateSignal(BuildSystemType.CMake, relativePath, "cmake_lists", "Found CMakeLists.txt", isRootSignal);
            return true;
        }

        if (string.Equals(fileName, "CMakeCache.txt", StringComparison.OrdinalIgnoreCase))
        {
            signal = CreateSignal(BuildSystemType.CMake, relativePath, "cmake_cache", "Found CMakeCache.txt", isRootSignal);
            return true;
        }

        if (string.Equals(fileName, "Makefile", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "makefile", StringComparison.OrdinalIgnoreCase))
        {
            signal = CreateSignal(BuildSystemType.Make, relativePath, "makefile", "Found Makefile", isRootSignal);
            return true;
        }

        if (string.Equals(fileName, "build.ninja", StringComparison.OrdinalIgnoreCase))
        {
            signal = CreateSignal(BuildSystemType.Ninja, relativePath, "ninja_file", "Found build.ninja", isRootSignal);
            return true;
        }

        if (string.Equals(fileName, "Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            signal = CreateSignal(BuildSystemType.Dotnet, relativePath, "dotnet_file", $"Found {fileName}", isRootSignal);
            return true;
        }

        if (IsBuildScript(fileName))
        {
            signal = CreateSignal(BuildSystemType.Script, relativePath, "build_script", $"Found repo-local build script {fileName}", isRootSignal);
            return true;
        }

        signal = new BuildDetectionSignalRecord();
        return false;
    }

    private static BuildDetectionSignalRecord CreateSignal(
        BuildSystemType buildSystemType,
        string relativePath,
        string signalType,
        string description,
        bool isRootSignal)
    {
        return new BuildDetectionSignalRecord
        {
            BuildSystemType = buildSystemType,
            RelativePath = relativePath,
            SignalType = signalType,
            Description = description,
            Depth = ComputeDepth(relativePath),
            IsRootSignal = isRootSignal
        };
    }

    private static BuildDetectionSignalRecord? PickPrimarySignal(IReadOnlyList<BuildDetectionSignalRecord> signals)
    {
        return signals
            .OrderBy(signal => signal.Depth)
            .ThenBy(signal => signal.RelativePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string FormatSignal(BuildDetectionSignalRecord signal)
    {
        return $"{signal.RelativePath} [{signal.SignalType}]";
    }

    private static HashSet<string> GetGeneratedCMakeBuildDirectories(IReadOnlyList<BuildDetectionSignalRecord> cmakeSignals)
    {
        return cmakeSignals
            .Where(signal => string.Equals(signal.SignalType, "cmake_cache", StringComparison.OrdinalIgnoreCase))
            .Select(signal => NormalizeDirectory(signal.RelativePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsGeneratedCMakeBuildSignal(BuildDetectionSignalRecord signal, HashSet<string> cmakeBuildDirectories)
    {
        if (cmakeBuildDirectories.Count == 0)
            return false;

        var signalDirectory = NormalizeDirectory(signal.RelativePath);
        return cmakeBuildDirectories.Contains(signalDirectory);
    }

    private static int ComputeDepth(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".")
            return 0;

        return relativePath.Count(ch => ch == '/');
    }

    private static string NormalizeDirectory(string? relativePath)
    {
        var normalized = NormalizePath(Path.GetDirectoryName((relativePath ?? "").Replace('/', Path.DirectorySeparatorChar)) ?? "");
        return string.IsNullOrWhiteSpace(normalized) ? "." : normalized;
    }

    private static string NormalizePath(string path)
    {
        return (path ?? "").Replace('\\', '/');
    }

    private static bool IsBuildScript(string fileName)
    {
        return string.Equals(fileName, "build.sh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "build.bat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "build.cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "build.ps1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "configure.sh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "configure.bat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "configure.cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "configure.ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureWorkspaceExists(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace not found: {workspaceRoot}");
    }

    private static bool IsIgnoredDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, ".ram", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase);
    }
}
