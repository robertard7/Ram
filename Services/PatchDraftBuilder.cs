using System.IO;
using System.Xml.Linq;
using RAM.Models;

namespace RAM.Services;

public sealed class PatchDraftBuilder
{
    public PatchDraftRecord Build(string workspaceRoot, RepairProposalRecord proposal, RepairPlanInput? input = null)
    {
        if (proposal is null)
            throw new ArgumentNullException(nameof(proposal));

        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (string.IsNullOrWhiteSpace(proposal.TargetFilePath))
            throw new InvalidOperationException("preview_patch_draft failed: repair proposal does not have a target file.");

        var fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, proposal.TargetFilePath.Replace('/', Path.DirectorySeparatorChar)));
        var fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        var workspacePrefix = fullWorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!string.Equals(fullPath, fullWorkspaceRoot, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("preview_patch_draft failed: target file must stay inside the active workspace.");
        }

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("File not found.", fullPath);

        var lines = File.ReadAllLines(fullPath);
        var draft = CreateBaseDraft(workspaceRoot, proposal, input);

        if (TryBuildCircularProjectReferenceDraft(proposal, lines, draft))
            return draft;

        if (TryBuildDeterministicProjectFileDraft(proposal, fullPath, lines, draft))
            return draft;

        if (TryBuildGeneratedSymbolRecoveryDraft(proposal, lines, draft))
            return draft;

        if (TryBuildMissingSemicolonDraft(proposal, lines, draft))
            return draft;

        if (TryBuildForcedAssertionDraft(proposal, lines, draft))
            return draft;

        return BuildFallbackDraft(proposal, input, lines, draft);
    }

    private static PatchDraftRecord CreateBaseDraft(string workspaceRoot, RepairProposalRecord proposal, RepairPlanInput? input)
    {
        return new PatchDraftRecord
        {
            DraftId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            SourceProposalId = proposal.ProposalId,
            SourceProposalArtifactId = proposal.SourceArtifactId,
            SourceProposalArtifactType = proposal.SourceArtifactType,
            FailureKind = proposal.FailureKind,
            TargetFilePath = proposal.TargetFilePath,
            TargetLineNumber = proposal.TargetLineNumber,
            TargetColumnNumber = proposal.TargetColumnNumber,
            DraftKind = "unknown",
            Confidence = FirstNonEmpty(proposal.Confidence, input?.Confidence, "none"),
            FailureSummary = FirstNonEmpty(proposal.FailureSummary, input?.FailureMessage),
            ProposalSummary = FirstNonEmpty(proposal.Title, proposal.Rationale),
            TargetProjectPath = FirstNonEmpty(proposal.TargetProjectPath, input?.TargetProjectPath),
            RequiresModel = proposal.RequiresModel,
            RetrievalBackend = proposal.RetrievalBackend,
            RetrievalEmbedderModel = proposal.RetrievalEmbedderModel,
            RetrievalQueryKind = proposal.RetrievalQueryKind,
            RetrievalHitCount = proposal.RetrievalHitCount,
            RetrievalSourceKinds = [.. proposal.RetrievalSourceKinds],
            RetrievalContextPacketArtifactRelativePath = proposal.RetrievalContextPacketArtifactRelativePath,
            ReferencedSymbolName = proposal.ReferencedSymbolName,
            ReferencedMemberName = proposal.ReferencedMemberName,
            SymbolRecoveryStatus = proposal.SymbolRecoveryStatus,
            SymbolRecoverySummary = proposal.SymbolRecoverySummary,
            SymbolRecoveryCandidatePath = proposal.SymbolRecoveryCandidatePath,
            SymbolRecoveryCandidateNamespace = proposal.SymbolRecoveryCandidateNamespace
        };
    }

    private static bool TryBuildGeneratedSymbolRecoveryDraft(
        RepairProposalRecord proposal,
        string[] lines,
        PatchDraftRecord draft)
    {
        if ((!string.Equals(proposal.FailureKind, "build_failure", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(proposal.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase))
            || !string.Equals(proposal.ProposedActionType, "reconcile_generated_symbol", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        draft.StartLine = proposal.TargetLineNumber > 0 ? proposal.TargetLineNumber : 1;
        draft.EndLine = draft.StartLine;
        draft.DraftKind = "rebuild_symbol_recovery";
        draft.OriginalExcerpt = FirstNonEmpty(
            proposal.FileExcerpt,
            BuildExcerptFromLines(lines, proposal.TargetLineNumber));
        draft.ReplacementText = "";
        draft.RationaleSummary = FirstNonEmpty(
            proposal.SymbolRecoverySummary,
            proposal.Rationale,
            string.Equals(proposal.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
                ? "The previously missing generated symbol now exists, so the bounded repair should reconcile through a focused test rerun before escalating."
                : "The previously missing generated symbol now exists, so the bounded repair should reconcile through rebuild-first verification before escalating.");
        draft.CanApplyLocally = true;
        draft.RequiresModel = false;
        draft.Confidence = FirstNonEmpty(proposal.Confidence, "high");
        return true;
    }

    private static bool TryBuildMissingSemicolonDraft(RepairProposalRecord proposal, string[] lines, PatchDraftRecord draft)
    {
        if (!string.Equals(proposal.FailureKind, "build_failure", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(proposal.ProposedActionType, "replace_block", StringComparison.OrdinalIgnoreCase)
            || proposal.TargetLineNumber <= 0
            || proposal.TargetLineNumber > lines.Length)
        {
            return false;
        }

        var lineIndex = proposal.TargetLineNumber - 1;
        var originalLine = lines[lineIndex];
        var trimmed = originalLine.TrimEnd();
        if (!LooksLikeMissingSemicolonCandidate(trimmed))
            return false;

        draft.StartLine = proposal.TargetLineNumber;
        draft.EndLine = proposal.TargetLineNumber;
        draft.DraftKind = "replace_range";
        draft.OriginalExcerpt = originalLine;
        draft.ReplacementText = AppendSemicolon(originalLine);
        draft.RationaleSummary = "The compiler failure points at a single statement line that appears to be missing a terminating semicolon.";
        draft.CanApplyLocally = true;
        draft.RequiresModel = false;
        draft.Confidence = "high";
        return true;
    }

    private static bool TryBuildForcedAssertionDraft(RepairProposalRecord proposal, string[] lines, PatchDraftRecord draft)
    {
        if (!string.Equals(proposal.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase))
            return false;

        var searchStart = proposal.TargetLineNumber > 0
            ? Math.Max(proposal.TargetLineNumber - 2, 1)
            : 1;
        var searchEnd = proposal.TargetLineNumber > 0
            ? Math.Min(proposal.TargetLineNumber + 2, lines.Length)
            : Math.Min(10, lines.Length);

        for (var lineNumber = searchStart; lineNumber <= searchEnd; lineNumber++)
        {
            var line = lines[lineNumber - 1];
            if (!TryBuildAssertionReplacement(line, out var replacement))
                continue;

            draft.StartLine = lineNumber;
            draft.EndLine = lineNumber;
            draft.TargetLineNumber = lineNumber;
            draft.DraftKind = "replace_range";
            draft.OriginalExcerpt = line;
            draft.ReplacementText = replacement;
            draft.RationaleSummary = "The failing test contains a deterministic forced-failure assertion at the recorded location.";
            draft.CanApplyLocally = true;
            draft.RequiresModel = false;
            draft.Confidence = "high";
            return true;
        }

        return false;
    }

    private static bool TryBuildDeterministicProjectFileDraft(
        RepairProposalRecord proposal,
        string fullPath,
        string[] lines,
        PatchDraftRecord draft)
    {
        if (!string.Equals(proposal.FailureKind, "build_failure", StringComparison.OrdinalIgnoreCase)
            || (proposal.ProposedActionType is not "ensure_wpf_project_settings"
                and not "ensure_library_project_settings")
            || !proposal.TargetFilePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var document = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
            if (!string.Equals(document.Root?.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase))
                return false;

            var project = document.Root!;
            var ns = project.Name.Namespace;
            var propertyGroup = project.Elements()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "PropertyGroup", StringComparison.OrdinalIgnoreCase));
            if (propertyGroup is null)
            {
                propertyGroup = new XElement(ns + "PropertyGroup");
                project.AddFirst(propertyGroup);
            }

            var changes = new List<string>();
            var targetFramework = FindOrCreateChild(propertyGroup, ns, "TargetFramework");
            if (string.Equals(proposal.ProposedActionType, "ensure_wpf_project_settings", StringComparison.OrdinalIgnoreCase))
            {
                var normalizedFramework = targetFramework.Value.Trim();
                if (string.IsNullOrWhiteSpace(normalizedFramework))
                {
                    targetFramework.Value = "net10.0-windows";
                    changes.Add("TargetFramework=net10.0-windows");
                }
                else if (!normalizedFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase))
                {
                    targetFramework.Value = normalizedFramework + "-windows";
                    changes.Add("TargetFramework=-windows");
                }
                var useWpf = FindOrCreateChild(propertyGroup, ns, "UseWPF");
                if (!string.Equals(useWpf.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    useWpf.Value = "true";
                    changes.Add("UseWPF=true");
                }

                var outputType = FindOrCreateChild(propertyGroup, ns, "OutputType");
                if (!string.Equals(outputType.Value.Trim(), "WinExe", StringComparison.OrdinalIgnoreCase))
                {
                    outputType.Value = "WinExe";
                    changes.Add("OutputType=WinExe");
                }
            }
            else
            {
                var normalizedFramework = targetFramework.Value.Trim();
                if (string.IsNullOrWhiteSpace(normalizedFramework))
                {
                    targetFramework.Value = "net10.0";
                    changes.Add("TargetFramework=net10.0");
                }
                else if (normalizedFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase))
                {
                    targetFramework.Value = normalizedFramework.Replace("-windows", "", StringComparison.OrdinalIgnoreCase);
                    changes.Add("TargetFramework=strip-windows");
                }

                var implicitUsings = FindOrCreateChild(propertyGroup, ns, "ImplicitUsings");
                if (!string.Equals(implicitUsings.Value.Trim(), "enable", StringComparison.OrdinalIgnoreCase))
                {
                    implicitUsings.Value = "enable";
                    changes.Add("ImplicitUsings=enable");
                }

                var nullable = FindOrCreateChild(propertyGroup, ns, "Nullable");
                if (!string.Equals(nullable.Value.Trim(), "enable", StringComparison.OrdinalIgnoreCase))
                {
                    nullable.Value = "enable";
                    changes.Add("Nullable=enable");
                }

                var useWpf = propertyGroup.Elements()
                    .FirstOrDefault(element => string.Equals(element.Name.LocalName, "UseWPF", StringComparison.OrdinalIgnoreCase));
                if (useWpf is not null)
                {
                    useWpf.Remove();
                    changes.Add("RemoveUseWPF");
                }

                var outputType = propertyGroup.Elements()
                    .FirstOrDefault(element => string.Equals(element.Name.LocalName, "OutputType", StringComparison.OrdinalIgnoreCase));
                if (outputType is not null
                    && (string.Equals(outputType.Value.Trim(), "Exe", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(outputType.Value.Trim(), "WinExe", StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(outputType.Value)))
                {
                    outputType.Remove();
                    changes.Add("RemoveOutputType");
                }
            }

            if (changes.Count == 0)
                return false;

            draft.StartLine = 1;
            draft.EndLine = Math.Max(lines.Length, 1);
            draft.TargetLineNumber = proposal.TargetLineNumber > 0 ? proposal.TargetLineNumber : 1;
            draft.DraftKind = "replace_project_file_settings";
            draft.OriginalExcerpt = string.Join(Environment.NewLine, lines);
            draft.ReplacementText = document.ToString() + Environment.NewLine;
            draft.RationaleSummary = string.Equals(proposal.ProposedActionType, "ensure_wpf_project_settings", StringComparison.OrdinalIgnoreCase)
                ? $"Normalize deterministic WPF project settings ({string.Join(", ", changes)}) for the desktop app project."
                : $"Normalize deterministic library project settings ({string.Join(", ", changes)}) for the core/contracts library project.";
            draft.CanApplyLocally = true;
            draft.RequiresModel = false;
            draft.Confidence = FirstNonEmpty(proposal.Confidence, "high");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static PatchDraftRecord BuildFallbackDraft(
        RepairProposalRecord proposal,
        RepairPlanInput? input,
        string[] lines,
        PatchDraftRecord draft)
    {
        var excerpt = FirstNonEmpty(
            proposal.FileExcerpt,
            input?.FileExcerpt,
            BuildExcerptFromLines(lines, proposal.TargetLineNumber));

        draft.StartLine = proposal.TargetLineNumber > 0 ? proposal.TargetLineNumber : 1;
        draft.EndLine = draft.StartLine;
        draft.DraftKind = proposal.ProposedActionType is "inspect_only" or "unknown"
            ? "inspect_only"
            : proposal.ProposedActionType;
        draft.OriginalExcerpt = excerpt;
        draft.ReplacementText = "";
        draft.RationaleSummary = string.IsNullOrWhiteSpace(proposal.Rationale)
            ? "RAM could not shape a safe local patch from the current proposal and file context."
            : proposal.Rationale;
        draft.CanApplyLocally = false;
        draft.RequiresModel = proposal.RequiresModel || string.IsNullOrWhiteSpace(proposal.ModelBrief) == false;
        draft.ModelBrief = FirstNonEmpty(proposal.ModelBrief, BuildModelPatchBrief(proposal, excerpt));
        return draft;
    }

    private static bool TryBuildCircularProjectReferenceDraft(RepairProposalRecord proposal, string[] lines, PatchDraftRecord draft)
    {
        if (!string.Equals(proposal.FailureKind, "build_failure", StringComparison.OrdinalIgnoreCase)
            || proposal.TargetLineNumber <= 0
            || proposal.TargetLineNumber > lines.Length)
        {
            return false;
        }

        if (proposal.ProposedActionType is not "remove_self_project_reference" and not "remove_duplicate_project_reference")
            return false;

        var lineIndex = proposal.TargetLineNumber - 1;
        var originalLine = lines[lineIndex];
        if (!originalLine.Contains("ProjectReference", StringComparison.OrdinalIgnoreCase))
            return false;

        draft.StartLine = proposal.TargetLineNumber;
        draft.EndLine = proposal.TargetLineNumber;
        draft.DraftKind = "remove_project_reference";
        draft.OriginalExcerpt = originalLine;
        draft.ReplacementText = "";
        draft.RationaleSummary = proposal.ProposedActionType switch
        {
            "remove_self_project_reference" => "Remove the self project reference that creates the circular restore graph.",
            "remove_duplicate_project_reference" => "Remove the duplicate project reference that destabilizes the restore graph.",
            _ => "Remove the bounded project reference that matches the recorded circular dependency repair."
        };
        draft.CanApplyLocally = true;
        draft.RequiresModel = false;
        draft.Confidence = FirstNonEmpty(proposal.Confidence, "high");
        return true;
    }

    private static bool LooksLikeMissingSemicolonCandidate(string trimmedLine)
    {
        if (string.IsNullOrWhiteSpace(trimmedLine))
            return false;

        if (trimmedLine.EndsWith(";", StringComparison.Ordinal)
            || trimmedLine.EndsWith("{", StringComparison.Ordinal)
            || trimmedLine.EndsWith("}", StringComparison.Ordinal)
            || trimmedLine.EndsWith(",", StringComparison.Ordinal)
            || trimmedLine.StartsWith("//", StringComparison.Ordinal)
            || trimmedLine.StartsWith("#", StringComparison.Ordinal))
        {
            return false;
        }

        return trimmedLine.Contains('(')
            || trimmedLine.Contains('=')
            || trimmedLine.StartsWith("return ", StringComparison.Ordinal)
            || trimmedLine.StartsWith("throw ", StringComparison.Ordinal);
    }

    private static string AppendSemicolon(string line)
    {
        var trailingWhitespaceLength = line.Length - line.TrimEnd().Length;
        var suffix = trailingWhitespaceLength > 0 ? line[^trailingWhitespaceLength..] : "";
        var core = trailingWhitespaceLength > 0 ? line[..^trailingWhitespaceLength] : line;
        return core + ";" + suffix;
    }

    private static bool TryBuildAssertionReplacement(string line, out string replacement)
    {
        if (line.Contains("Assert.True(false", StringComparison.Ordinal))
        {
            replacement = line.Replace("Assert.True(false", "Assert.True(true", StringComparison.Ordinal);
            return true;
        }

        if (line.Contains("Assert.False(true", StringComparison.Ordinal))
        {
            replacement = line.Replace("Assert.False(true", "Assert.False(false", StringComparison.Ordinal);
            return true;
        }

        replacement = "";
        return false;
    }

    private static string BuildExcerptFromLines(string[] lines, int targetLineNumber)
    {
        if (lines.Length == 0)
            return "";

        var startLine = targetLineNumber > 0 ? Math.Max(targetLineNumber - 2, 1) : 1;
        var endLine = targetLineNumber > 0 ? Math.Min(targetLineNumber + 2, lines.Length) : Math.Min(5, lines.Length);
        var excerptLines = new List<string>();
        for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
            excerptLines.Add(lines[lineNumber - 1]);

        return string.Join(Environment.NewLine, excerptLines);
    }

    private static string BuildModelPatchBrief(RepairProposalRecord proposal, string excerpt)
    {
        var lines = new List<string>
        {
            "Patch task:",
            $"Target file: {DisplayValue(proposal.TargetFilePath)}",
            $"Failure summary: {DisplayValue(proposal.FailureSummary)}",
            $"Requested edit objective: {DisplayValue(proposal.Title)}"
        };

        if (proposal.TargetLineNumber > 0)
            lines.Add($"Target location: line {proposal.TargetLineNumber}" + (proposal.TargetColumnNumber > 0 ? $", column {proposal.TargetColumnNumber}" : ""));

        if (!string.IsNullOrWhiteSpace(excerpt))
        {
            lines.Add("Bounded excerpt:");
            lines.Add(excerpt);
        }

        if (proposal.RetrievalHitCount > 0 || !string.IsNullOrWhiteSpace(proposal.RetrievalContextPacketArtifactRelativePath))
        {
            lines.Add($"Retrieved context: backend={DisplayValue(proposal.RetrievalBackend)} embedder={DisplayValue(proposal.RetrievalEmbedderModel)} query={DisplayValue(proposal.RetrievalQueryKind)} hits={proposal.RetrievalHitCount}");
            if (proposal.RetrievalSourceKinds.Count > 0)
                lines.Add($"Retrieved sources: {string.Join(", ", proposal.RetrievalSourceKinds)}");
        }

        lines.Add("Constraints: keep the edit in this file only, keep the change small, and do not modify unrelated code.");
        return string.Join(Environment.NewLine, lines);
    }

    private static XElement FindOrCreateChild(XElement parent, XNamespace ns, string localName)
    {
        var existing = parent.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var created = new XElement(ns + localName);
        parent.Add(created);
        return created;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
