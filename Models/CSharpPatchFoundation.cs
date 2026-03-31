namespace RAM.Models;

public sealed class CSharpModificationSurfaceRecord
{
    public string RelativePath { get; set; } = "";
    public string SurfaceRole { get; set; } = "";
    public string InclusionReason { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string FileKind { get; set; } = "";
    public string LogicalRole { get; set; } = "";
    public int RetrievalChunkCount { get; set; }
    public List<string> RelatedSymbols { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
}

public sealed class CSharpPatchScopeDecisionRecord
{
    public bool IsApplicable { get; set; }
    public bool ScopeApproved { get; set; }
    public string ReasonCode { get; set; } = "";
    public string Summary { get; set; } = "";
    public string AllowedEditScope { get; set; } = "";
    public List<string> AllowedTargetPaths { get; set; } = [];
    public List<string> BlockedTargetPaths { get; set; } = [];
}

public sealed class CSharpPatchWorkContractRecord
{
    public string ContractId { get; set; } = "";
    public string ContractVersion { get; set; } = "csharp_patch_contract.v1";
    public string WorkspaceRoot { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string ModificationIntent { get; set; } = "";
    public string TargetSurfaceType { get; set; } = "";
    public string TargetProject { get; set; } = "";
    public string MutationFamily { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string AllowedEditScope { get; set; } = "";
    public string EditScope { get; set; } = "";
    public bool ScopeApproved { get; set; }
    public string ScopeReasonCode { get; set; } = "";
    public string ScopeSummary { get; set; } = "";
    public string ValidationMode { get; set; } = "mandatory_post_mutation_verification";
    public List<string> ValidationRequirements { get; set; } = [];
    public List<string> RerunRequirements { get; set; } = [];
    public List<string> VerificationRequirements { get; set; } = [];
    public string WarningPolicyMode { get; set; } = "track_only";
    public string TargetSolutionPath { get; set; } = "";
    public string TargetProjectPath { get; set; } = "";
    public List<string> TargetFiles { get; set; } = [];
    public List<string> SupportingFiles { get; set; } = [];
    public List<string> TargetSymbols { get; set; } = [];
    public List<string> RetrievalContextRequirements { get; set; } = [];
    public string FollowThroughMode { get; set; } = "";
    public List<string> CompletionContract { get; set; } = [];
    public List<string> PreserveConstraints { get; set; } = [];
    public bool PreviewRequired { get; set; } = true;
    public string RepairCause { get; set; } = "";
    public string FeatureName { get; set; } = "";
    public string RegistrationSurface { get; set; } = "";
    public string TestUpdateScope { get; set; } = "";
    public List<string> NamespaceConstraints { get; set; } = [];
    public List<string> DependencyUpdateRequirements { get; set; } = [];
    public string RetrievalReadinessStatus { get; set; } = "";
    public string WorkspaceTruthFingerprint { get; set; } = "";
    public string IntentResolutionVersion { get; set; } = "";
    public List<string> IntentClassificationReasons { get; set; } = [];
    public string EditSurfacePlannerVersion { get; set; } = "";
    public List<string> VerificationSurfaces { get; set; } = [];
    public List<string> OutOfScopeSurfaces { get; set; } = [];
    public List<string> PlanningReasons { get; set; } = [];
    public List<CSharpModificationSurfaceRecord> EditSurfaceFiles { get; set; } = [];
    public List<string> AllowedExtensions { get; set; } = [];
    public string SourceFailureKind { get; set; } = "";
    public string SourceProposalId { get; set; } = "";
    public long SourceArtifactId { get; set; }
    public string SourceArtifactType { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string RetrievalBackend { get; set; } = "";
    public string RetrievalEmbedderModel { get; set; } = "";
    public string RetrievalQueryKind { get; set; } = "";
    public int RetrievalHitCount { get; set; }
    public List<string> RetrievalSourceKinds { get; set; } = [];
    public string RetrievalQueryArtifactRelativePath { get; set; } = "";
    public string RetrievalResultArtifactRelativePath { get; set; } = "";
    public string RetrievalContextPacketArtifactRelativePath { get; set; } = "";
    public string RetrievalIndexBatchArtifactRelativePath { get; set; } = "";
    public List<string> RelatedArtifactPaths { get; set; } = [];
    public List<long> RelatedArtifactIds { get; set; } = [];
}

public sealed class CSharpPatchPlannedEditRecord
{
    public string FilePath { get; set; } = "";
    public string DraftKind { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public bool CanApplyLocally { get; set; }
    public string IntentSummary { get; set; } = "";
}

public sealed class CSharpPatchPlanRecord
{
    public string PlanId { get; set; } = "";
    public string PlanVersion { get; set; } = "csharp_patch_plan.v1";
    public string ContractId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string ModificationIntent { get; set; } = "";
    public string TargetSurfaceType { get; set; } = "";
    public string TargetProject { get; set; } = "";
    public string MutationFamily { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string AllowedEditScope { get; set; } = "";
    public string EditScope { get; set; } = "";
    public string WarningPolicyMode { get; set; } = "track_only";
    public string TargetSolutionPath { get; set; } = "";
    public string TargetProjectPath { get; set; } = "";
    public List<string> TargetFiles { get; set; } = [];
    public List<string> SupportingFiles { get; set; } = [];
    public List<string> TargetSymbols { get; set; } = [];
    public string FollowThroughMode { get; set; } = "";
    public List<string> CompletionContract { get; set; } = [];
    public List<string> PreserveConstraints { get; set; } = [];
    public List<string> VerificationRequirements { get; set; } = [];
    public bool PreviewRequired { get; set; } = true;
    public string RepairCause { get; set; } = "";
    public string FeatureName { get; set; } = "";
    public string RegistrationSurface { get; set; } = "";
    public string TestUpdateScope { get; set; } = "";
    public List<string> NamespaceConstraints { get; set; } = [];
    public List<string> DependencyUpdateRequirements { get; set; } = [];
    public string RetrievalReadinessStatus { get; set; } = "";
    public string WorkspaceTruthFingerprint { get; set; } = "";
    public string IntentResolutionVersion { get; set; } = "";
    public List<string> IntentClassificationReasons { get; set; } = [];
    public string EditSurfacePlannerVersion { get; set; } = "";
    public List<string> VerificationSurfaces { get; set; } = [];
    public List<string> OutOfScopeSurfaces { get; set; } = [];
    public List<string> PlanningReasons { get; set; } = [];
    public List<CSharpModificationSurfaceRecord> EditSurfaceFiles { get; set; } = [];
    public List<CSharpPatchPlannedEditRecord> PlannedEdits { get; set; } = [];
    public List<string> ValidationSteps { get; set; } = [];
    public List<string> RerunRequirements { get; set; } = [];
    public string SourceProposalId { get; set; } = "";
    public string SourcePatchDraftId { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string RetrievalBackend { get; set; } = "";
    public string RetrievalEmbedderModel { get; set; } = "";
    public string RetrievalQueryKind { get; set; } = "";
    public int RetrievalHitCount { get; set; }
    public List<string> RetrievalSourceKinds { get; set; } = [];
    public string RetrievalQueryArtifactRelativePath { get; set; } = "";
    public string RetrievalResultArtifactRelativePath { get; set; } = "";
    public string RetrievalContextPacketArtifactRelativePath { get; set; } = "";
    public string RetrievalIndexBatchArtifactRelativePath { get; set; } = "";
    public List<string> RelatedArtifactPaths { get; set; } = [];
    public List<long> RelatedArtifactIds { get; set; } = [];
}
