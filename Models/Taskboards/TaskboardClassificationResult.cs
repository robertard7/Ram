namespace RAM.Models;

public sealed class TaskboardClassificationResult
{
    public TaskboardDocumentType DocumentType { get; set; } = TaskboardDocumentType.Unknown;
    public TaskboardTitlePatternKind TitlePatternKind { get; set; } = TaskboardTitlePatternKind.Unknown;
    public bool PreferredTitlePatternMatch { get; set; }
    public bool AcceptedAsTaskboardCandidate { get; set; }
    public TaskboardClassificationConfidence Confidence { get; set; } = TaskboardClassificationConfidence.Low;
    public List<string> MatchedSignals { get; set; } = [];
    public List<string> MissingExpectedSignals { get; set; } = [];
    public string Reason { get; set; } = "";

    public bool IsSupportedTaskboard =>
        DocumentType is TaskboardDocumentType.CodexTaskboard or TaskboardDocumentType.TaskboardCandidate;
}
