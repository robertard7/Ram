namespace RAM.Models;

public sealed class TaskboardActionResult
{
    public string ActionName { get; set; } = "";
    public bool Success { get; set; }
    public bool StateChanged { get; set; }
    public bool WasSkipped { get; set; }
    public string StatusCode { get; set; } = "";
    public string Message { get; set; } = "";
    public int AffectedCount { get; set; }
    public TaskboardImportRecord? ImportRecord { get; set; }
}
