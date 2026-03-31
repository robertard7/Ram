namespace RAM.Models;

public sealed class DotnetBuildParseResult
{
    public bool Success { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public List<DotnetBuildErrorRecord> BuildLocations { get; set; } = [];
    public List<DotnetBuildErrorRecord> TopErrors { get; set; } = [];
    public string Summary { get; set; } = "";
    public string NormalizedFailureFamily { get; set; } = "";
    public string NormalizedErrorCode { get; set; } = "";
    public string NormalizedFailureSummary { get; set; } = "";
    public string NormalizedSourcePath { get; set; } = "";
}
