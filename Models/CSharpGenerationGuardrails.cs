namespace RAM.Models;

public enum CSharpGenerationIntent
{
    None,
    ScaffoldFile,
    ImplementBehavior,
    WireRuntimeIntegration,
    VerifyBehavior
}

public enum CSharpGenerationProfile
{
    None,
    ContractGeneration,
    SimpleImplementation,
    TestHelperImplementation,
    BuilderImplementation,
    NormalizerImplementation,
    RepositoryImplementation,
    ViewmodelGeneration,
    WpfXamlStubOnly,
    WpfXamlLayoutImplementation,
    WpfViewmodelImplementation,
    WpfShellIntegration,
    RuntimeWiring,
    TestRegistryImplementation,
    SnapshotBuilderImplementation,
    FindingsNormalizerImplementation
}

public sealed class CSharpGenerationPromptContractRecord
{
    public string ContractVersion { get; set; } = "ram_csharp_generation_contract.v1";
    public bool Applicable { get; set; }
    public CSharpGenerationArgumentContractRecord ArgumentContract { get; set; } = new();
    public string ToolName { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string TargetFramework { get; set; } = "";
    public string TemplateKind { get; set; } = "";
    public string NamespaceName { get; set; } = "";
    public string FileRole { get; set; } = "";
    public string DeclaredRole { get; set; } = "";
    public string DeclaredPattern { get; set; } = "";
    public string DeclaredProject { get; set; } = "";
    public string DeclaredNamespace { get; set; } = "";
    public string ImplementationDepth { get; set; } = "";
    public List<string> FollowThroughRequirements { get; set; } = [];
    public string ValidationTarget { get; set; } = "";
    public List<string> CompanionArtifactHints { get; set; } = [];
    public CSharpGenerationIntent Intent { get; set; } = CSharpGenerationIntent.None;
    public CSharpGenerationProfile Profile { get; set; } = CSharpGenerationProfile.None;
    public bool AllowPlaceholders { get; set; }
    public bool AllowAsync { get; set; }
    public bool BehaviorFirstAcceptance { get; set; }
    public List<string> RequiredTypeNames { get; set; } = [];
    public List<string> RequiredMemberNames { get; set; } = [];
    public List<string> RequiredApiTokens { get; set; } = [];
    public List<string> AllowedNamespaces { get; set; } = [];
    public List<string> ForbiddenNamespaces { get; set; } = [];
    public List<string> AllowedApiOwnerTokens { get; set; } = [];
    public List<string> ExpectedSiblingFiles { get; set; } = [];
    public List<string> LocalContextHints { get; set; } = [];
    public List<string> DependencyPrerequisites { get; set; } = [];
    public string DependencyStatus { get; set; } = "";
    public string DependencySummary { get; set; } = "";
    public List<string> ProfileRequirements { get; set; } = [];
    public string PromptContractText { get; set; } = "";
    public string Summary { get; set; } = "";
}

public sealed class CSharpGenerationProfileEnforcementRecord
{
    public string EnforcementVersion { get; set; } = "ram_csharp_generation_profile_enforcement.v1";
    public string Status { get; set; } = "";
    public List<string> FailedRules { get; set; } = [];
    public List<string> ObservedSignals { get; set; } = [];
    public string Summary { get; set; } = "";
}

public sealed class CSharpGenerationGuardrailEvaluationRecord
{
    public string EvaluationVersion { get; set; } = "ram_csharp_generation_guardrail_evaluation.v1";
    public CSharpGenerationPromptContractRecord Contract { get; set; } = new();
    public bool Accepted { get; set; }
    public string DecisionCode { get; set; } = "";
    public string AntiStubStatus { get; set; } = "";
    public string AntiHallucinationStatus { get; set; } = "";
    public string BehaviorStatus { get; set; } = "";
    public CSharpGenerationProfileEnforcementRecord ProfileEnforcement { get; set; } = new();
    public string PostWriteCheckStatus { get; set; } = "";
    public string FamilyAlignmentStatus { get; set; } = "";
    public string IntegrationStatus { get; set; } = "";
    public string BehaviorDepthTier { get; set; } = "";
    public string PrimaryRejectionClass { get; set; } = "";
    public bool RetrySuggested { get; set; }
    public string RetryStatus { get; set; } = "";
    public List<string> RejectionReasons { get; set; } = [];
    public List<string> UnexpectedNamespaces { get; set; } = [];
    public List<string> UnexpectedApiOwners { get; set; } = [];
    public List<string> MissingRequiredTypes { get; set; } = [];
    public List<string> MissingRequiredMembers { get; set; } = [];
    public List<string> MissingRequiredApiTokens { get; set; } = [];
    public List<string> DependencyPrerequisites { get; set; } = [];
    public string DependencyStatus { get; set; } = "";
    public string DependencySummary { get; set; } = "";
    public string EscalationStatus { get; set; } = "";
    public string EscalationSummary { get; set; } = "";
    public List<string> PostWriteFailedRules { get; set; } = [];
    public List<string> PostWriteObservedSignals { get; set; } = [];
    public string OutputQuality { get; set; } = "";
    public string CompletionStrength { get; set; } = "";
    public bool StrongerBehaviorProofStillMissing { get; set; }
    public string Summary { get; set; } = "";
}
