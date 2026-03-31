namespace RAM.Models;

public sealed class TaskboardOperatorSummaryPacket
{
    public string SummaryId { get; set; } = "";
    public string TerminalFingerprint { get; set; } = "";
    public string PlanTitle { get; set; } = "";
    public string ActionName { get; set; } = "";
    public string FinalStatus { get; set; } = "";
    public string TerminalCategory { get; set; } = "";
    public string Headline { get; set; } = "";
    public string ProgressText { get; set; } = "";
    public string TerminalWorkText { get; set; } = "";
    public string WhatHappenedFacts { get; set; } = "";
    public string BlockerIdentity { get; set; } = "";
    public string BlockerReason { get; set; } = "";
    public string FailureIdentity { get; set; } = "";
    public string VerificationResult { get; set; } = "";
    public string BaselineAuthority { get; set; } = "";
    public string GuardState { get; set; } = "";
    public string ProtectionSummary { get; set; } = "";
    public string NextDeterministicAction { get; set; } = "";
    public string TerminalNote { get; set; } = "";
    public List<string> EvidenceLines { get; set; } = [];
    public List<string> ArtifactPaths { get; set; } = [];
    public string TraceabilityText { get; set; } = "";
}

public sealed class TaskboardOperatorSummaryRenderResult
{
    public string RenderedText { get; set; } = "";
    public string ModeLabel { get; set; } = "";
    public bool UsedFallback { get; set; }
    public bool ModelAttempted { get; set; }
    public string FallbackReason { get; set; } = "";
}
