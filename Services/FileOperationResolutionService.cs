using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class FileOperationResolutionService
{
    private static readonly Regex ReadFirstLinesPattern = new(
        @"^\s*(?:please\s+|can\s+you\s+)?(?:show|read)\s+(?:me\s+)?the\s+first\s+(?<count>\d+)\s+lines?\s+(?:of|from)\s+(?<target>.+?)[.?!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ReadLineRangePattern = new(
        @"^\s*(?:please\s+|can\s+you\s+)?(?:show|read)\s+(?:me\s+)?lines?\s+(?<start>\d+)\s*(?:to|-)\s*(?<end>\d+)\s+(?:of|from)\s+(?<target>.+?)[.?!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FileInfoPattern = new(
        @"^\s*(?:please\s+|can\s+you\s+)?(?:show|get|display|read)\s+(?:me\s+)?file\s+info\s+(?:for\s+)?(?<target>.+?)[.?!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CreateNamedWithWritePattern = new(
        @"^\s*(?:please\s+|can\s+you\s+)?create\s+(?:a\s+)?file\s+named\s+(?<path>(""[^""]+""|'[^']+'|[^\s]+))\s+and\s+write\s+(?<content>.+?)\s+(?:into|to)\s+it[.?!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CreateFileWithContentPattern = new(
        @"^\s*(?:please\s+|can\s+you\s+)?create\s+(?:a\s+)?file\s+(?<path>(""[^""]+""|'[^']+'|[^\s]+))\s+with\s+(?<content>.+?)[.?!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CreateNamedOnlyPattern = new(
        @"^\s*(?:please\s+|can\s+you\s+)?create\s+(?:a\s+)?file\s+named\s+(?<path>(""[^""]+""|'[^']+'|[^\s]+))[.?!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WriteFilePattern = new(
        @"^\s*(?:please\s+|can\s+you\s+)?write\s+(?<content>.+?)\s+(?:to|into)\s+(?<path>(""[^""]+""|'[^']+'|[^\s]+))[.?!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AppendFilePattern = new(
        @"^\s*(?:please\s+|can\s+you\s+)?append\s+(?<content>.+?)\s+to\s+(?<target>.+?)[.?!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ReplaceInFilePattern = new(
        @"^\s*(?:please\s+|can\s+you\s+)?replace\s+(?<oldText>.+?)\s+with\s+(?<newText>.+?)\s+in\s+(?<target>.+?)[.?!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MakeDirectoryPattern = new(
        @"^\s*(?:please\s+|can\s+you\s+)?(?:make|create)\s+(?:a\s+)?(?:folder|directory)\s+(?:named\s+)?(?<path>(""[^""]+""|'[^']+'|[^\s]+))[.?!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public ResolvedUserIntent? Resolve(string prompt, string activeTargetRelativePath)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        if (TryResolveCreateFile(prompt, out var createResolution))
            return createResolution;

        if (TryResolveWriteFile(prompt, activeTargetRelativePath, out var writeResolution))
            return writeResolution;

        if (TryResolveAppendFile(prompt, activeTargetRelativePath, out var appendResolution))
            return appendResolution;

        if (TryResolveReplaceInFile(prompt, activeTargetRelativePath, out var replaceResolution))
            return replaceResolution;

        if (TryResolveReadFileChunk(prompt, activeTargetRelativePath, out var readChunkResolution))
            return readChunkResolution;

        if (TryResolveFileInfo(prompt, activeTargetRelativePath, out var fileInfoResolution))
            return fileInfoResolution;

        if (TryResolveMakeDirectory(prompt, out var makeDirectoryResolution))
            return makeDirectoryResolution;

        return null;
    }

    private static bool TryResolveCreateFile(string prompt, out ResolvedUserIntent? resolution)
    {
        if (TryBuildFileResolution(CreateNamedWithWritePattern, prompt, "create_file", out resolution))
            return true;

        if (TryBuildFileResolution(CreateFileWithContentPattern, prompt, "create_file", out resolution))
            return true;

        if (!TryBuildFileResolution(CreateNamedOnlyPattern, prompt, "create_file", out resolution))
            return false;

        return true;
    }

    private static bool TryResolveWriteFile(string prompt, string activeTargetRelativePath, out ResolvedUserIntent? resolution)
    {
        var match = WriteFilePattern.Match(prompt ?? "");
        if (!match.Success)
        {
            resolution = null;
            return false;
        }

        var path = ResolvePathTarget(match.Groups["path"].Value, activeTargetRelativePath);
        var content = CleanContent(match.Groups["content"].Value);
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(content))
        {
            resolution = null;
            return false;
        }

        resolution = BuildResolution(
            "write_file",
            $"Resolved write_file request locally for {path}.",
            ("path", path),
            ("content", content));
        return true;
    }

    private static bool TryResolveAppendFile(string prompt, string activeTargetRelativePath, out ResolvedUserIntent? resolution)
    {
        var match = AppendFilePattern.Match(prompt ?? "");
        if (!match.Success)
        {
            resolution = null;
            return false;
        }

        var content = CleanContent(match.Groups["content"].Value);
        var target = CleanTarget(match.Groups["target"].Value);
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(target))
        {
            resolution = null;
            return false;
        }

        var path = ResolvePathTarget(target, activeTargetRelativePath);

        if (string.IsNullOrWhiteSpace(path))
        {
            resolution = null;
            return false;
        }

        resolution = BuildResolution(
            "append_file",
            $"Resolved append_file request locally for {path}.",
            ("path", path),
            ("content", content));
        return true;
    }

    private static bool TryResolveReplaceInFile(string prompt, string activeTargetRelativePath, out ResolvedUserIntent? resolution)
    {
        var match = ReplaceInFilePattern.Match(prompt ?? "");
        if (!match.Success)
        {
            resolution = null;
            return false;
        }

        var oldText = CleanContent(match.Groups["oldText"].Value);
        var newText = CleanContent(match.Groups["newText"].Value);
        var path = ResolvePathTarget(match.Groups["target"].Value, activeTargetRelativePath);
        if (string.IsNullOrWhiteSpace(oldText)
            || string.IsNullOrWhiteSpace(newText)
            || string.IsNullOrWhiteSpace(path))
        {
            resolution = null;
            return false;
        }

        resolution = BuildResolution(
            "replace_in_file",
            $"Resolved replace_in_file request locally for {path}.",
            ("path", path),
            ("old_text", oldText),
            ("new_text", newText));
        return true;
    }

    private static bool TryResolveReadFileChunk(string prompt, string activeTargetRelativePath, out ResolvedUserIntent? resolution)
    {
        var firstLinesMatch = ReadFirstLinesPattern.Match(prompt ?? "");
        if (firstLinesMatch.Success)
        {
            var path = ResolvePathTarget(firstLinesMatch.Groups["target"].Value, activeTargetRelativePath);
            if (string.IsNullOrWhiteSpace(path))
            {
                resolution = null;
                return false;
            }

            var lineCount = ParsePositiveInt(firstLinesMatch.Groups["count"].Value, 20);
            resolution = BuildResolution(
                "read_file_chunk",
                $"Resolved read_file_chunk request locally for {path}.",
                ("path", path),
                ("start_line", "1"),
                ("line_count", lineCount.ToString()));
            return true;
        }

        var lineRangeMatch = ReadLineRangePattern.Match(prompt ?? "");
        if (!lineRangeMatch.Success)
        {
            resolution = null;
            return false;
        }

        var rangePath = ResolvePathTarget(lineRangeMatch.Groups["target"].Value, activeTargetRelativePath);
        var startLine = ParsePositiveInt(lineRangeMatch.Groups["start"].Value, 1);
        var endLine = ParsePositiveInt(lineRangeMatch.Groups["end"].Value, startLine);
        if (string.IsNullOrWhiteSpace(rangePath))
        {
            resolution = null;
            return false;
        }

        if (endLine < startLine)
        {
            var temp = startLine;
            startLine = endLine;
            endLine = temp;
        }

        resolution = BuildResolution(
            "read_file_chunk",
            $"Resolved read_file_chunk request locally for {rangePath}.",
            ("path", rangePath),
            ("start_line", startLine.ToString()),
            ("line_count", (endLine - startLine + 1).ToString()));
        return true;
    }

    private static bool TryResolveFileInfo(string prompt, string activeTargetRelativePath, out ResolvedUserIntent? resolution)
    {
        var match = FileInfoPattern.Match(prompt ?? "");
        if (!match.Success)
        {
            resolution = null;
            return false;
        }

        var path = ResolvePathTarget(match.Groups["target"].Value, activeTargetRelativePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            resolution = null;
            return false;
        }

        resolution = BuildResolution(
            "file_info",
            $"Resolved file_info request locally for {path}.",
            ("path", path));
        return true;
    }

    private static bool TryResolveMakeDirectory(string prompt, out ResolvedUserIntent? resolution)
    {
        var match = MakeDirectoryPattern.Match(prompt ?? "");
        if (!match.Success)
        {
            resolution = null;
            return false;
        }

        var path = CleanPath(match.Groups["path"].Value);
        if (string.IsNullOrWhiteSpace(path))
        {
            resolution = null;
            return false;
        }

        resolution = BuildResolution(
            "make_dir",
            $"Resolved make_dir request locally for {path}.",
            ("path", path));
        return true;
    }

    private static bool TryBuildFileResolution(Regex regex, string prompt, string toolName, out ResolvedUserIntent? resolution)
    {
        var match = regex.Match(prompt ?? "");
        if (!match.Success)
        {
            resolution = null;
            return false;
        }

        var path = CleanPath(match.Groups["path"].Value);
        if (string.IsNullOrWhiteSpace(path))
        {
            resolution = null;
            return false;
        }

        var arguments = new List<(string Key, string Value)>
        {
            ("path", path)
        };

        if (match.Groups["content"].Success)
        {
            var content = CleanContent(match.Groups["content"].Value);
            if (string.IsNullOrWhiteSpace(content))
            {
                resolution = null;
                return false;
            }

            arguments.Add(("content", content));
        }

        resolution = BuildResolution(
            toolName,
            $"Resolved {toolName} request locally for {path}.",
            arguments.ToArray());
        return true;
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

    private static string CleanPath(string value)
    {
        var cleaned = (value ?? "").Trim().TrimEnd('.', '?', '!', ';', ':', ',');

        if ((cleaned.StartsWith('"') && cleaned.EndsWith('"'))
            || (cleaned.StartsWith('\'') && cleaned.EndsWith('\'')))
        {
            cleaned = cleaned[1..^1].Trim();
        }

        return cleaned;
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

    private static string CleanTarget(string value)
    {
        return (value ?? "").Trim().TrimEnd('.', '?', '!', ';', ':', ',');
    }

    private static string ResolvePathTarget(string value, string activeTargetRelativePath)
    {
        var target = CleanTarget(value);
        if (string.IsNullOrWhiteSpace(target))
            return "";

        return IsCurrentFileReference(target)
            ? activeTargetRelativePath
            : CleanPath(target);
    }

    private static bool IsCurrentFileReference(string target)
    {
        var normalized = $" {target.Trim().ToLowerInvariant()} ";
        return normalized.Contains(" that file ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" this file ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" it ", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParsePositiveInt(string value, int fallback)
    {
        return int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }
}
