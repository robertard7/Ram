namespace RAM.Models;

public sealed class TaskboardIntakeResult
{
    public TaskboardIntakeResultCategory Category { get; set; } = TaskboardIntakeResultCategory.IntakeException;
    public string Message { get; set; } = "";
    public TaskboardClassificationResult Classification { get; set; } = new();
    public TaskboardImportRecord? ImportRecord { get; set; }
    public TaskboardParseResult? ParseResult { get; set; }
    public TaskboardDocument? PlanDocument { get; set; }
    public TaskboardValidationReport? ValidationReport { get; set; }
    public TaskboardActivationResult? AutoActivationResult { get; set; }
}

public sealed class TaskboardActivationResult
{
    public string ActionName { get; set; } = "";
    public bool Success { get; set; }
    public bool StateChanged { get; set; }
    public bool WasSkipped { get; set; }
    public bool ActivePlanChanged { get; set; }
    public string StatusCode { get; set; } = "";
    public string Message { get; set; } = "";
    public TaskboardImportRecord? ImportRecord { get; set; }
}
