namespace RAM.Models;

public enum RamTrainingDatasetTrack
{
    Unknown,
    Intake,
    Coder
}

public sealed class RamTrainingLineageRecord
{
    public string WorkspaceRoot { get; set; } = "";
    public string RunStateId { get; set; } = "";
    public string PlanImportId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public int SourcePriority { get; set; }
    public int ExtractionConfidence { get; set; }
    public long SourceArtifactId { get; set; }
    public string SourceArtifactType { get; set; } = "";
    public string SourceArtifactRelativePath { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class RamTrainingSourceSystemRecord
{
    public string SourceId { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public string LocationHint { get; set; } = "";
    public bool Present { get; set; }
    public int RecordCount { get; set; }
    public string PreferredUse { get; set; } = "";
    public string Summary { get; set; } = "";
}

public sealed class RamTrainingSourceTruthRuleRecord
{
    public string DatasetFamily { get; set; } = "";
    public string PreferredSourceKind { get; set; } = "";
    public List<string> FallbackSourceKinds { get; set; } = [];
    public string RuleSummary { get; set; } = "";
}

public sealed class RamTrainingDatasetContractRecord
{
    public RamTrainingDatasetTrack Track { get; set; } = RamTrainingDatasetTrack.Unknown;
    public string DatasetFamily { get; set; } = "";
    public string FileName { get; set; } = "";
    public string InstructionTemplate { get; set; } = "";
    public List<string> RequiredFields { get; set; } = [];
    public List<string> PreferredSourceKinds { get; set; } = [];
    public string CanonicalAnswerShape { get; set; } = "";
    public string ValidationSummary { get; set; } = "";
}

public sealed class RamTrainingSourceInventoryRecord
{
    public string InventoryId { get; set; } = "";
    public string InventoryVersion { get; set; } = "ram_training_source_inventory.v1";
    public string WorkspaceRoot { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public List<string> SourceWorkspaceRoots { get; set; } = [];
    public List<RamTrainingSourceSystemRecord> SourceSystems { get; set; } = [];
    public List<RamTrainingSourceTruthRuleRecord> SourceTruthRules { get; set; } = [];
    public List<RamTrainingDatasetContractRecord> DatasetContracts { get; set; } = [];
}

public sealed class RamTrainingIntermediateRecord
{
    public string RecordId { get; set; } = "";
    public string RecordVersion { get; set; } = "ram_training_intermediate.v1";
    public RamTrainingDatasetTrack TrackHint { get; set; } = RamTrainingDatasetTrack.Unknown;
    public string DatasetFamilyHint { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public string RawInput { get; set; } = "";
    public string RawContext { get; set; } = "";
    public string RawResult { get; set; } = "";
    public string CanonicalLabel { get; set; } = "";
    public List<string> ReasonCodes { get; set; } = [];
    public List<string> QualitySignals { get; set; } = [];
    public RamTrainingLineageRecord Lineage { get; set; } = new();
}

public sealed class RamTrainingExampleRecord
{
    public string ExampleId { get; set; } = "";
    public string ExampleVersion { get; set; } = "ram_training_example.v1";
    public RamTrainingDatasetTrack Track { get; set; } = RamTrainingDatasetTrack.Unknown;
    public string DatasetFamily { get; set; } = "";
    public string Instruction { get; set; } = "";
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int QualityScore { get; set; }
    public string ValidationStatus { get; set; } = "";
    public List<string> ReasonCodes { get; set; } = [];
    public List<string> QualitySignals { get; set; } = [];
    public RamTrainingLineageRecord Lineage { get; set; } = new();
}

public sealed class RamTrainingReviewRecord
{
    public string ReviewId { get; set; } = "";
    public string Bucket { get; set; } = "";
    public RamTrainingDatasetTrack Track { get; set; } = RamTrainingDatasetTrack.Unknown;
    public string DatasetFamily { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int QualityScore { get; set; }
    public string ValidationStatus { get; set; } = "";
    public List<string> ReasonCodes { get; set; } = [];
    public List<string> QualitySignals { get; set; } = [];
    public RamTrainingLineageRecord Lineage { get; set; } = new();
}

public sealed class RamTrainingValidationReport
{
    public string ReportId { get; set; } = "";
    public string ReportVersion { get; set; } = "ram_training_validation_report.v1";
    public string WorkspaceRoot { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public Dictionary<string, int> IntermediateCountsBySourceKind { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> FinalCountsByDatasetFamily { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ReviewCountsByBucket { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int DuplicateRejectCount { get; set; }
    public int ContradictionRejectCount { get; set; }
    public int AmbiguityRejectCount { get; set; }
    public int LowConfidenceRejectCount { get; set; }
    public int IncompleteLineageRejectCount { get; set; }
    public int ShallowCoderRejectCount { get; set; }
    public int BlockedButUsefulReviewCount { get; set; }
    public int AcceptedHighQualityCount { get; set; }
    public int AcceptedTotalCount { get; set; }
    public List<RamTrainingDatasetFamilyStatusRecord> DatasetFamilyStatuses { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

public sealed class RamTrainingDatasetFamilyStatusRecord
{
    public RamTrainingDatasetTrack Track { get; set; } = RamTrainingDatasetTrack.Unknown;
    public string DatasetFamily { get; set; } = "";
    public int CandidateCount { get; set; }
    public int AcceptedCount { get; set; }
    public int ReviewCount { get; set; }
    public Dictionary<string, int> ReviewCountsByBucket { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> RejectCountsByReason { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Notes { get; set; } = [];
}

public sealed class RamTrainingExportBundleRecord
{
    public string ExportId { get; set; } = "";
    public string ExportVersion { get; set; } = "ram_training_export_bundle.v1";
    public string WorkspaceRoot { get; set; } = "";
    public string OutputRoot { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public List<string> SourceWorkspaceRoots { get; set; } = [];
    public string SourceInventoryPath { get; set; } = "";
    public string DatasetContractsPath { get; set; } = "";
    public string IntakeIntermediatePath { get; set; } = "";
    public string CoderIntermediatePath { get; set; } = "";
    public Dictionary<string, string> FinalDatasetPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ReviewQueuePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ValidationReportPath { get; set; } = "";
    public string ExportBundlePath { get; set; } = "";
}

public sealed class RamTrainingValidationResult
{
    public List<RamTrainingExampleRecord> AcceptedExamples { get; set; } = [];
    public Dictionary<string, List<RamTrainingReviewRecord>> ReviewQueues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public RamTrainingValidationReport Report { get; set; } = new();
}
