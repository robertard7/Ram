using RAM.Models;

namespace RAM.Services;

public sealed class BuilderRequestClassifier
{
    private static readonly string[] BuildPhrases =
    [
        "build me",
        "make a tool",
        "make tool",
        "update this feature",
        "change this behavior",
        "add support for",
        "wire up ",
        "hook up "
    ];

    private static readonly string[] BuildLeadPhrases =
    [
        "build ",
        "create ",
        "implement ",
        "extend ",
        "please build ",
        "please create ",
        "please implement ",
        "please extend ",
        "can you build ",
        "can you create ",
        "can you implement ",
        "can you extend "
    ];

    private static readonly string[] CapabilityChangePhrases =
    [
        "add ",
        "modify ",
        "update ",
        "change ",
        "extend ",
        "can you add ",
        "can you modify ",
        "can you update ",
        "can you change ",
        "please add ",
        "please modify ",
        "please update ",
        "please change "
    ];

    private static readonly string[] CapabilityTargets =
    [
        " tool",
        " feature",
        " capability",
        " support",
        " behavior",
        " workflow",
        " routing",
        " prompt",
        " registry",
        " builder",
        " ui",
        " storage"
    ];

    private static readonly string[] ToolPhrases =
    [
        "read ",
        "open ",
        "inspect ",
        "show ",
        "list ",
        "what's in",
        "what is in",
        "look at ",
        "display ",
        "check "
    ];

    private static readonly string[] ToolTargets =
    [
        " file",
        " folder",
        " directory",
        " path",
        " workspace",
        " artifact",
        " artifacts",
        " memory",
        " project",
        " projects",
        " solution",
        " solutions",
        " test",
        " tests",
        " diff",
        " git",
        ".txt",
        ".md",
        ".cs",
        ".sln",
        ".csproj",
        ".xaml",
        ".json"
    ];

    public BuilderRequestKind Classify(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return BuilderRequestKind.NormalQuestion;

        var text = Normalize(prompt);
        var trimmed = prompt.Trim().ToLowerInvariant();

        if (ContainsAny(text, BuildPhrases)
            || StartsWithAny(trimmed, BuildLeadPhrases)
            || (StartsWithAny(trimmed, CapabilityChangePhrases) && ContainsAny(text, CapabilityTargets)))
            return BuilderRequestKind.BuildRequest;

        if (ContainsAny(text, ToolPhrases) && ContainsAny(text, ToolTargets))
            return BuilderRequestKind.ToolLikely;

        if (LooksLikeWorkspacePath(text))
            return BuilderRequestKind.ToolLikely;

        return BuilderRequestKind.NormalQuestion;
    }

    private static string Normalize(string prompt)
    {
        return $" {prompt.Trim().ToLowerInvariant()} ";
    }

    private static bool ContainsAny(string text, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (text.Contains(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool LooksLikeWorkspacePath(string text)
    {
        return text.Contains(@":\", StringComparison.OrdinalIgnoreCase)
            || text.Contains("./", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".\\", StringComparison.OrdinalIgnoreCase)
            || text.Contains('/', StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWithAny(string text, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (text.StartsWith(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
