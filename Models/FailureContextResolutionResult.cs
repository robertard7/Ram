namespace RAM.Models;

public sealed class FailureContextResolutionResult
{
    public bool Success { get; set; }
    public bool HasOpenablePath { get; set; }
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public RepairContextRecord? RepairContext { get; set; }
    public RepairContextItem? Item { get; set; }
}
