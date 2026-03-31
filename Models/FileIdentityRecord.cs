namespace RAM.Models;

public sealed class FileIdentityRecord
{
    public string Path { get; set; } = "";
    public string FileType { get; set; } = "";
    public string Role { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string NamespaceHint { get; set; } = "";
    public string IdentityTrace { get; set; } = "";
}
