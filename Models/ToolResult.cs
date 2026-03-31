namespace RAM.Models;

public sealed class ToolResult
{
    public string ToolName { get; set; } = "";
    public bool Success { get; set; }
    public string OutcomeType { get; set; } = "";
    public string Summary { get; set; } = "";
    public string StructuredDataJson { get; set; } = "";
    public string Output { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
}
