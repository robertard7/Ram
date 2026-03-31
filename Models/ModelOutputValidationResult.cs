namespace RAM.Models;

public sealed class ModelOutputValidationResult
{
    public bool IsValid { get; set; }
    public string RejectionReason { get; set; } = "";
    public ToolRequest? ParsedToolRequest { get; set; }
}
