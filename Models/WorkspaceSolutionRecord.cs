namespace RAM.Models;

public sealed class WorkspaceSolutionRecord
{
    public string SolutionKey { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string SolutionName { get; set; } = "";
    public List<string> MemberProjectPaths { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
}
