namespace RAM.Models;

public enum TaskboardValidationOutcome
{
    Valid,
    ValidWithWarnings,
    MissingRequiredSection,
    DuplicateBatch,
    UnsupportedStructure,
    TextLimitExceeded,
    UnsafeContentDetected,
    ValidationException
}

public enum TaskboardValidationSeverity
{
    Warning,
    Error
}

public sealed class TaskboardValidationMessage
{
    public TaskboardValidationSeverity Severity { get; set; } = TaskboardValidationSeverity.Warning;
    public string Code { get; set; } = "";
    public string SectionId { get; set; } = "";
    public int LineNumber { get; set; }
    public string OffendingText { get; set; } = "";
    public string LineClassification { get; set; } = "";
    public string ExpectedGrammar { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class TaskboardValidationReport
{
    public string ValidatorVersion { get; set; } = "";
    public string GrammarVersion { get; set; } = "";
    public string CanonicalFormHint { get; set; } = "";
    public List<string> CanonicalExamples { get; set; } = [];
    public TaskboardValidationOutcome Outcome { get; set; } = TaskboardValidationOutcome.ValidationException;
    public List<TaskboardValidationMessage> Errors { get; set; } = [];
    public List<TaskboardValidationMessage> Warnings { get; set; } = [];

    public bool CanPromote => Outcome is TaskboardValidationOutcome.Valid or TaskboardValidationOutcome.ValidWithWarnings;
}
