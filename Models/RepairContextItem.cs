namespace RAM.Models;

public sealed class RepairContextItem
{
    public string SourceType { get; set; } = "";
    public string Title { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string RawPath { get; set; } = "";
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string Confidence { get; set; } = "";
    public List<string> CandidatePaths { get; set; } = [];
}
