namespace RAM.Models;

public sealed class BehaviorDepthEvidenceRecord
{
    public string EvidenceVersion { get; set; } = "ram_behavior_depth_evidence.v1";
    public string EvidenceId { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Profile { get; set; } = "";
    public string BehaviorDepthTier { get; set; } = "";
    public string OutputQuality { get; set; } = "";
    public string CompletionStrength { get; set; } = "";
    public bool StrongerBehaviorProofStillMissing { get; set; }
    public List<string> ChangedFiles { get; set; } = [];
    public List<string> CallerReferencePaths { get; set; } = [];
    public List<string> CalleeReferenceTokens { get; set; } = [];
    public bool DiOrRegistrationEvidenceFound { get; set; }
    public List<string> DiOrRegistrationEvidencePaths { get; set; } = [];
    public bool CommandViewModelOrBindingEvidenceFound { get; set; }
    public List<string> CommandViewModelOrBindingEvidencePaths { get; set; } = [];
    public bool RepositoryOrServiceLinkageFound { get; set; }
    public List<string> RepositoryOrServiceLinkagePaths { get; set; } = [];
    public bool TestLinkageFound { get; set; }
    public List<string> TestLinkagePaths { get; set; } = [];
    public List<string> ShallowPatternFlags { get; set; } = [];
    public string CompletionRecommendation { get; set; } = "";
    public string FollowUpRecommendation { get; set; } = "";
    public string SourceNamespace { get; set; } = "";
    public string TemplateKind { get; set; } = "";
    public string RequestedRole { get; set; } = "";
    public string RequestedPattern { get; set; } = "";
    public string RequestedProject { get; set; } = "";
    public string RequestedImplementationDepth { get; set; } = "";
    public List<string> RequestedFollowThroughRequirements { get; set; } = [];
    public string ValidationTarget { get; set; } = "";
    public List<string> CompanionArtifactHints { get; set; } = [];
    public string FeatureFamily { get; set; } = "";
    public string IntegrationGapKind { get; set; } = "";
    public string NextFollowThroughHint { get; set; } = "";
    public List<string> CandidateConsumerSurfaceHints { get; set; } = [];
    public List<string> EvidenceSummarySignals { get; set; } = [];
}
