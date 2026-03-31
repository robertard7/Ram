namespace RAM.Models;

public sealed class DotnetTestParseResult
{
    public bool Success { get; set; }
    public int TotalTests { get; set; }
    public int PassedCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<DotnetTestFailureRecord> FailingTests { get; set; } = [];
    public string Summary { get; set; } = "";
}
