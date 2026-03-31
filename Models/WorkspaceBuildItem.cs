namespace RAM.Models;

public sealed class WorkspaceBuildItem
{
    public string RelativePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ParentDirectory { get; set; } = "";
    public string ItemType { get; set; } = "";
    public string LanguageHint { get; set; } = "";
    public bool LikelyTestProject { get; set; }
    public FileIdentityRecord Identity { get; set; } = new();
}
