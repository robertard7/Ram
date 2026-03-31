using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardMarkdownParser
{
    public const string ParserVersion = "3";

    private const int MaxLineCount = 1800;
    private const int MaxBlockItemCount = 1200;

    private static readonly Regex HeadingRegex = new(@"^(?<hashes>#{1,6})\s+(?<text>.+?)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex OrderedListRegex = new(@"^\d+\.\s+(?<text>.+?)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex BulletListRegex = new(@"^[-*]\s+(?<text>.+?)\s*$", RegexOptions.CultureInvariant);

    public TaskboardParseResult Parse(string text)
    {
        var result = new TaskboardParseResult
        {
            ParserVersion = ParserVersion
        };

        var normalized = NormalizeLines(text);
        var lines = normalized.Split('\n');
        if (lines.Length > MaxLineCount)
        {
            result.Diagnostics.Add(new TaskboardParserDiagnostic
            {
                Severity = TaskboardParserDiagnosticSeverity.Error,
                Code = "line_limit_exceeded",
                Message = $"The taskboard exceeds the parser line limit of {MaxLineCount} lines.",
                ExpectedGrammar = TaskboardGrammarService.BuildCanonicalFormHint()
            });
            return result;
        }

        var document = new TaskboardMarkdownDocument
        {
            GrammarVersion = TaskboardGrammarService.GrammarVersion,
            LineCount = lines.Length
        };

        TaskboardMarkdownSection? currentLevel2 = null;
        TaskboardMarkdownSection? currentMetadataLevel3 = null;
        var paragraphLines = new List<string>();
        var listItems = new List<string>();
        TaskboardMarkdownBlockType? listType = null;
        var blockStartLine = 0;
        var seenBatchHeadings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batchStarted = false;

        void AddDiagnostic(
            TaskboardParserDiagnosticSeverity severity,
            string code,
            int lineNumber,
            string message,
            string offendingText = "",
            TaskboardLineClassificationKind lineClassification = TaskboardLineClassificationKind.Unknown,
            string expectedGrammar = "")
        {
            result.Diagnostics.Add(new TaskboardParserDiagnostic
            {
                Severity = severity,
                Code = code,
                LineNumber = lineNumber,
                Message = message,
                OffendingText = offendingText ?? "",
                LineClassification = lineClassification,
                ExpectedGrammar = expectedGrammar ?? ""
            });
        }

        void AddLineClassification(
            int lineNumber,
            string rawText,
            TaskboardLineClassificationKind classification,
            string sectionHeading = "",
            string code = "",
            string expectedGrammar = "")
        {
            document.LineClassifications.Add(new TaskboardLineClassification
            {
                LineNumber = lineNumber,
                RawText = rawText ?? "",
                NormalizedText = (rawText ?? "").Trim(),
                Classification = classification,
                SectionHeading = sectionHeading ?? "",
                Code = code ?? "",
                ExpectedGrammar = expectedGrammar ?? ""
            });
        }

        TaskboardMarkdownSection? CurrentSection() => currentMetadataLevel3 ?? currentLevel2;

        TaskboardSectionRole CurrentTopLevelRole() => currentLevel2?.SectionRole ?? TaskboardSectionRole.Unknown;

        void AttachBlock(TaskboardMarkdownBlock block)
        {
            var section = CurrentSection();
            if (section is null)
            {
                AddDiagnostic(
                    TaskboardParserDiagnosticSeverity.Error,
                    "content_outside_section",
                    block.SourceLineStart,
                    "Found content outside a supported section.",
                    offendingText: string.Join(" ", block.Lines),
                    lineClassification: TaskboardLineClassificationKind.InvalidContent,
                    expectedGrammar: "Place content under the H1 title and inside metadata H2 sections or batch sections.");
                return;
            }

            section.Blocks.Add(block);
            section.SourceLineEnd = Math.Max(section.SourceLineEnd, block.SourceLineEnd);
            if (currentLevel2 is not null)
                currentLevel2.SourceLineEnd = Math.Max(currentLevel2.SourceLineEnd, block.SourceLineEnd);
        }

        void FlushParagraph()
        {
            if (paragraphLines.Count == 0)
                return;

            AttachBlock(new TaskboardMarkdownBlock
            {
                BlockType = TaskboardMarkdownBlockType.Paragraph,
                SourceLineStart = blockStartLine,
                SourceLineEnd = blockStartLine + paragraphLines.Count - 1,
                Lines = [.. paragraphLines]
            });

            paragraphLines.Clear();
            blockStartLine = 0;
        }

        void FlushList()
        {
            if (listType is null || listItems.Count == 0)
                return;

            AttachBlock(new TaskboardMarkdownBlock
            {
                BlockType = listType.Value,
                SourceLineStart = blockStartLine,
                SourceLineEnd = blockStartLine + listItems.Count - 1,
                Lines = [.. listItems]
            });

            listItems.Clear();
            listType = null;
            blockStartLine = 0;
        }

        void FlushBlocks()
        {
            FlushParagraph();
            FlushList();
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var rawLine = lines[index];
            var trimmed = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                FlushBlocks();
                continue;
            }

            var headingMatch = HeadingRegex.Match(trimmed);
            if (headingMatch.Success)
            {
                FlushBlocks();

                var level = headingMatch.Groups["hashes"].Value.Length;
                var headingText = headingMatch.Groups["text"].Value.Trim();

                if (level == 1)
                {
                    if (!string.IsNullOrWhiteSpace(document.Title))
                    {
                        AddLineClassification(
                            lineNumber,
                            rawLine,
                            TaskboardLineClassificationKind.InvalidContent,
                            code: "unsupported_multiple_h1",
                            expectedGrammar: "Use exactly one H1 title at the top of the taskboard.");
                        AddDiagnostic(
                            TaskboardParserDiagnosticSeverity.Error,
                            "unsupported_multiple_h1",
                            lineNumber,
                            "Only one H1 taskboard title is supported.",
                            offendingText: headingText,
                            lineClassification: TaskboardLineClassificationKind.InvalidContent,
                            expectedGrammar: "Use exactly one H1 title at the top of the taskboard.");
                        currentLevel2 = null;
                        currentMetadataLevel3 = null;
                        continue;
                    }

                    document.Title = headingText;
                    document.RawTitle = headingText;
                    document.NormalizedTitle = NormalizeTitle(headingText);
                    document.TitleLineNumber = lineNumber;
                    currentLevel2 = null;
                    currentMetadataLevel3 = null;
                    AddLineClassification(lineNumber, rawLine, TaskboardLineClassificationKind.Title, code: "h1_title");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(document.Title))
                {
                    AddLineClassification(
                        lineNumber,
                        rawLine,
                        TaskboardLineClassificationKind.InvalidContent,
                        code: "heading_before_title",
                        expectedGrammar: "Start the document with exactly one H1 taskboard title.");
                    AddDiagnostic(
                        TaskboardParserDiagnosticSeverity.Error,
                        "heading_before_title",
                        lineNumber,
                        "Taskboard sections must come after the H1 title.",
                        offendingText: headingText,
                        lineClassification: TaskboardLineClassificationKind.InvalidContent,
                        expectedGrammar: "Start the document with exactly one H1 taskboard title.");
                    currentLevel2 = null;
                    currentMetadataLevel3 = null;
                    continue;
                }

                if (level == 2)
                {
                    currentMetadataLevel3 = null;

                    if (TaskboardGrammarService.TryParseBatchHeading(headingText, out _, out _))
                    {
                        currentLevel2 = new TaskboardMarkdownSection
                        {
                            HeadingText = headingText,
                            RawHeadingText = headingText,
                            HeadingLevel = 2,
                            NormalizedHeadingKind = TaskboardHeadingKind.Section,
                            SectionRole = TaskboardSectionRole.Batch,
                            IsExecutableSection = true,
                            SourceLineStart = lineNumber,
                            SourceLineEnd = lineNumber
                        };
                        document.Sections.Add(currentLevel2);
                        AddLineClassification(lineNumber, rawLine, TaskboardLineClassificationKind.BatchHeading, headingText, "batch_heading");
                        batchStarted = true;

                        if (!seenBatchHeadings.Add(NormalizeKey(headingText)))
                        {
                            AddDiagnostic(
                                TaskboardParserDiagnosticSeverity.Warning,
                                "duplicate_batch_heading",
                                lineNumber,
                                $"Duplicate batch heading detected: {headingText}.",
                                offendingText: headingText,
                                lineClassification: TaskboardLineClassificationKind.BatchHeading,
                                expectedGrammar: "Each batch heading should be unique.");
                        }

                        continue;
                    }

                    if (!batchStarted)
                    {
                        currentLevel2 = new TaskboardMarkdownSection
                        {
                            HeadingText = headingText,
                            RawHeadingText = headingText,
                            HeadingLevel = 2,
                            NormalizedHeadingKind = TaskboardHeadingKind.Section,
                            SectionRole = TaskboardSectionRole.Metadata,
                            IsExecutableSection = false,
                            SourceLineStart = lineNumber,
                            SourceLineEnd = lineNumber
                        };
                        document.Sections.Add(currentLevel2);
                        AddLineClassification(lineNumber, rawLine, TaskboardLineClassificationKind.MetadataHeading, headingText, "metadata_heading");
                        continue;
                    }

                    var metadataAfterBatch = batchStarted;
                    var expectedHeadingGrammar = TaskboardGrammarService.BuildExpectedHeadingGrammar(batchStarted);
                    var code = metadataAfterBatch ? "metadata_after_batch_section" : "unsupported_section_heading";
                    var message = metadataAfterBatch
                        ? $"Metadata heading `{headingText}` is not allowed after executable batch sections have started."
                        : $"Heading `{headingText}` is not a supported taskboard section heading.";
                    AddLineClassification(
                        lineNumber,
                        rawLine,
                        TaskboardLineClassificationKind.InvalidContent,
                        code: code,
                        expectedGrammar: expectedHeadingGrammar);
                    AddDiagnostic(
                        TaskboardParserDiagnosticSeverity.Error,
                        code,
                        lineNumber,
                        message,
                        offendingText: headingText,
                        lineClassification: TaskboardLineClassificationKind.InvalidContent,
                        expectedGrammar: expectedHeadingGrammar);
                    currentLevel2 = null;
                    continue;
                }

                if (level == 3)
                {
                    if (currentLevel2 is null)
                    {
                        AddLineClassification(
                            lineNumber,
                            rawLine,
                            TaskboardLineClassificationKind.InvalidContent,
                            code: "unsupported_nested_heading_structure",
                            expectedGrammar: "Use H3 headings only under metadata H2 sections.");
                        AddDiagnostic(
                            TaskboardParserDiagnosticSeverity.Error,
                            "unsupported_nested_heading_structure",
                            lineNumber,
                            "H3 subsections require a metadata H2 parent.",
                            offendingText: headingText,
                            lineClassification: TaskboardLineClassificationKind.InvalidContent,
                            expectedGrammar: "Use H3 headings only under metadata H2 sections.");
                        continue;
                    }

                    if (CurrentTopLevelRole() == TaskboardSectionRole.Batch)
                    {
                        AddLineClassification(
                            lineNumber,
                            rawLine,
                            TaskboardLineClassificationKind.InvalidContent,
                            currentLevel2.HeadingText,
                            "unsupported_nested_section_under_batch",
                            TaskboardGrammarService.BuildExpectedBatchContentGrammar());
                        AddDiagnostic(
                            TaskboardParserDiagnosticSeverity.Error,
                            "unsupported_nested_section_under_batch",
                            lineNumber,
                            $"Nested heading `{headingText}` is not allowed inside batch `{currentLevel2.HeadingText}`.",
                            offendingText: headingText,
                            lineClassification: TaskboardLineClassificationKind.InvalidContent,
                            expectedGrammar: TaskboardGrammarService.BuildExpectedBatchContentGrammar());
                        continue;
                    }

                    currentMetadataLevel3 = new TaskboardMarkdownSection
                    {
                        HeadingText = headingText,
                        RawHeadingText = headingText,
                        HeadingLevel = 3,
                        NormalizedHeadingKind = TaskboardHeadingKind.Subsection,
                        SectionRole = TaskboardSectionRole.Metadata,
                        IsExecutableSection = false,
                        SourceLineStart = lineNumber,
                        SourceLineEnd = lineNumber
                    };
                    currentLevel2.Children.Add(currentMetadataLevel3);
                    currentLevel2.SourceLineEnd = Math.Max(currentLevel2.SourceLineEnd, lineNumber);
                    AddLineClassification(lineNumber, rawLine, TaskboardLineClassificationKind.MetadataHeading, headingText, "metadata_subsection");
                    continue;
                }

                AddLineClassification(
                    lineNumber,
                    rawLine,
                    TaskboardLineClassificationKind.InvalidContent,
                    code: "unsupported_nested_heading_structure",
                    expectedGrammar: "Supported taskboard grammar uses H1, H2, and optional H3 under metadata sections only.");
                AddDiagnostic(
                    TaskboardParserDiagnosticSeverity.Error,
                    "unsupported_nested_heading_structure",
                    lineNumber,
                    $"Heading level {level} `{headingText}` exceeds the supported taskboard grammar depth.",
                    offendingText: headingText,
                    lineClassification: TaskboardLineClassificationKind.InvalidContent,
                    expectedGrammar: "Supported taskboard grammar uses H1, H2, and optional H3 under metadata sections only.");
                currentMetadataLevel3 = null;
                continue;
            }

            var orderedMatch = OrderedListRegex.Match(trimmed);
            if (orderedMatch.Success)
            {
                var textValue = orderedMatch.Groups["text"].Value.Trim();
                if (CurrentTopLevelRole() == TaskboardSectionRole.Batch)
                {
                    AddLineClassification(
                        lineNumber,
                        rawLine,
                        TaskboardLineClassificationKind.InvalidContent,
                        currentLevel2?.HeadingText ?? "",
                        "unsupported_executable_step_format",
                        "Inside a batch, use `- command` bullet steps only.");
                    AddDiagnostic(
                        TaskboardParserDiagnosticSeverity.Error,
                        "unsupported_executable_step_format",
                        lineNumber,
                        $"Numbered item `{textValue}` is not a supported executable step format inside a batch.",
                        offendingText: textValue,
                        lineClassification: TaskboardLineClassificationKind.InvalidContent,
                        expectedGrammar: "Inside a batch, use `- command` bullet steps only.");
                }
                else if (CurrentSection() is null)
                {
                    AddLineClassification(
                        lineNumber,
                        rawLine,
                        TaskboardLineClassificationKind.InvalidContent,
                        code: "step_outside_batch",
                        expectedGrammar: "Executable steps must appear as `- command` lines inside a `## Batch N — Name` section.");
                    AddDiagnostic(
                        TaskboardParserDiagnosticSeverity.Error,
                        "step_outside_batch",
                        lineNumber,
                        "Found a list item outside a supported batch or metadata section.",
                        offendingText: textValue,
                        lineClassification: TaskboardLineClassificationKind.InvalidContent,
                        expectedGrammar: "Executable steps must appear as `- command` lines inside a `## Batch N — Name` section.");
                }
                else
                {
                    FlushParagraph();
                    if (listType != TaskboardMarkdownBlockType.NumberedList)
                    {
                        FlushList();
                        listType = TaskboardMarkdownBlockType.NumberedList;
                        blockStartLine = lineNumber;
                    }

                    listItems.Add(textValue);
                    AddLineClassification(lineNumber, rawLine, TaskboardLineClassificationKind.MetadataBody, CurrentSection()?.HeadingText ?? "", "metadata_numbered_item");
                    if (listItems.Count > MaxBlockItemCount)
                    {
                        AddDiagnostic(
                            TaskboardParserDiagnosticSeverity.Error,
                            "list_item_limit_exceeded",
                            lineNumber,
                            $"A list exceeded the parser item limit of {MaxBlockItemCount} items.",
                            offendingText: textValue,
                            lineClassification: TaskboardLineClassificationKind.MetadataBody);
                    }
                }

                continue;
            }

            var bulletMatch = BulletListRegex.Match(trimmed);
            if (bulletMatch.Success)
            {
                var textValue = bulletMatch.Groups["text"].Value.Trim();
                FlushParagraph();
                if (CurrentSection() is null)
                {
                    AddLineClassification(
                        lineNumber,
                        rawLine,
                        TaskboardLineClassificationKind.InvalidContent,
                        code: "step_outside_batch",
                        expectedGrammar: "Executable steps must appear as `- command` lines inside a `## Batch N — Name` section.");
                    AddDiagnostic(
                        TaskboardParserDiagnosticSeverity.Error,
                        "step_outside_batch",
                        lineNumber,
                        "Found a bullet item outside a supported batch or metadata section.",
                        offendingText: textValue,
                        lineClassification: TaskboardLineClassificationKind.InvalidContent,
                        expectedGrammar: "Executable steps must appear as `- command` lines inside a `## Batch N — Name` section.");
                    continue;
                }

                if (listType != TaskboardMarkdownBlockType.BulletList)
                {
                    FlushList();
                    listType = TaskboardMarkdownBlockType.BulletList;
                    blockStartLine = lineNumber;
                }

                listItems.Add(textValue);
                AddLineClassification(
                    lineNumber,
                    rawLine,
                    CurrentTopLevelRole() == TaskboardSectionRole.Batch
                        ? TaskboardLineClassificationKind.ExecutableStep
                        : TaskboardLineClassificationKind.MetadataBody,
                    CurrentSection()?.HeadingText ?? "",
                    CurrentTopLevelRole() == TaskboardSectionRole.Batch ? "executable_step" : "metadata_bullet");
                if (listItems.Count > MaxBlockItemCount)
                {
                    AddDiagnostic(
                        TaskboardParserDiagnosticSeverity.Error,
                        "list_item_limit_exceeded",
                        lineNumber,
                        $"A list exceeded the parser item limit of {MaxBlockItemCount} items.",
                        offendingText: textValue,
                        lineClassification: CurrentTopLevelRole() == TaskboardSectionRole.Batch
                            ? TaskboardLineClassificationKind.ExecutableStep
                            : TaskboardLineClassificationKind.MetadataBody);
                }

                continue;
            }

            FlushList();
            if (paragraphLines.Count == 0)
                blockStartLine = lineNumber;
            paragraphLines.Add(trimmed);

            if (CurrentSection() is null)
            {
                AddLineClassification(
                    lineNumber,
                    rawLine,
                    TaskboardLineClassificationKind.InvalidContent,
                    code: "content_outside_section",
                    expectedGrammar: "Put prose under a metadata H2 section. Executable steps belong only inside batch sections.");
                AddDiagnostic(
                    TaskboardParserDiagnosticSeverity.Error,
                    "content_outside_section",
                    lineNumber,
                    "Found content outside a supported section.",
                    offendingText: trimmed,
                    lineClassification: TaskboardLineClassificationKind.InvalidContent,
                    expectedGrammar: "Put prose under a metadata H2 section. Executable steps belong only inside batch sections.");
                continue;
            }

            if (CurrentTopLevelRole() == TaskboardSectionRole.Batch)
            {
                AddLineClassification(
                    lineNumber,
                    rawLine,
                    TaskboardLineClassificationKind.InvalidContent,
                    currentLevel2?.HeadingText ?? "",
                    "batch_contains_non_executable_prose",
                    TaskboardGrammarService.BuildExpectedBatchContentGrammar());
                AddDiagnostic(
                    TaskboardParserDiagnosticSeverity.Error,
                    "batch_contains_non_executable_prose",
                    lineNumber,
                    $"Paragraph text is not allowed inside batch `{currentLevel2?.HeadingText}`.",
                    offendingText: trimmed,
                    lineClassification: TaskboardLineClassificationKind.InvalidContent,
                    expectedGrammar: TaskboardGrammarService.BuildExpectedBatchContentGrammar());
                continue;
            }

            AddLineClassification(lineNumber, rawLine, TaskboardLineClassificationKind.MetadataBody, CurrentSection()?.HeadingText ?? "", "metadata_paragraph");
        }

        FlushBlocks();
        result.Document = document;
        result.Success = !result.Diagnostics.Any(diagnostic => diagnostic.Severity == TaskboardParserDiagnosticSeverity.Error);
        return result;
    }

    private static string NormalizeLines(string text)
    {
        return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static string NormalizeKey(string value)
    {
        return (value ?? "").Trim().ToLowerInvariant();
    }

    private static string NormalizeTitle(string value)
    {
        return string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
