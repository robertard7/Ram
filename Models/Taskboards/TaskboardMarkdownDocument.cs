namespace RAM.Models;

public enum TaskboardMarkdownBlockType
{
    Paragraph,
    BulletList,
    NumberedList
}

public enum TaskboardSectionRole
{
    Unknown,
    Metadata,
    Batch,
    Invalid
}

public enum TaskboardHeadingKind
{
    Unknown,
    Section,
    Subsection,
    Detail
}

public enum TaskboardParserDiagnosticSeverity
{
    Warning,
    Error
}

public enum TaskboardLineClassificationKind
{
    Unknown,
    Title,
    BatchHeading,
    MetadataHeading,
    ExecutableStep,
    MetadataBody,
    InvalidContent
}

public sealed class TaskboardMarkdownDocument
{
    public string GrammarVersion { get; set; } = "";
    public string Title { get; set; } = "";
    public string RawTitle { get; set; } = "";
    public string NormalizedTitle { get; set; } = "";
    public TaskboardTitlePatternKind TitlePatternKind { get; set; } = TaskboardTitlePatternKind.Unknown;
    public int TitleLineNumber { get; set; }
    public int LineCount { get; set; }
    public List<TaskboardMarkdownSection> Sections { get; set; } = [];
    public List<TaskboardLineClassification> LineClassifications { get; set; } = [];
}

public sealed class TaskboardMarkdownSection
{
    public string HeadingText { get; set; } = "";
    public string RawHeadingText { get; set; } = "";
    public int HeadingLevel { get; set; }
    public TaskboardHeadingKind NormalizedHeadingKind { get; set; } = TaskboardHeadingKind.Unknown;
    public TaskboardSectionRole SectionRole { get; set; } = TaskboardSectionRole.Unknown;
    public bool IsExecutableSection { get; set; }
    public int SourceLineStart { get; set; }
    public int SourceLineEnd { get; set; }
    public List<TaskboardMarkdownBlock> Blocks { get; set; } = [];
    public List<TaskboardMarkdownSection> Children { get; set; } = [];
}

public sealed class TaskboardMarkdownBlock
{
    public TaskboardMarkdownBlockType BlockType { get; set; } = TaskboardMarkdownBlockType.Paragraph;
    public int SourceLineStart { get; set; }
    public int SourceLineEnd { get; set; }
    public List<string> Lines { get; set; } = [];
}

public sealed class TaskboardLineClassification
{
    public int LineNumber { get; set; }
    public string RawText { get; set; } = "";
    public string NormalizedText { get; set; } = "";
    public TaskboardLineClassificationKind Classification { get; set; } = TaskboardLineClassificationKind.Unknown;
    public string SectionHeading { get; set; } = "";
    public string Code { get; set; } = "";
    public string ExpectedGrammar { get; set; } = "";
}

public sealed class TaskboardParserDiagnostic
{
    public TaskboardParserDiagnosticSeverity Severity { get; set; } = TaskboardParserDiagnosticSeverity.Warning;
    public string Code { get; set; } = "";
    public int LineNumber { get; set; }
    public string Message { get; set; } = "";
    public string OffendingText { get; set; } = "";
    public TaskboardLineClassificationKind LineClassification { get; set; } = TaskboardLineClassificationKind.Unknown;
    public string ExpectedGrammar { get; set; } = "";
}

public sealed class TaskboardParseResult
{
    public string ParserVersion { get; set; } = "";
    public bool Success { get; set; }
    public TaskboardMarkdownDocument? Document { get; set; }
    public List<TaskboardParserDiagnostic> Diagnostics { get; set; } = [];
}
