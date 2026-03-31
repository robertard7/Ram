namespace RAM.Models;

public sealed class DotnetScaffoldTemplateRecord
{
    public string TemplateId { get; set; } = "";
    public string DotnetTemplateName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SupportStatus { get; set; } = "";
    public string DefaultProjectRoot { get; set; } = "src";
    public string DefaultRole { get; set; } = "";
    public string DefaultTargetFramework { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public List<string> DefaultSwitches { get; set; } = [];
    public List<string> CompositionTags { get; set; } = [];
    public string Summary { get; set; } = "";
}

public sealed class DotnetScaffoldSurfaceMatrixRecord
{
    public string MatrixVersion { get; set; } = "dotnet_scaffold_surface.v1";
    public List<DotnetScaffoldTemplateRecord> Templates { get; set; } = [];
}
