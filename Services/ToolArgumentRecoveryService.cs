using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class ToolArgumentRecoveryService
{
    private static readonly ArtifactClassificationService ArtifactClassificationService = new();
    private static readonly Regex QuotedPathPattern = new(@"[""`'](?<path>[^""`']+\.[A-Za-z0-9]+)[""`']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BarePathPattern = new(@"(?<path>(?:\.{0,2}[\\/])?[\w\-.\\/]+\.[A-Za-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AppendContentPattern = new(@"^\s*(?:please\s+|can\s+you\s+)?append\s+(?<content>.+?)\s+to\s+.+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ReplaceTextPattern = new(@"^\s*(?:please\s+|can\s+you\s+)?replace\s+(?<oldText>.+?)\s+with\s+(?<newText>.+?)\s+in\s+.+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WriteContentPattern = new(@"^\s*(?:please\s+|can\s+you\s+)?write\s+(?<content>.+?)\s+to\s+.+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public ToolArgumentRecoveryOutcome Recover(
        ToolRequest request,
        string userPrompt,
        string activeTargetRelativePath,
        ArtifactRecord? activeArtifact)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        var recoveredRequest = request.Clone();
        recoveredRequest.ToolName = NormalizeToolName(recoveredRequest.ToolName);

        var notes = new List<string>();
        var failureMessage = "";

        switch (recoveredRequest.ToolName)
        {
            case "read_file":
            case "file_info":
            case "read_file_chunk":
            case "inspect_project":
                RecoverPathArgument(recoveredRequest, userPrompt, activeTargetRelativePath, activeArtifact, notes, ref failureMessage);
                break;

            case "write_file":
                RecoverPathArgument(recoveredRequest, userPrompt, activeTargetRelativePath, activeArtifact, notes, ref failureMessage);
                RecoverContentArgument(recoveredRequest, userPrompt, WriteContentPattern, notes);
                break;

            case "plan_repair":
            case "preview_patch_draft":
            case "apply_patch_draft":
            case "verify_patch_draft":
                RecoverRepairLoopPathArgument(recoveredRequest, userPrompt, notes);
                break;

            case "append_file":
                RecoverPathArgument(recoveredRequest, userPrompt, activeTargetRelativePath, activeArtifact, notes, ref failureMessage);
                RecoverContentArgument(recoveredRequest, userPrompt, AppendContentPattern, notes);
                break;

            case "replace_in_file":
                RecoverPathArgument(recoveredRequest, userPrompt, activeTargetRelativePath, activeArtifact, notes, ref failureMessage);
                RecoverReplaceTextArguments(recoveredRequest, userPrompt, notes);
                break;

            case "search_files":
                RecoverSearchPattern(recoveredRequest, userPrompt, notes, UserInputResolutionService.TryExtractSearchFilesPattern);
                break;

            case "search_text":
                RecoverSearchPattern(recoveredRequest, userPrompt, notes, UserInputResolutionService.TryExtractSearchTextPattern);
                break;
        }

        return new ToolArgumentRecoveryOutcome
        {
            Request = recoveredRequest,
            Notes = notes,
            FailureMessage = failureMessage
        };
    }

    private static void RecoverPathArgument(
        ToolRequest request,
        string userPrompt,
        string activeTargetRelativePath,
        ArtifactRecord? activeArtifact,
        List<string> notes,
        ref string failureMessage)
    {
        if (request.TryGetArgument("path", out _))
            return;

        if (TryExtractLikelyPath(userPrompt, out var extractedPath))
        {
            request.Arguments["path"] = extractedPath;
            notes.Add($"Recovered path from user request: {extractedPath}");
            return;
        }

        var fallbackPath = FirstNonEmpty(activeTargetRelativePath, GetRecoverableArtifactPath(activeArtifact));
        if (!string.IsNullOrWhiteSpace(fallbackPath))
        {
            request.Arguments["path"] = fallbackPath;
            notes.Add($"Recovered active target path: {fallbackPath}");
            return;
        }

        if (ReferencesCurrentFile(userPrompt))
        {
            failureMessage =
                $"{request.ToolName} failed: missing required argument 'path' and there is no active file to resolve 'that file'." +
                Environment.NewLine +
                "No active file is set. Say `append hello to notes/test.txt` or create/read a file first.";
        }
    }

    private static void RecoverOptionalPathArgument(
        ToolRequest request,
        string userPrompt,
        string activeTargetRelativePath,
        ArtifactRecord? activeArtifact,
        List<string> notes)
    {
        if (request.TryGetArgument("path", out _))
            return;

        if (TryExtractLikelyPath(userPrompt, out var extractedPath))
        {
            request.Arguments["path"] = extractedPath;
            notes.Add($"Recovered path from user request: {extractedPath}");
            return;
        }

        var fallbackPath = FirstNonEmpty(activeTargetRelativePath, GetRecoverableArtifactPath(activeArtifact));
        if (string.IsNullOrWhiteSpace(fallbackPath))
            return;

        request.Arguments["path"] = fallbackPath;
        notes.Add($"Recovered active target path: {fallbackPath}");
    }

    private static void RecoverRepairLoopPathArgument(
        ToolRequest request,
        string userPrompt,
        List<string> notes)
    {
        if (request.TryGetArgument("path", out _))
            return;

        if (!TryExtractLikelyPath(userPrompt, out var extractedPath))
            return;

        request.Arguments["path"] = extractedPath;
        notes.Add($"Recovered path from user request: {extractedPath}");
    }

    private static void RecoverContentArgument(
        ToolRequest request,
        string userPrompt,
        Regex regex,
        List<string> notes)
    {
        if (request.TryGetArgument("content", out _))
            return;

        var match = regex.Match(userPrompt ?? "");
        if (!match.Success)
            return;

        var content = CleanContent(match.Groups["content"].Value);
        if (string.IsNullOrWhiteSpace(content))
            return;

        request.Arguments["content"] = content;
        notes.Add("Recovered content from user request.");
    }

    private static void RecoverSearchPattern(
        ToolRequest request,
        string userPrompt,
        List<string> notes,
        TryExtractPatternDelegate extractPattern)
    {
        if (request.TryGetArgument("pattern", out _))
            return;

        if (!extractPattern(userPrompt ?? "", out var pattern))
            return;

        request.Arguments["pattern"] = pattern;
        notes.Add($"Recovered search pattern: {pattern}");
    }

    private static void RecoverReplaceTextArguments(
        ToolRequest request,
        string userPrompt,
        List<string> notes)
    {
        if (request.TryGetArgument("old_text", out _) && request.TryGetArgument("new_text", out _))
            return;

        var match = ReplaceTextPattern.Match(userPrompt ?? "");
        if (!match.Success)
            return;

        if (!request.TryGetArgument("old_text", out _))
        {
            var oldText = CleanContent(match.Groups["oldText"].Value);
            if (!string.IsNullOrWhiteSpace(oldText))
            {
                request.Arguments["old_text"] = oldText;
                notes.Add("Recovered old_text from user request.");
            }
        }

        if (!request.TryGetArgument("new_text", out _))
        {
            var newText = CleanContent(match.Groups["newText"].Value);
            if (!string.IsNullOrWhiteSpace(newText))
            {
                request.Arguments["new_text"] = newText;
                notes.Add("Recovered new_text from user request.");
            }
        }
    }

    private static bool TryExtractLikelyPath(string prompt, out string path)
    {
        var quoted = QuotedPathPattern.Match(prompt ?? "");
        if (quoted.Success)
        {
            path = quoted.Groups["path"].Value.Trim();
            return !string.IsNullOrWhiteSpace(path);
        }

        var bare = BarePathPattern.Match(prompt ?? "");
        if (bare.Success)
        {
            path = bare.Groups["path"].Value.Trim();
            return !string.IsNullOrWhiteSpace(path);
        }

        path = "";
        return false;
    }

    private static string CleanContent(string value)
    {
        var cleaned = (value ?? "").Trim().TrimEnd('.', '?', '!');

        if ((cleaned.StartsWith('"') && cleaned.EndsWith('"'))
            || (cleaned.StartsWith('\'') && cleaned.EndsWith('\'')))
        {
            cleaned = cleaned[1..^1].Trim();
        }

        return cleaned;
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

    private static string GetRecoverableArtifactPath(ArtifactRecord? activeArtifact)
    {
        return ArtifactClassificationService.IsFileBackedArtifact(activeArtifact)
            ? activeArtifact!.RelativePath
            : "";
    }

    private static bool ReferencesCurrentFile(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var normalized = $" {prompt.Trim().ToLowerInvariant()} ";
        return normalized.Contains(" that file ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" this file ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" append to it ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" write it ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" read it ", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(" to it ", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(" it ", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeToolName(string toolName)
    {
        return (toolName ?? "").Trim().ToLowerInvariant();
    }

    public delegate bool TryExtractPatternDelegate(string prompt, out string pattern);
}

public sealed class ToolArgumentRecoveryOutcome
{
    public ToolRequest Request { get; set; } = new();
    public List<string> Notes { get; set; } = [];
    public string FailureMessage { get; set; } = "";
}
