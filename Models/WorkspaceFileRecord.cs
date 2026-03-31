namespace RAM.Models;

public sealed class WorkspaceFileRecord
{
    public string FileKey { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ParentDirectory { get; set; } = "";
    public string Extension { get; set; } = "";
    public string FileKind { get; set; } = "";
    public string LanguageHint { get; set; } = "";
    public long SizeBytes { get; set; }
    public string LastWriteUtc { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public string OwningProjectPath { get; set; } = "";
    public FileIdentityRecord Identity { get; set; } = new();
    public List<string> Evidence { get; set; } = [];
}
