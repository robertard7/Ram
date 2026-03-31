using RAM.Models;

namespace RAM.Services;

public sealed class BuildOutputParsingService
{
    private readonly CMakeOutputParser _cmakeOutputParser = new();
    private readonly DotnetBuildParser _dotnetBuildParser = new();
    private readonly GccBuildParser _gccBuildParser = new();
    private readonly MakeNinjaOutputParser _makeNinjaOutputParser = new();

    public DotnetBuildParseResult ParseBuildOutput(
        string toolName,
        string workspaceRoot,
        string stdout,
        string stderr)
    {
        if (string.Equals(toolName, "dotnet_build", StringComparison.OrdinalIgnoreCase))
            return _dotnetBuildParser.Parse(workspaceRoot, stdout, stderr);

        var combined = CombineOutput(stdout, stderr);
        var locations = new List<DotnetBuildErrorRecord>();

        locations.AddRange(_gccBuildParser.Parse(workspaceRoot, combined));
        locations.AddRange(_cmakeOutputParser.Parse(workspaceRoot, combined));
        locations.AddRange(_makeNinjaOutputParser.Parse(combined));

        var orderedLocations = locations
            .OrderBy(location => location.Order)
            .ThenBy(location => location.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(location => location.RawPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var errorCount = orderedLocations.Count(location => string.Equals(location.Severity, "error", StringComparison.OrdinalIgnoreCase));
        var warningCount = orderedLocations.Count(location => string.Equals(location.Severity, "warning", StringComparison.OrdinalIgnoreCase));
        var success = errorCount == 0 && !LooksLikeBuildFailure(combined);

        return new DotnetBuildParseResult
        {
            Success = success,
            ErrorCount = errorCount,
            WarningCount = warningCount,
            BuildLocations = orderedLocations,
            TopErrors = orderedLocations
                .Where(location => string.Equals(location.Severity, "error", StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .ToList(),
            Summary = success
                ? $"{toolName} succeeded with {warningCount} warning(s) and 0 error(s)."
                : $"{toolName} failed with {Math.Max(errorCount, 1)} error(s) and {warningCount} warning(s)."
        };
    }

    private static bool LooksLikeBuildFailure(string combined)
    {
        if (string.IsNullOrWhiteSpace(combined))
            return false;

        return combined.Contains(" error:", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("fatal error", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("undefined reference", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("CMake Error", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("build stopped", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("recipe for target", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("***", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineOutput(string stdout, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return stdout ?? "";

        if (string.IsNullOrWhiteSpace(stdout))
            return stderr ?? "";

        return stdout + Environment.NewLine + stderr;
    }
}
