namespace RAM.Models;

public sealed class WorkspaceReferenceRecord
{
    public string ReferenceKey { get; set; } = "";
    public string ReferenceKind { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Include { get; set; } = "";
    public string Version { get; set; } = "";
    public List<string> Evidence { get; set; } = [];
}
