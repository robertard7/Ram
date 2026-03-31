namespace RAM.Models;

public sealed class RepairContextRecord
{
    public string ToolName { get; set; } = "";
    public string OutcomeType { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Summary { get; set; } = "";
    public string FailureFamily { get; set; } = "";
    public string NormalizedErrorCode { get; set; } = "";
    public string NormalizedFailureSummary { get; set; } = "";
    public string NormalizedSourcePath { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public List<RepairContextItem> Items { get; set; } = [];
}
