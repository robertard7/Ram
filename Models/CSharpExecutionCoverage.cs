namespace RAM.Models;

public enum CSharpExecutionCoverageStatus
{
    Unknown,
    FullyWired,
    PartiallyWired,
    VocabularyOnly,
    Unsupported
}

public enum CSharpExecutionShapeKind
{
    Unknown,
    TruthOnly,
    OneStepByDesign,
    MultiStep
}

public sealed class CSharpExecutionCoverageRecord
{
    public string CoverageId { get; set; } = "";
    public string CoverageKind { get; set; } = "";
    public string ToolId { get; set; } = "";
    public string ChainTemplateId { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string PhraseFamily { get; set; } = "";
    public string LaneFamily { get; set; } = "";
    public List<string> ReachablePhases { get; set; } = [];
    public bool AutoRunReachable { get; set; }
    public bool MinimumCompleteSetMember { get; set; }
    public CSharpExecutionCoverageStatus Status { get; set; } = CSharpExecutionCoverageStatus.Unknown;
    public CSharpExecutionShapeKind ExecutionShape { get; set; } = CSharpExecutionShapeKind.Unknown;
    public bool IsRunnable { get; set; }
    public bool RuntimeControllerWired { get; set; }
    public int IntendedStepCount { get; set; }
    public List<string> IntendedStepToolIds { get; set; } = [];
    public List<string> MissingPieces { get; set; } = [];
    public string Summary { get; set; } = "";
}

public sealed class CSharpExecutionCoverageAudit
{
    public string AuditId { get; set; } = "";
    public bool MinimumCompleteSetReady { get; set; }
    public int FullyWiredCount { get; set; }
    public int FullyWiredMultiStepCount { get; set; }
    public int OneStepByDesignCount { get; set; }
    public int PartiallyWiredCount { get; set; }
    public int VocabularyOnlyCount { get; set; }
    public int UnsupportedCount { get; set; }
    public string Summary { get; set; } = "";
    public List<CSharpExecutionCoverageRecord> Records { get; set; } = [];
}

public sealed class CSharpExecutionCoverageEvaluation
{
    public bool Relevant { get; set; }
    public bool IsRunnable { get; set; }
    public string CoverageId { get; set; } = "";
    public CSharpExecutionCoverageStatus Status { get; set; } = CSharpExecutionCoverageStatus.Unknown;
    public string ReasonCode { get; set; } = "";
    public string Summary { get; set; } = "";
}
