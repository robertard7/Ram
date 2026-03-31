using System.IO;
using RAM.Models;
using RAM.Tools;

namespace RAM.Services;

public sealed class RepairPlanInputBuilder
{
    private readonly ArtifactClassificationService _artifactClassificationService;
    private readonly ProjectGraphRepairInferenceService _projectGraphRepairInferenceService = new();
    private readonly RepairContextService _repairContextService;
    private readonly ReadFileChunkTool _readFileChunkTool = new();

    public RepairPlanInputBuilder(
        RepairContextService repairContextService,
        ArtifactClassificationService artifactClassificationService)
    {
        _repairContextService = repairContextService;
        _artifactClassificationService = artifactClassificationService;
    }

    public RepairPlanInput Build(
        string workspaceRoot,
        RamDbService ramDbService,
        string scope,
        string explicitPath,
        string runStateId = "")
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return Failure("plan_repair failed: workspace is required.");

        var latestRepairArtifact = ramDbService.LoadLatestArtifactByType(workspaceRoot, "repair_context");
        var executionState = ramDbService.LoadExecutionState(workspaceRoot);
        var recentArtifacts = ramDbService.LoadLatestArtifacts(workspaceRoot, 25);
        var hasPersistedRepairChain = recentArtifacts.Any(artifact =>
            _artifactClassificationService.IsRepairArtifact(artifact)
            || _artifactClassificationService.IsPatchArtifact(artifact)
            || _artifactClassificationService.IsVerificationArtifact(artifact));
        var resolution = _repairContextService.ResolveContext(
            workspaceRoot,
            ramDbService,
            scope);

        if (!resolution.Success && string.IsNullOrWhiteSpace(explicitPath))
            return Failure("plan_repair failed: no recorded build or test failure is available for this workspace."
                + Environment.NewLine
                + "Run dotnet build or dotnet test first, or select a failure-related artifact.");

        var failureKind = FirstNonEmpty(
            resolution.RepairContext?.OutcomeType,
            executionState.LastFailureOutcomeType,
            "unknown");

        if (!_artifactClassificationService.IsRepairEligibleFailureKind(failureKind) && !hasPersistedRepairChain)
        {
            return Failure("plan_repair failed: no recorded build or test failure is available for this workspace."
                + Environment.NewLine
                + "Run dotnet build or dotnet test first, or select a failure-related artifact.");
        }

        var input = new RepairPlanInput
        {
            Success = true,
            FailureKind = failureKind,
            ContextSource = resolution.Source,
            SourceArtifactId = latestRepairArtifact?.Id ?? 0,
            SourceArtifactType = latestRepairArtifact?.ArtifactType ?? "",
            TargetProjectPath = NormalizeProjectTarget(FirstNonEmpty(
                resolution.RepairContext?.TargetPath,
                executionState.LastFailureTargetPath)),
            RunStateId = runStateId,
            Message = resolution.Message
        };

        if (!string.IsNullOrWhiteSpace(runStateId))
        {
            input.RecentRunTouchedFilePaths = ramDbService.LoadFileTouchRecordsForRun(workspaceRoot, runStateId, 400)
                .Select(record => NormalizeRelativePath(record.FilePath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var selectedItem = resolution.Item;
        var normalizedExplicitPath = "";
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var explicitRelativePath = NormalizeExplicitPath(workspaceRoot, explicitPath);
            if (string.IsNullOrWhiteSpace(explicitRelativePath))
                return Failure("plan_repair failed: explicit target path must stay inside the active workspace.");

            normalizedExplicitPath = explicitRelativePath;
            input.ExplicitPath = explicitRelativePath;
            input.TargetFilePath = explicitRelativePath;

            if (selectedItem is not null
                && string.Equals(selectedItem.RelativePath, explicitRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                ApplyItem(input, selectedItem);
            }
            else if (selectedItem is not null
                && ShouldPreferSelectedItemOverExplicitTarget(explicitRelativePath, selectedItem))
            {
                ApplyItem(input, selectedItem);
                input.TargetFilePath = selectedItem.RelativePath;
                input.Message = FirstNonEmpty(
                    selectedItem.Message,
                    $"Retargeted repair from `{explicitRelativePath}` to the concrete compiler failure source `{selectedItem.RelativePath}`.");
            }
        }
        else if (selectedItem is not null)
        {
            ApplyItem(input, selectedItem);
        }

        if (string.IsNullOrWhiteSpace(input.Confidence))
            input.Confidence = "none";

        if (selectedItem is not null && selectedItem.CandidatePaths.Count > 1 && string.IsNullOrWhiteSpace(selectedItem.RelativePath))
        {
            input.HasAmbiguity = true;
            input.AmbiguitySummary = string.IsNullOrWhiteSpace(selectedItem.Message)
                ? "Multiple candidate files matched the current failure target."
                : selectedItem.Message;
            input.CandidatePaths = selectedItem.CandidatePaths.Take(5).ToList();
        }

        if (string.IsNullOrWhiteSpace(input.FailureMessage))
            input.FailureMessage = FirstNonEmpty(
                resolution.RepairContext?.NormalizedFailureSummary,
                resolution.RepairContext?.Summary,
                resolution.Message,
                "No stored failure summary is available for the current repair target.");

        if (string.IsNullOrWhiteSpace(input.FailureTitle))
            input.FailureTitle = selectedItem?.Title ?? "Repair target";

        if (string.IsNullOrWhiteSpace(input.FailureCode))
            input.FailureCode = resolution.RepairContext?.NormalizedErrorCode ?? "";

        if (!input.HasAmbiguity
            && IsCircularDependencyFailure(input)
            && (string.IsNullOrWhiteSpace(input.TargetFilePath)
                || input.TargetFilePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || input.TargetFilePath.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                || input.TargetFilePath.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)))
        {
            ApplyCircularDependencyInference(workspaceRoot, input);
        }

        ApplyRepairTargetingClassification(input, normalizedExplicitPath, selectedItem);

        if (!string.IsNullOrWhiteSpace(input.TargetFilePath))
            input.FileExcerpt = BuildExcerpt(workspaceRoot, input.TargetFilePath, input.TargetLineNumber);

        if (string.IsNullOrWhiteSpace(input.TargetFilePath) && !input.HasAmbiguity)
        {
            input.Success = false;
            input.Message = "plan_repair failed: RAM does not have a single file target for the current failure context.";
        }

        return input;
    }

    private static void ApplyItem(RepairPlanInput input, RepairContextItem item)
    {
        input.TargetFilePath = string.IsNullOrWhiteSpace(input.TargetFilePath)
            ? item.RelativePath
            : input.TargetFilePath;
        input.TargetLineNumber = item.LineNumber;
        input.TargetColumnNumber = item.ColumnNumber;
        input.FailureCode = item.Code;
        input.FailureTitle = item.Title;
        input.FailureMessage = item.Message;
        input.Confidence = item.Confidence;
        input.CandidatePaths = item.CandidatePaths.Take(5).ToList();
    }

    private void ApplyCircularDependencyInference(string workspaceRoot, RepairPlanInput input)
    {
        var inference = _projectGraphRepairInferenceService.InferCircularDependencyTarget(workspaceRoot, input.TargetProjectPath);
        if (inference.Success && inference.Issue is not null)
        {
            input.TargetFilePath = inference.Issue.RelativeProjectPath;
            input.TargetLineNumber = inference.Issue.LineNumber;
            input.TargetColumnNumber = 0;
            input.Confidence = FirstNonEmpty(inference.Issue.Confidence, input.Confidence, "medium");
            input.FailureTitle = FirstNonEmpty(input.FailureTitle, inference.Issue.Summary);
            input.Message = FirstNonEmpty(inference.Summary, input.Message);
            return;
        }

        if (inference.Ambiguous && inference.Candidates.Count > 0)
        {
            input.HasAmbiguity = true;
            input.AmbiguitySummary = FirstNonEmpty(
                inference.Summary,
                "Multiple workspace project files could explain the recorded circular dependency.");
            input.CandidatePaths = inference.Candidates
                .Select(candidate => candidate.RelativeProjectPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();
        }
    }

    private string BuildExcerpt(string workspaceRoot, string relativePath, int lineNumber)
    {
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(fullPath))
                return "";

            var startLine = lineNumber > 0 ? Math.Max(lineNumber - 10, 1) : 1;
            return _readFileChunkTool.ReadLines(workspaceRoot, fullPath, startLine, 40);
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeExplicitPath(string workspaceRoot, string explicitPath)
    {
        if (string.IsNullOrWhiteSpace(explicitPath))
            return "";

        try
        {
            var fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
            var fullPath = Path.IsPathRooted(explicitPath)
                ? Path.GetFullPath(explicitPath)
                : Path.GetFullPath(Path.Combine(workspaceRoot, explicitPath));
            var workspacePrefix = fullWorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            var isInsideWorkspace = string.Equals(fullPath, fullWorkspaceRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase);

            return isInsideWorkspace
                ? Path.GetRelativePath(fullWorkspaceRoot, fullPath).Replace('\\', '/')
                : "";
        }
        catch
        {
            return "";
        }
    }

    private string NormalizeProjectTarget(string path)
    {
        var normalizedPath = NormalizeRelativePath(path);
        return _artifactClassificationService.IsBuildOrTestTargetPath(normalizedPath)
            ? normalizedPath
            : "";
    }

    private static string NormalizeRelativePath(string path)
    {
        return (path ?? "").Replace('\\', '/');
    }

    private static bool IsCircularDependencyFailure(RepairPlanInput input)
    {
        return string.Equals(input.FailureCode, "MSB4006", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(input.FailureMessage, "MSB4006", "circular dependency", "target dependency graph", "NuGet.targets");
    }

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldPreferSelectedItemOverExplicitTarget(string explicitRelativePath, RepairContextItem selectedItem)
    {
        if (string.IsNullOrWhiteSpace(explicitRelativePath)
            || selectedItem is null
            || string.IsNullOrWhiteSpace(selectedItem.RelativePath))
        {
            return false;
        }

        if (!IsProjectScopedRepairPath(explicitRelativePath))
            return false;

        if (!IsSourceRepairCandidate(selectedItem))
            return false;

        if (string.Equals(selectedItem.RelativePath, explicitRelativePath, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static void ApplyRepairTargetingClassification(
        RepairPlanInput input,
        string explicitRelativePath,
        RepairContextItem? selectedItem)
    {
        var normalizedTarget = NormalizeRelativePath(input.TargetFilePath);
        var normalizedExplicit = NormalizeRelativePath(explicitRelativePath);
        var normalizedSelected = NormalizeRelativePath(selectedItem?.RelativePath ?? "");

        if (!string.IsNullOrWhiteSpace(normalizedExplicit)
            && IsProjectScopedRepairPath(normalizedExplicit)
            && IsSourceRepairPath(normalizedTarget)
            && !string.Equals(normalizedTarget, normalizedExplicit, StringComparison.OrdinalIgnoreCase))
        {
            input.TargetingStrategy = "source_first_retargeted";
            input.TargetingSummary = $"Retargeted repair from `{normalizedExplicit}` to source failure `{normalizedTarget}` because the recorded compiler failure resolves to a concrete source location.";
            return;
        }

        if (IsSourceRepairPath(normalizedTarget)
            && (IsSourceRepairCandidate(selectedItem)
                || input.TargetLineNumber > 0
                || (input.FailureCode ?? "").StartsWith("CS", StringComparison.OrdinalIgnoreCase)))
        {
            input.TargetingStrategy = "source_first_compiler_failure";
            input.TargetingSummary = string.Equals(normalizedTarget, normalizedSelected, StringComparison.OrdinalIgnoreCase)
                ? $"Planned repair directly against source failure `{normalizedTarget}` because the compiler reported a concrete code location."
                : $"Preferred source repair target `{normalizedTarget}` because the compiler failure is anchored to code rather than project configuration.";
            return;
        }

        if (IsProjectScopedRepairPath(normalizedTarget))
        {
            input.TargetingStrategy = "project_file_first";
            input.TargetingSummary = $"Planned repair against project or build configuration target `{normalizedTarget}` because the failure currently appears project-local.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(normalizedExplicit))
        {
            input.TargetingStrategy = "explicit_target_only";
            input.TargetingSummary = $"Used explicit repair target `{normalizedExplicit}` because no stronger source-first target was resolved.";
            return;
        }

        input.TargetingStrategy = "runtime_selected_target";
        input.TargetingSummary = string.IsNullOrWhiteSpace(normalizedTarget)
            ? "Repair targeting remains unresolved."
            : $"Used runtime-selected repair target `{normalizedTarget}`.";
    }

    private static bool IsSourceRepairCandidate(RepairContextItem? selectedItem)
    {
        if (selectedItem is null || !IsSourceRepairPath(selectedItem.RelativePath))
            return false;

        return selectedItem.LineNumber > 0
            || (selectedItem.Code ?? "").StartsWith("CS", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(
                selectedItem.Message,
                "could not be found",
                "does not exist in the current context",
                "type or namespace name",
                "does not contain a definition",
                "using directive");
    }

    private static bool IsSourceRepairPath(string path)
    {
        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProjectScopedRepairPath(string path)
    {
        return path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);
    }

    private static RepairPlanInput Failure(string message)
    {
        return new RepairPlanInput
        {
            Success = false,
            Message = message,
            FailureKind = "unknown"
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
