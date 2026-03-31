namespace RAM.Models;

public sealed class ResolvedUserIntent
{
    public ToolRequest ToolRequest { get; set; } = new();
    public string ResolutionReason { get; set; } = "";
}
