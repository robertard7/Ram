namespace RAM.Models;

public enum TaskboardWorkFamilyResolutionSource
{
    Unknown,
    OperationKind,
    PhraseFamily,
    TemplateId,
    StackFallback
}

public sealed class TaskboardWorkFamilyResolution
{
    public string FamilyId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public TaskboardWorkFamilyResolutionSource Source { get; set; } = TaskboardWorkFamilyResolutionSource.Unknown;
    public string StackFamily { get; set; } = "";
    public string PhraseFamily { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> CandidateFamilies { get; set; } = [];
}
