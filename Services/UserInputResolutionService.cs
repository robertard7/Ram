using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class UserInputResolutionService
{
    private readonly BuildOperationResolutionService _buildOperationResolutionService = new();
    private readonly FileOperationResolutionService _fileOperationResolutionService = new();

    private static readonly Regex[] FileSearchPatterns =
    [
        new(@"\b(?:search|find|look\s+for)\s+(?:for\s+)?files?\s+with\s+(?<pattern>.+?)\s+in\s+the\s+name\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\b(?:search|find|look\s+for)\s+(?:for\s+)?files?\s+named\s+(?<pattern>.+?)(?:\s+in\s+the\s+(?:project|workspace))?[.?!]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    ];

    private static readonly Regex[] TextSearchPatterns =
    [
        new(@"\b(?:search|find|look\s+for)\s+(?:for\s+)?text\s+(?<pattern>.+?)(?:\s+in\s+the\s+(?:project|workspace))?[.?!]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\b(?:search|find|look\s+for)\s+(?<pattern>.+?)\s+in\s+the\s+(?:project|workspace)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    ];

    private static readonly string[] DotnetBuildPhrases =
    [
        "build the project",
        "build project",
        "build the solution",
        "build solution",
        "please build the project",
        "please build the solution",
        "can you build the project",
        "can you build the solution",
        "compile the project",
        "compile project"
    ];

    public ResolvedUserIntent? Resolve(string prompt, BuilderRequestKind requestKind)
    {
        return Resolve(prompt, requestKind, "", "");
    }

    public ResolvedUserIntent? Resolve(string prompt, BuilderRequestKind requestKind, string activeTargetRelativePath)
    {
        return Resolve(prompt, requestKind, activeTargetRelativePath, "");
    }

    public ResolvedUserIntent? Resolve(
        string prompt,
        BuilderRequestKind requestKind,
        string activeTargetRelativePath,
        string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        var trimmed = prompt.Trim();
        var normalized = NormalizeWhitespace(trimmed.ToLowerInvariant());

        if (IsGitStatusRequest(normalized))
            return BuildResolution("git_status", "Resolved directly from user input: git status request.");

        if (IsDotnetBuildRequest(normalized))
            return BuildResolution("dotnet_build", "Resolved directly from user input: dotnet build request.");

        if (IsShowArtifactsRequest(normalized))
            return BuildResolution("show_artifacts", "Resolved directly from user input: show artifacts request.");

        var buildOperationResolution = _buildOperationResolutionService.Resolve(trimmed, activeTargetRelativePath, workspaceRoot);
        if (buildOperationResolution is not null)
        {
            if (requestKind == BuilderRequestKind.BuildRequest
                && IsLocalRepairOperation(buildOperationResolution.ToolRequest.ToolName))
            {
                return null;
            }

            return buildOperationResolution;
        }

        var fileOperationResolution = _fileOperationResolutionService.Resolve(trimmed, activeTargetRelativePath);
        if (fileOperationResolution is not null)
            return fileOperationResolution;

        if (requestKind == BuilderRequestKind.BuildRequest)
            return null;

        if (TryExtractSearchFilesPattern(trimmed, out var filePattern))
        {
            return BuildResolution(
                "search_files",
                $"Resolved directly from user input: filename search for '{filePattern}'.",
                ("pattern", filePattern));
        }

        if (TryExtractSearchTextPattern(trimmed, out var textPattern))
        {
            return BuildResolution(
                "search_text",
                $"Resolved directly from user input: text search for '{textPattern}'.",
                ("pattern", textPattern));
        }

        return null;
    }

    public static bool TryExtractSearchFilesPattern(string prompt, out string pattern)
    {
        foreach (var regex in FileSearchPatterns)
        {
            if (!TryExtractPattern(regex, prompt, out pattern))
                continue;

            return true;
        }

        pattern = "";
        return false;
    }

    public static bool TryExtractSearchTextPattern(string prompt, out string pattern)
    {
        var lowered = prompt.ToLowerInvariant();
        if (lowered.Contains(" file", StringComparison.OrdinalIgnoreCase)
            || lowered.Contains(" files", StringComparison.OrdinalIgnoreCase)
            || lowered.Contains("filename", StringComparison.OrdinalIgnoreCase)
            || lowered.Contains("file name", StringComparison.OrdinalIgnoreCase))
        {
            pattern = "";
            return false;
        }

        foreach (var regex in TextSearchPatterns)
        {
            if (!TryExtractPattern(regex, prompt, out pattern))
                continue;

            return true;
        }

        pattern = "";
        return false;
    }

    private static bool IsGitStatusRequest(string normalizedPrompt)
    {
        return normalizedPrompt == "git status"
            || normalizedPrompt.Contains(" git status", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("show git status", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what is git status", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what's git status", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("check git status", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDotnetBuildRequest(string normalizedPrompt)
    {
        if (normalizedPrompt.Contains("dotnet build", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var phrase in DotnetBuildPhrases)
        {
            if (normalizedPrompt == phrase || normalizedPrompt.StartsWith(phrase + " ", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsShowArtifactsRequest(string normalizedPrompt)
    {
        return normalizedPrompt == "show artifacts"
            || normalizedPrompt == "show the artifacts"
            || normalizedPrompt.Contains(" show artifacts", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("list artifacts", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("show recent artifacts", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractPattern(Regex regex, string prompt, out string pattern)
    {
        var match = regex.Match(prompt ?? "");
        if (!match.Success)
        {
            pattern = "";
            return false;
        }

        pattern = CleanExtractedValue(match.Groups["pattern"].Value);
        return !string.IsNullOrWhiteSpace(pattern);
    }

    private static string CleanExtractedValue(string value)
    {
        var cleaned = (value ?? "").Trim();

        cleaned = Regex.Replace(
            cleaned,
            @"\s+in\s+the\s+(?:project|workspace)\s*$",
            "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        cleaned = cleaned.Trim().TrimEnd('.', '?', '!', ';', ':', ',');

        if ((cleaned.StartsWith('"') && cleaned.EndsWith('"'))
            || (cleaned.StartsWith('\'') && cleaned.EndsWith('\'')))
        {
            cleaned = cleaned[1..^1].Trim();
        }

        if (cleaned.StartsWith("text ", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[5..].Trim();

        if (cleaned.StartsWith("for ", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[4..].Trim();

        return cleaned;
    }

    private static string NormalizeWhitespace(string text)
    {
        return Regex.Replace(text ?? "", @"\s+", " ").Trim();
    }

    private static bool IsLocalRepairOperation(string toolName)
    {
        return string.Equals(toolName, "plan_repair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "preview_patch_draft", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "apply_patch_draft", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "verify_patch_draft", StringComparison.OrdinalIgnoreCase);
    }

    private static ResolvedUserIntent BuildResolution(
        string toolName,
        string reason,
        params (string Key, string Value)[] arguments)
    {
        var request = new ToolRequest
        {
            ToolName = toolName,
            Reason = reason
        };

        foreach (var (key, value) in arguments)
        {
            request.Arguments[key] = value;
        }

        return new ResolvedUserIntent
        {
            ToolRequest = request,
            ResolutionReason = reason
        };
    }
}
