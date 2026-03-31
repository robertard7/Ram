namespace RAM.Models;

public sealed class IntentDraft
{
    public string Title { get; set; } = "";
    public string Objective { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string TargetStack { get; set; } = "";
    public string ImplementationDirection { get; set; } = "";
    public List<string> OpenQuestions { get; set; } = [];
}
