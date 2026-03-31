namespace RAM.Models;

public sealed class WorkspaceProjectRecord
{
    public string ProjectKey { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string ProjectDirectory { get; set; } = "";
    public string Sdk { get; set; } = "";
    public List<string> TargetFrameworks { get; set; } = [];
    public string OutputType { get; set; } = "";
    public bool IsTestProject { get; set; }
    public List<string> SolutionPaths { get; set; } = [];
    public List<string> OwnedFilePaths { get; set; } = [];
    public List<string> SourceFilePaths { get; set; } = [];
    public List<string> XamlFilePaths { get; set; } = [];
    public List<string> ConfigFilePaths { get; set; } = [];
    public List<string> TestedProjectPaths { get; set; } = [];
    public List<WorkspaceReferenceRecord> PackageReferences { get; set; } = [];
    public List<WorkspaceReferenceRecord> ProjectReferences { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
}
