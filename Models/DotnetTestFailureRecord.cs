namespace RAM.Models;

public sealed class DotnetTestFailureRecord
{
    public string TestName { get; set; } = "";
    public string Message { get; set; } = "";
    public string StackTraceExcerpt { get; set; } = "";
    public string SourceFilePath { get; set; } = "";
    public int SourceLine { get; set; }
    public int SourceColumn { get; set; }
    public string ResolvedSourcePath { get; set; } = "";
    public string SourceConfidence { get; set; } = "";
    public string AmbiguityDetails { get; set; } = "";
    public List<string> CandidatePaths { get; set; } = [];
}
