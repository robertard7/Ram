namespace RAM.Models;

public sealed class TaskboardMaintenanceBaselineRecord
{
    public bool IsMaintenanceMode { get; set; }
    public bool BaselineDeclared { get; set; }
    public bool BaselineResolved { get; set; }
    public bool BaselineAutoBound { get; set; }
    public string PlanTitle { get; set; } = "";
    public string Summary { get; set; } = "";
    public string DeclaredSolutionPath { get; set; } = "";
    public string PrimarySolutionPath { get; set; } = "";
    public string PrimaryUiProjectPath { get; set; } = "";
    public string CoreProjectPath { get; set; } = "";
    public string ServicesProjectPath { get; set; } = "";
    public string StorageProjectPath { get; set; } = "";
    public string StorageAuthorityRoot { get; set; } = "";
    public string StorageResolutionKind { get; set; } = "";
    public string StorageResolutionSummary { get; set; } = "";
    public string TestsProjectPath { get; set; } = "";
    public string BindingSource { get; set; } = "";
    public List<string> DeclaredPaths { get; set; } = [];
    public List<string> DeclaredMutationRoots { get; set; } = [];
    public List<string> AllowedMutationRoots { get; set; } = [];
    public List<string> ExcludedGeneratedRoots { get; set; } = [];
    public List<string> DiscoveredProjectRoots { get; set; } = [];
    public List<string> CompatibleStorageRoots { get; set; } = [];
}

public sealed class TaskboardMaintenanceMutationGuardResult
{
    public bool Applies { get; set; }
    public bool Allowed { get; set; } = true;
    public string ReasonCode { get; set; } = "";
    public string Summary { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string BaselineSolutionPath { get; set; } = "";
    public List<string> AllowedRoots { get; set; } = [];
    public List<string> DeclaredRoots { get; set; } = [];
    public List<string> DiscoveredRoots { get; set; } = [];
    public List<string> CompatibleStorageRoots { get; set; } = [];
    public List<string> ExcludedRoots { get; set; } = [];
    public string StorageResolutionKind { get; set; } = "";
    public string StorageResolutionSummary { get; set; } = "";
}
