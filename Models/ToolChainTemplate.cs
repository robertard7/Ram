namespace RAM.Models;

public sealed class ToolChainTemplate
{
    public string Name { get; set; } = "";
    public ToolChainType ChainType { get; set; } = ToolChainType.None;
    public int MaxStepCount { get; set; }
    public bool ModelSummaryAllowed { get; set; }
    public ChainTemplateStepGraph StepGraph { get; set; } = new();
    public HashSet<string> StartingTools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<string>> AllowedTransitions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
