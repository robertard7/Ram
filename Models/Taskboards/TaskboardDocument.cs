namespace RAM.Models;

public sealed class TaskboardDocument
{
    public string DocumentId { get; set; } = "";
    public string ImportId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string GrammarVersion { get; set; } = "";
    public string Title { get; set; } = "";
    public string RawTitle { get; set; } = "";
    public string NormalizedTitle { get; set; } = "";
    public TaskboardTitlePatternKind TitlePatternKind { get; set; } = TaskboardTitlePatternKind.Unknown;
    public string ContentHash { get; set; } = "";
    public string ParserVersion { get; set; } = "";
    public string ModelBuilderVersion { get; set; } = "";
    public TaskboardPhaseMetadata PhaseMetadata { get; set; } = new();
    public string ObjectiveText { get; set; } = "";
    public string ObjectiveSectionId { get; set; } = "";
    public TaskboardGuardrails Guardrails { get; set; } = new();
    public List<TaskboardBatch> Batches { get; set; } = [];
    public TaskboardAcceptanceCriteria AcceptanceCriteria { get; set; } = new();
    public TaskboardInvariant Invariants { get; set; } = new();
    public List<TaskboardSectionContent> AdditionalSections { get; set; } = [];
}

public sealed class TaskboardPhaseMetadata
{
    public string PhaseLabel { get; set; } = "";
    public string PhaseTitle { get; set; } = "";
    public string DisplayTitle { get; set; } = "";
}

public sealed class TaskboardBatch
{
    public string BatchId { get; set; } = "";
    public int BatchNumber { get; set; }
    public string Title { get; set; } = "";
    public TaskboardSectionContent Content { get; set; } = new();
    public List<TaskboardStep> Steps { get; set; } = [];
}

public sealed class TaskboardStep
{
    public string StepId { get; set; } = "";
    public int Ordinal { get; set; }
    public string Title { get; set; } = "";
    public TaskboardSectionContent Content { get; set; } = new();
}

public sealed class TaskboardSectionContent
{
    public string SectionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string RawHeadingText { get; set; } = "";
    public int RawHeadingLevel { get; set; }
    public TaskboardHeadingKind NormalizedHeadingKind { get; set; } = TaskboardHeadingKind.Unknown;
    public int SourceLineStart { get; set; }
    public int SourceLineEnd { get; set; }
    public List<string> Paragraphs { get; set; } = [];
    public List<string> BulletItems { get; set; } = [];
    public List<string> NumberedItems { get; set; } = [];
    public List<TaskboardSectionContent> Subsections { get; set; } = [];
}

public sealed class TaskboardGuardrails
{
    public string SectionId { get; set; } = "";
    public string Title { get; set; } = "";
    public List<TaskboardSectionContent> Buckets { get; set; } = [];
}

public sealed class TaskboardAcceptanceCriteria
{
    public string SectionId { get; set; } = "";
    public string Title { get; set; } = "";
    public List<TaskboardSectionContent> Sections { get; set; } = [];
}

public sealed class TaskboardInvariant
{
    public string SectionId { get; set; } = "";
    public string Title { get; set; } = "";
    public List<TaskboardSectionContent> Sections { get; set; } = [];
}
