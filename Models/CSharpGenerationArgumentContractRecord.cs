namespace RAM.Models;

public sealed class CSharpGenerationArgumentContractRecord
{
    public string ContractVersion { get; set; } = "ram_csharp_generation_arguments.v1";
    public string ModificationIntent { get; set; } = "";
    public string FileRole { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string ImplementationDepth { get; set; } = "";
    public string FollowThroughMode { get; set; } = "";
    public string TargetProject { get; set; } = "";
    public string TargetProjectPath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string NamespaceName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public List<string> BaseTypes { get; set; } = [];
    public List<string> Interfaces { get; set; } = [];
    public List<string> ConstructorDependencies { get; set; } = [];
    public List<string> RequiredUsings { get; set; } = [];
    public List<string> SupportingSurfaces { get; set; } = [];
    public List<string> CompletionContract { get; set; } = [];
    public string DomainEntity { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string StorageContext { get; set; } = "";
    public string TestSubject { get; set; } = "";
    public string UiSurface { get; set; } = "";
    public string FeatureName { get; set; } = "";
    public string RetrievalReadinessStatus { get; set; } = "";
    public string WorkspaceTruthFingerprint { get; set; } = "";
}

public sealed class CSharpGeneratedArtifactPlanRecord
{
    public string RelativePath { get; set; } = "";
    public string FileRole { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class CSharpGeneratedOutputPlanRecord
{
    public string PlanVersion { get; set; } = "ram_csharp_generated_output_plan.v1";
    public string Summary { get; set; } = "";
    public string TemplateGenerationSummary { get; set; } = "";
    public CSharpGeneratedArtifactPlanRecord PrimaryArtifact { get; set; } = new();
    public List<CSharpGeneratedArtifactPlanRecord> CompanionArtifacts { get; set; } = [];
}
