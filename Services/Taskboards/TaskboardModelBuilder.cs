using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardModelBuilder
{
    public const string ModelBuilderVersion = "3";

    public TaskboardDocument Build(
        string importId,
        string workspaceRoot,
        string contentHash,
        TaskboardMarkdownDocument markdownDocument,
        string parserVersion)
    {
        var document = new TaskboardDocument
        {
            DocumentId = StableId("doc", contentHash, markdownDocument.Title),
            ImportId = importId,
            WorkspaceRoot = workspaceRoot,
            GrammarVersion = markdownDocument.GrammarVersion,
            Title = string.IsNullOrWhiteSpace(markdownDocument.NormalizedTitle) ? markdownDocument.Title : markdownDocument.NormalizedTitle,
            RawTitle = string.IsNullOrWhiteSpace(markdownDocument.RawTitle) ? markdownDocument.Title : markdownDocument.RawTitle,
            NormalizedTitle = string.IsNullOrWhiteSpace(markdownDocument.NormalizedTitle) ? markdownDocument.Title : markdownDocument.NormalizedTitle,
            TitlePatternKind = markdownDocument.TitlePatternKind,
            ContentHash = contentHash,
            ParserVersion = parserVersion,
            ModelBuilderVersion = ModelBuilderVersion,
            PhaseMetadata = BuildPhaseMetadata(string.IsNullOrWhiteSpace(markdownDocument.NormalizedTitle) ? markdownDocument.Title : markdownDocument.NormalizedTitle)
        };

        foreach (var section in markdownDocument.Sections)
        {
            if (IsObjectiveSection(section))
            {
                var content = BuildSectionContent(contentHash, "objective", section, includeChildren: true);
                document.ObjectiveSectionId = content.SectionId;
                document.ObjectiveText = BuildTextSummary(content);
                continue;
            }

            if (TryBuildBatch(contentHash, section, out var batch))
            {
                document.Batches.Add(batch);
                continue;
            }

            if (IsGuardrailsSection(section))
            {
                document.Guardrails = new TaskboardGuardrails
                {
                    SectionId = StableId("guardrails", contentHash, section.HeadingText),
                    Title = section.HeadingText,
                    Buckets = section.Children.Count > 0
                        ? section.Children.Select((child, index) => BuildSectionContent(contentHash, $"guardrails:{index + 1}", child, includeChildren: true)).ToList()
                        : [BuildSectionContent(contentHash, "guardrails", section, includeChildren: false)]
                };
                continue;
            }

            if (IsAcceptanceSection(section))
            {
                document.AcceptanceCriteria = new TaskboardAcceptanceCriteria
                {
                    SectionId = StableId("acceptance", contentHash, section.HeadingText),
                    Title = section.HeadingText,
                    Sections = section.Children.Count > 0
                        ? section.Children.Select((child, index) => BuildSectionContent(contentHash, $"acceptance:{index + 1}", child, includeChildren: true)).ToList()
                        : [BuildSectionContent(contentHash, "acceptance", section, includeChildren: false)]
                };
                continue;
            }

            if (IsInvariantSection(section))
            {
                document.Invariants = new TaskboardInvariant
                {
                    SectionId = StableId("invariants", contentHash, section.HeadingText),
                    Title = section.HeadingText,
                    Sections = section.Children.Count > 0
                        ? section.Children.Select((child, index) => BuildSectionContent(contentHash, $"invariants:{index + 1}", child, includeChildren: true)).ToList()
                        : [BuildSectionContent(contentHash, "invariants", section, includeChildren: false)]
                };
                continue;
            }

            document.AdditionalSections.Add(BuildSectionContent(contentHash, $"section:{document.AdditionalSections.Count + 1}", section, includeChildren: true));
        }

        return document;
    }

    private static TaskboardPhaseMetadata BuildPhaseMetadata(string title)
    {
        var normalized = (title ?? "").Trim();
        var suffix = StripTaskboardPrefix(normalized);
        if (string.IsNullOrWhiteSpace(suffix))
            suffix = normalized;

        var prefixedPhaseMatch = Regex.Match(
            suffix,
            @"^(?<phase>Phase\s+\S+)\s*:\s*(?<name>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (prefixedPhaseMatch.Success)
        {
            return new TaskboardPhaseMetadata
            {
                PhaseLabel = prefixedPhaseMatch.Groups["phase"].Value.Trim(),
                PhaseTitle = prefixedPhaseMatch.Groups["name"].Value.Trim(),
                DisplayTitle = suffix
            };
        }

        var infixPhaseMatch = Regex.Match(
            normalized,
            @"^(?<phase>Phase\s+\S+)\s+Taskboard\s*(?:—|-|:)\s*(?<name>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (infixPhaseMatch.Success)
        {
            return new TaskboardPhaseMetadata
            {
                PhaseLabel = infixPhaseMatch.Groups["phase"].Value.Trim(),
                PhaseTitle = infixPhaseMatch.Groups["name"].Value.Trim(),
                DisplayTitle = normalized
            };
        }

        return new TaskboardPhaseMetadata
        {
            DisplayTitle = suffix
        };
    }

    private static bool TryBuildBatch(string contentHash, TaskboardMarkdownSection section, out TaskboardBatch batch)
    {
        if (!TaskboardGrammarService.TryParseBatchHeading(section.HeadingText, out var batchNumber, out var batchTitle))
        {
            batch = new TaskboardBatch();
            return false;
        }

        var executableLines = section.Blocks
            .Where(block => block.BlockType == TaskboardMarkdownBlockType.BulletList)
            .SelectMany(block => block.Lines.Select((line, index) => new
            {
                Text = line.Trim(),
                SourceLine = block.SourceLineStart + index
            }))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Text))
            .ToList();

        batch = new TaskboardBatch
        {
            BatchId = StableId("batch", contentHash, batchNumber.ToString(), batchTitle),
            BatchNumber = batchNumber,
            Title = batchTitle,
            Content = BuildSectionContent(
                contentHash,
                $"batch:{batchNumber}",
                section,
                includeChildren: true,
                childSections: section.Children.Where(child => child.SectionRole == TaskboardSectionRole.Metadata)),
            Steps = executableLines
                .Select((entry, index) => new TaskboardStep
                {
                    StepId = StableId("step", contentHash, batchNumber.ToString(), (index + 1).ToString(), entry.Text),
                    Ordinal = index + 1,
                    Title = entry.Text,
                    Content = new TaskboardSectionContent
                    {
                        SectionId = StableId($"batch:{batchNumber}:step:{index + 1}", contentHash, entry.Text, entry.SourceLine.ToString()),
                        Title = entry.Text,
                        SourceLineStart = entry.SourceLine,
                        SourceLineEnd = entry.SourceLine,
                        BulletItems = [entry.Text]
                    }
                })
                .ToList()
        };
        return true;
    }

    private static TaskboardSectionContent BuildSectionContent(
        string contentHash,
        string prefix,
        TaskboardMarkdownSection section,
        bool includeChildren,
        IEnumerable<TaskboardMarkdownSection>? childSections = null)
    {
        var selectedChildren = includeChildren
            ? (childSections ?? section.Children).ToList()
            : [];

        return new TaskboardSectionContent
        {
            SectionId = StableId(prefix, contentHash, section.HeadingText, section.SourceLineStart.ToString()),
            Title = section.HeadingText,
            RawHeadingText = string.IsNullOrWhiteSpace(section.RawHeadingText) ? section.HeadingText : section.RawHeadingText,
            RawHeadingLevel = section.HeadingLevel,
            NormalizedHeadingKind = section.NormalizedHeadingKind,
            SourceLineStart = section.SourceLineStart,
            SourceLineEnd = section.SourceLineEnd,
            Paragraphs = ExtractBlockLines(section.Blocks, TaskboardMarkdownBlockType.Paragraph),
            BulletItems = ExtractBlockLines(section.Blocks, TaskboardMarkdownBlockType.BulletList),
            NumberedItems = ExtractBlockLines(section.Blocks, TaskboardMarkdownBlockType.NumberedList),
            Subsections = includeChildren
                ? selectedChildren.Select((child, index) => BuildSectionContent(contentHash, $"{prefix}:{index + 1}", child, includeChildren: true)).ToList()
                : []
        };
    }

    private static List<string> ExtractBlockLines(IEnumerable<TaskboardMarkdownBlock> blocks, TaskboardMarkdownBlockType type)
    {
        return blocks
            .Where(block => block.BlockType == type)
            .SelectMany(block => block.Lines)
            .ToList();
    }

    private static string BuildTextSummary(TaskboardSectionContent section)
    {
        var parts = new List<string>();
        parts.AddRange(section.Paragraphs);
        parts.AddRange(section.BulletItems);
        parts.AddRange(section.NumberedItems);
        foreach (var subsection in section.Subsections)
        {
            var summary = BuildTextSummary(subsection);
            if (!string.IsNullOrWhiteSpace(summary))
                parts.Add(summary);
        }

        return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static bool IsObjectiveSection(TaskboardMarkdownSection section)
    {
        return string.Equals(section.HeadingText, "Objective", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGuardrailsSection(TaskboardMarkdownSection section)
    {
        return section.HeadingText.Contains("guardrail", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAcceptanceSection(TaskboardMarkdownSection section)
    {
        return section.HeadingText.Contains("acceptance", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInvariantSection(TaskboardMarkdownSection section)
    {
        return section.HeadingText.Contains("invariant", StringComparison.OrdinalIgnoreCase);
    }

    private static string StableId(string prefix, params string[] values)
    {
        var normalized = string.Join("|", values.Select(value => (value ?? "").Trim().ToLowerInvariant()));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"{prefix}_{Convert.ToHexString(bytes)[..12].ToLowerInvariant()}";
    }

    private static string StripTaskboardPrefix(string title)
    {
        var normalized = (title ?? "").Trim();
        if (normalized.StartsWith("CODEX TASKBOARD", StringComparison.OrdinalIgnoreCase))
            return normalized["CODEX TASKBOARD".Length..].Trim().TrimStart('—', '-', ':').Trim();
        if (normalized.StartsWith("TASKBOARD", StringComparison.OrdinalIgnoreCase))
            return normalized["TASKBOARD".Length..].Trim().TrimStart('—', '-', ':').Trim();
        return normalized;
    }
}
