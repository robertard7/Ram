namespace RAM.Models;

public enum TaskboardDocumentType
{
    Unknown,
    PlainRequest,
    UnsupportedStructuredDocument,
    TaskboardCandidate,
    CodexTaskboard
}

public enum TaskboardTitlePatternKind
{
    Unknown,
    Preferred,
    Accepted,
    Candidate,
    Other
}

public enum TaskboardClassificationConfidence
{
    Low,
    Medium,
    High
}

public enum TaskboardImportState
{
    Imported,
    Parsed,
    Validated,
    Rejected,
    ReadyForPromotion,
    ActivePlan,
    Archived,
    Deleted
}

public enum TaskboardIntakeResultCategory
{
    AcceptedForClassification,
    UnsupportedDocument,
    TooLarge,
    EmptyInput,
    MalformedInput,
    IntakeException
}

public sealed class TaskboardImportRecord
{
    public string ImportId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Title { get; set; } = "";
    public TaskboardDocumentType DocumentType { get; set; } = TaskboardDocumentType.Unknown;
    public TaskboardTitlePatternKind TitlePatternKind { get; set; } = TaskboardTitlePatternKind.Unknown;
    public bool PreferredTitlePatternMatch { get; set; }
    public bool AcceptedAsTaskboardCandidate { get; set; }
    public TaskboardClassificationConfidence ClassificationConfidence { get; set; } = TaskboardClassificationConfidence.Low;
    public List<string> MatchedSignals { get; set; } = [];
    public List<string> MissingExpectedSignals { get; set; } = [];
    public string ClassificationReason { get; set; } = "";
    public TaskboardImportState State { get; set; } = TaskboardImportState.Imported;
    public TaskboardValidationOutcome ValidationOutcome { get; set; } = TaskboardValidationOutcome.ValidationException;
    public string ValidationSummary { get; set; } = "";
    public int ValidationErrorCount { get; set; }
    public int ValidationWarningCount { get; set; }
    public string ParserVersion { get; set; } = "";
    public string ModelBuilderVersion { get; set; } = "";
    public string ValidatorVersion { get; set; } = "";
    public long RawArtifactId { get; set; }
    public long ParsedArtifactId { get; set; }
    public long PlanArtifactId { get; set; }
    public long ValidationArtifactId { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
    public string ActivatedUtc { get; set; } = "";
}
