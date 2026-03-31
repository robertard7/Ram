namespace RAM.Models;

public sealed class RamDataCategoryDefinitionRecord
{
    public string CategoryKey { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsDurableTruth { get; set; }
    public bool IsTransientDebugNoise { get; set; }
    public bool IsArchiveOnly { get; set; }
    public string DefaultRetentionClass { get; set; } = "";
    public string ExampleRecordType { get; set; } = "";
}

public sealed class RamRetentionRuleDefinitionRecord
{
    public string CategoryKey { get; set; } = "";
    public string RetentionClass { get; set; } = "";
    public string LifecycleRule { get; set; } = "";
    public bool KeepFullPayload { get; set; }
    public bool EligibleForCompaction { get; set; }
}

public sealed class RamFileTouchRecord
{
    public long Id { get; set; }
    public string WorkspaceRoot { get; set; } = "";
    public string RunStateId { get; set; } = "";
    public string PlanImportId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string WorkItemTitle { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string OperationType { get; set; } = "";
    public string Reason { get; set; } = "";
    public string SourceActionName { get; set; } = "";
    public string ArtifactType { get; set; } = "";
    public bool IsProductiveTouch { get; set; }
    public bool ContentChanged { get; set; }
    public int TouchOrderIndex { get; set; }
    public string CreatedUtc { get; set; } = "";
}

public sealed class RamFileTouchRollupRecord
{
    public string FilePath { get; set; } = "";
    public int TouchCount { get; set; }
    public int RepeatedTouchCount { get; set; }
    public int ProductiveTouchCount { get; set; }
    public int NoOpTouchCount { get; set; }
    public List<string> ReasonCounts { get; set; } = [];
    public List<string> OperationCounts { get; set; } = [];
    public string FirstTouchedUtc { get; set; } = "";
    public string LastTouchedUtc { get; set; } = "";
}
