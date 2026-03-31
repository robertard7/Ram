namespace RAM.Models;

public enum TaskboardHeadingClass
{
    Unknown,
    Actionable,
    StructuralSupport,
    ConstraintPolicy,
    ContextReference,
    ValidationProof,
    ConditionalFollowup,
    ScaffoldSupport,
    RepairSupport
}

public enum TaskboardHeadingTreatment
{
    Unknown,
    Actionable,
    Covered,
    ConstraintOnly,
    ContextOnly,
    ProofOnly,
    ConditionalOnly
}

public sealed class TaskboardHeadingPolicyRecord
{
    public string OriginalTitle { get; set; } = "";
    public string NormalizedTitle { get; set; } = "";
    public string CanonicalAlias { get; set; } = "";
    public TaskboardHeadingClass HeadingClass { get; set; } = TaskboardHeadingClass.Unknown;
    public TaskboardHeadingTreatment Treatment { get; set; } = TaskboardHeadingTreatment.Unknown;
    public string ReasonCode { get; set; } = "";
    public bool IsKnownAliasOrPattern { get; set; }
    public bool IsActionable => HeadingClass == TaskboardHeadingClass.Actionable && Treatment == TaskboardHeadingTreatment.Actionable;
    public bool IsNonActionableSupport => !IsActionable && HeadingClass != TaskboardHeadingClass.Unknown;
}
