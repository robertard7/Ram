using RAM.Models;

namespace RAM.Services;

public sealed class AgentCallContext
{
    public string WorkspaceRoot { get; set; } = "";
    public string WorkflowType { get; set; } = "";
    public ResponseMode ResponseMode { get; set; } = ResponseMode.None;
    public bool DeterministicFallbackAvailable { get; set; }
    public bool HasCompleteInputs { get; set; }
    public bool NativeManualOnlyState { get; set; }
    public bool ModelSummaryRequested { get; set; }
    public int CandidateCount { get; set; }
}
