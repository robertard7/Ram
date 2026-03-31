namespace RAM.Models;

public sealed class CSharpModificationIntentRecord
{
    public string ResolverVersion { get; set; } = "csharp_modification_intent.v1";
    public string ModificationIntent { get; set; } = "";
    public string OperationKind { get; set; } = "";
    public string RequestedPattern { get; set; } = "";
    public string RequestedRoleHint { get; set; } = "";
    public string TargetSurfaceType { get; set; } = "";
    public string TargetProject { get; set; } = "";
    public string FollowThroughMode { get; set; } = "";
    public string RepairCause { get; set; } = "";
    public string FeatureName { get; set; } = "";
    public List<string> SuggestedCompletionContract { get; set; } = [];
    public List<string> SuggestedPreserveConstraints { get; set; } = [];
    public List<string> ClassificationReasons { get; set; } = [];
    public string Summary { get; set; } = "";
}

public sealed class CSharpEditSurfacePlanRecord
{
    public string PlannerVersion { get; set; } = "csharp_edit_surface_plan.v1";
    public string ModificationIntent { get; set; } = "";
    public string TargetSurfaceType { get; set; } = "";
    public string TargetProject { get; set; } = "";
    public string TargetProjectPath { get; set; } = "";
    public List<string> TargetFiles { get; set; } = [];
    public List<string> SupportingFiles { get; set; } = [];
    public List<string> RelatedSymbols { get; set; } = [];
    public string EditScope { get; set; } = "";
    public List<string> RetrievalContextRequirements { get; set; } = [];
    public string FollowThroughMode { get; set; } = "";
    public List<string> CompletionContract { get; set; } = [];
    public List<string> PreserveConstraints { get; set; } = [];
    public List<string> VerificationSurfaces { get; set; } = [];
    public string RegistrationSurface { get; set; } = "";
    public string TestUpdateScope { get; set; } = "";
    public List<string> NamespaceConstraints { get; set; } = [];
    public List<string> DependencyUpdateRequirements { get; set; } = [];
    public List<string> OutOfScopeSurfaces { get; set; } = [];
    public List<CSharpModificationSurfaceRecord> EditSurfaceFiles { get; set; } = [];
    public List<string> PlanningReasons { get; set; } = [];
    public string Summary { get; set; } = "";
}
