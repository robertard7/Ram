namespace RAM.Models;

public sealed class ToolDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public ToolRiskLevel RiskLevel { get; set; } = ToolRiskLevel.Safe;
    public string ArgumentsDescription { get; set; } = "";
}
