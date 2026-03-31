using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class RepairContextService
{
    private readonly ArtifactClassificationService _artifactClassificationService;
    private readonly BuildFailureLocationService _buildFailureLocationService = new();
    private readonly TestFailureSourceInferenceService _testFailureSourceInferenceService;

    public RepairContextService(
        WorkspaceBuildIndexService workspaceBuildIndexService,
        ArtifactClassificationService artifactClassificationService)
    {
        _artifactClassificationService = artifactClassificationService;
        _testFailureSourceInferenceService = new TestFailureSourceInferenceService(workspaceBuildIndexService);
    }

    public RepairContextRecord BuildForBuildFailure(string workspaceRoot, string targetPath, DotnetBuildParseResult parseResult, string toolName = "dotnet_build")
    {
        parseResult.TopErrors = _buildFailureLocationService
            .Prioritize(parseResult.BuildLocations, targetPath)
            .Where(location => string.Equals(location.Severity, "error", StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();

        return new RepairContextRecord
        {
            ToolName = string.IsNullOrWhiteSpace(toolName) ? "dotnet_build" : toolName,
            OutcomeType = "build_failure",
            TargetPath = NormalizeRelativePath(targetPath),
            Summary = parseResult.Summary,
            FailureFamily = parseResult.NormalizedFailureFamily,
            NormalizedErrorCode = parseResult.NormalizedErrorCode,
            NormalizedFailureSummary = parseResult.NormalizedFailureSummary,
            NormalizedSourcePath = parseResult.NormalizedSourcePath,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            Items = parseResult.TopErrors
                .Select(location => new RepairContextItem
                {
                    SourceType = "build_error",
                    Title = string.IsNullOrWhiteSpace(location.Code) ? "Build error" : location.Code,
                    RelativePath = location.InsideWorkspace ? location.FilePath : "",
                    RawPath = location.RawPath,
                    LineNumber = location.LineNumber,
                    ColumnNumber = location.ColumnNumber,
                    Code = location.Code,
                    Message = location.Message,
                    Confidence = location.InsideWorkspace ? "high" : "none"
                })
                .ToList()
        };
    }

    public RepairContextRecord BuildForTestFailure(string workspaceRoot, string targetPath, DotnetTestParseResult parseResult)
    {
        _testFailureSourceInferenceService.Enrich(workspaceRoot, parseResult.FailingTests, targetPath);

        return new RepairContextRecord
        {
            ToolName = "dotnet_test",
            OutcomeType = "test_failure",
            TargetPath = NormalizeRelativePath(targetPath),
            Summary = parseResult.Summary,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            Items = parseResult.FailingTests
                .Select(failure => new RepairContextItem
                {
                    SourceType = "test_failure",
                    Title = failure.TestName,
                    RelativePath = failure.ResolvedSourcePath,
                    RawPath = failure.SourceFilePath,
                    LineNumber = failure.SourceLine,
                    ColumnNumber = failure.SourceColumn,
                    Message = failure.Message,
                    Confidence = string.IsNullOrWhiteSpace(failure.SourceConfidence) ? "none" : failure.SourceConfidence,
                    CandidatePaths = failure.CandidatePaths.ToList()
                })
                .ToList()
        };
    }

    public ArtifactRecord SaveRepairContextArtifact(
        string workspaceRoot,
        RamDbService ramDbService,
        RepairContextRecord repairContext)
    {
        var relativePath = BuildRepairContextArtifactPath(repairContext.ToolName, repairContext.TargetPath);
        var existingArtifact = ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();

        artifact.IntentTitle = "";
        artifact.ArtifactType = "repair_context";
        artifact.Title = $"Repair context: {repairContext.ToolName} {DisplayValue(repairContext.TargetPath)}";
        artifact.RelativePath = relativePath;
        artifact.Content = JsonSerializer.Serialize(repairContext, new JsonSerializerOptions { WriteIndented = true });
        artifact.Summary = BuildRepairContextSummary(repairContext);

        if (existingArtifact is null)
            return ramDbService.SaveArtifact(workspaceRoot, artifact);

        ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    public FailureContextResolutionResult ResolveContext(
        string workspaceRoot,
        RamDbService ramDbService,
        string scope)
    {
        var normalizedScope = NormalizeScope(scope);
        var artifact = ramDbService.LoadLatestArtifactByType(workspaceRoot, "repair_context");
        if (TryBuildResultFromArtifact(artifact, normalizedScope, out var artifactResult))
            return artifactResult;

        var state = ramDbService.LoadExecutionState(workspaceRoot);
        var fromState = BuildFromExecutionState(workspaceRoot, state);
        if (fromState is not null && TryBuildResult(fromState, normalizedScope, "execution_state", out var stateResult))
            return stateResult;

        return new FailureContextResolutionResult
        {
            Success = false,
            HasOpenablePath = false,
            Source = "none",
            Message = "open_failure_context failed: no recorded build or test failure is available for this workspace."
                + Environment.NewLine
                + "Run dotnet build or dotnet test first, or select a failure-related artifact."
        };
    }

    private RepairContextRecord? BuildFromExecutionState(string workspaceRoot, WorkspaceExecutionStateRecord state)
    {
        if (string.Equals(state.LastFailureOutcomeType, "build_failure", StringComparison.OrdinalIgnoreCase))
        {
            var parseResult = DeserializeParsedSection<DotnetBuildParseResult>(state.LastFailureDataJson);
            if (parseResult is not null)
                return BuildForBuildFailure(workspaceRoot, state.LastFailureTargetPath, parseResult, state.LastFailureToolName);
        }

        if (string.Equals(state.LastFailureOutcomeType, "test_failure", StringComparison.OrdinalIgnoreCase))
        {
            var parseResult = DeserializeParsedSection<DotnetTestParseResult>(state.LastFailureDataJson);
            if (parseResult is not null)
                return BuildForTestFailure(workspaceRoot, state.LastFailureTargetPath, parseResult);
        }

        return null;
    }

    private static bool TryBuildResultFromArtifact(
        ArtifactRecord? artifact,
        string scope,
        out FailureContextResolutionResult result)
    {
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.Content))
        {
            result = new FailureContextResolutionResult();
            return false;
        }

        try
        {
            var repairContext = JsonSerializer.Deserialize<RepairContextRecord>(artifact.Content);
            if (repairContext is null)
            {
                result = new FailureContextResolutionResult();
                return false;
            }

            return TryBuildResult(repairContext, scope, "repair_context_artifact", out result);
        }
        catch
        {
            result = new FailureContextResolutionResult();
            return false;
        }
    }

    private static bool TryBuildResult(
        RepairContextRecord repairContext,
        string scope,
        string source,
        out FailureContextResolutionResult result)
    {
        var preferredItems = SelectPreferredItems(repairContext, scope);
        var item = preferredItems.FirstOrDefault();
        if (item is null)
        {
            result = new FailureContextResolutionResult();
            return false;
        }

        var hasOpenablePath = !string.IsNullOrWhiteSpace(item.RelativePath);
        result = new FailureContextResolutionResult
        {
            Success = true,
            HasOpenablePath = hasOpenablePath,
            Source = source,
            Message = hasOpenablePath
                ? $"Resolved failure context from {source}: {item.RelativePath}"
                : $"Failure context is available from {source}, but no single workspace file could be opened deterministically.",
            RepairContext = repairContext,
            Item = item
        };

        return true;
    }

    private static IReadOnlyList<RepairContextItem> SelectPreferredItems(RepairContextRecord repairContext, string scope)
    {
        var filtered = repairContext.Items
            .Where(item => ScopeMatches(scope, item))
            .OrderBy(item => string.IsNullOrWhiteSpace(item.RelativePath) ? 1 : 0)
            .ThenBy(item => string.Equals(item.Confidence, "high", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return filtered.Count > 0
            ? filtered
            : repairContext.Items
                .OrderBy(item => string.IsNullOrWhiteSpace(item.RelativePath) ? 1 : 0)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    private static bool ScopeMatches(string scope, RepairContextItem item)
    {
        return scope switch
        {
            "build" => item.SourceType == "build_error",
            "test" => item.SourceType == "test_failure",
            _ => true
        };
    }

    private static string BuildRepairContextArtifactPath(string toolName, string targetPath)
    {
        var slugSource = string.IsNullOrWhiteSpace(targetPath) ? toolName : $"{toolName}-{NormalizeRelativePath(targetPath)}";
        var slug = new string(slugSource
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(slug))
            slug = "workspace";

        return $".ram/repair-context/{slug}.json";
    }

    private static string BuildRepairContextSummary(RepairContextRecord repairContext)
    {
        if (!string.IsNullOrWhiteSpace(repairContext.NormalizedFailureSummary))
        {
            var normalizedTarget = string.IsNullOrWhiteSpace(repairContext.TargetPath)
                ? ""
                : $" target={repairContext.TargetPath}";
            return $"{repairContext.NormalizedFailureSummary}{normalizedTarget}".Trim();
        }

        var firstItem = repairContext.Items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.RelativePath))
            ?? repairContext.Items.FirstOrDefault();

        if (firstItem is null)
            return repairContext.Summary;

        var location = string.IsNullOrWhiteSpace(firstItem.RelativePath)
            ? DisplayValue(firstItem.RawPath)
            : firstItem.RelativePath;
        var lineSuffix = firstItem.LineNumber > 0 ? $":{firstItem.LineNumber}" : "";
        return $"{repairContext.Summary} Focus: {location}{lineSuffix}.";
    }

    private static T? DeserializeParsedSection<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("parsed", out var parsedElement))
                return parsedElement.Deserialize<T>();
        }
        catch
        {
            return default;
        }

        return default;
    }

    private static string NormalizeScope(string scope)
    {
        var normalized = (scope ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "build" or "test" => normalized,
            _ => "auto"
        };
    }

    private static string NormalizeRelativePath(string path)
    {
        return (path ?? "").Replace('\\', '/');
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
