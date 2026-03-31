namespace RAM.Models;

public sealed class FailurePathPromptStep
{
    public string Prompt { get; set; } = "";
    public string RequiredState { get; set; } = "";
    public List<string> ExpectedArtifactTypesAfterStep { get; set; } = [];
}
