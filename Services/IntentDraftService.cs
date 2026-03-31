using System.IO;
using System.Text;
using RAM.Models;

namespace RAM.Services;

public sealed class IntentDraftService
{
    public IntentDraft CreateDraft(string prompt, string workspaceRoot)
    {
        var cleaned = Normalize(prompt);
        var projectName = InferProjectName(workspaceRoot);
        var targetStack = InferTargetStack(workspaceRoot);

        return new IntentDraft
        {
            Title = BuildTitle(cleaned),
            Objective = cleaned,
            ProjectName = projectName,
            TargetStack = targetStack,
            ImplementationDirection = InferImplementationDirection(cleaned, projectName, targetStack, workspaceRoot),
            OpenQuestions = BuildOpenQuestions(cleaned)
        };
    }

    public bool IsClearEnough(IntentDraft draft)
    {
        if (draft is null)
            return false;

        return !string.IsNullOrWhiteSpace(draft.Objective)
            && draft.Objective.Trim().Length >= 16;
    }

    public string BuildBuilderResponse(IntentDraft draft, bool intentSaved)
    {
        var sb = new StringBuilder();
        var targetName = string.IsNullOrWhiteSpace(draft.ProjectName)
            ? "the current workspace app"
            : $"the current {draft.ProjectName} app";

        sb.AppendLine($"This looks like a new feature request for {targetName}, so I treated it as a builder request instead of a tool call.");
        sb.AppendLine();
        sb.AppendLine($"Intent candidate: {draft.Title}");
        sb.AppendLine($"Objective: {draft.Objective}");

        if (!string.IsNullOrWhiteSpace(draft.TargetStack))
        {
            sb.AppendLine($"Likely stack: {draft.TargetStack}");
        }

        if (!string.IsNullOrWhiteSpace(draft.ImplementationDirection))
        {
            sb.AppendLine($"Implementation direction: {draft.ImplementationDirection}");
        }

        if (intentSaved)
        {
            sb.AppendLine("Current intent was updated from this request.");
        }

        if (draft.OpenQuestions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Open questions:");

            foreach (var question in draft.OpenQuestions.Take(2))
            {
                sb.AppendLine($"- {question}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    public string BuildUnknownToolFallbackResponse(string missingToolName, IntentDraft draft, bool intentSaved)
    {
        var builderResponse = BuildBuilderResponse(draft, intentSaved);
        return $"The requested tool `{missingToolName}` is not in the current registry. I switched to builder mode instead.{Environment.NewLine}{Environment.NewLine}{builderResponse}";
    }

    private static string Normalize(string prompt)
    {
        return (prompt ?? "")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }

    private static string BuildTitle(string text)
    {
        const int max = 48;

        if (text.Length <= max)
            return text;

        return text[..max].TrimEnd() + "...";
    }

    private static string InferProjectName(string workspaceRoot)
    {
        var csproj = FindFirstRelevantFile(workspaceRoot, path =>
            string.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase));

        return csproj is null
            ? ""
            : Path.GetFileNameWithoutExtension(csproj);
    }

    private static string InferTargetStack(string workspaceRoot)
    {
        var csproj = FindFirstRelevantFile(workspaceRoot, path =>
            string.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase));

        var hasXaml = FindFirstRelevantFile(workspaceRoot, path =>
            string.Equals(Path.GetExtension(path), ".xaml", StringComparison.OrdinalIgnoreCase)) is not null;

        var hasPackageJson = FindFirstRelevantFile(workspaceRoot, path =>
            string.Equals(Path.GetFileName(path), "package.json", StringComparison.OrdinalIgnoreCase)) is not null;

        var csprojText = csproj is null || !File.Exists(csproj)
            ? ""
            : SafeReadText(csproj);

        var hasSqlite = csprojText.Contains("Microsoft.Data.Sqlite", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(workspaceRoot, ".ram", "ram.db"));

        if (csproj is not null && hasXaml)
            return hasSqlite ? "C# / WPF with SQLite already in the project" : "C# / WPF";

        if (csproj is not null)
            return hasSqlite ? "C# / .NET with SQLite already in the project" : "C# / .NET";

        if (hasPackageJson)
            return "JavaScript / TypeScript app";

        return "";
    }

    private static string InferImplementationDirection(string objective, string projectName, string targetStack, string workspaceRoot)
    {
        var prefix = string.IsNullOrWhiteSpace(projectName)
            ? "This appears to target the current workspace app."
            : $"This appears to target the current {projectName} app.";

        if (objective.Contains("revision", StringComparison.OrdinalIgnoreCase)
            || objective.Contains("history", StringComparison.OrdinalIgnoreCase)
            || objective.Contains("version", StringComparison.OrdinalIgnoreCase))
        {
            return $"{prefix} A good direction is to add revision rows in `.ram/ram.db`, link them to saved artifacts, and update the existing save flow to record revision metadata.";
        }

        if (objective.Contains("tool", StringComparison.OrdinalIgnoreCase))
        {
            var stackNote = string.IsNullOrWhiteSpace(targetStack)
                ? "through the current app"
                : $"through the current {targetStack} app";

            return $"{prefix} A good direction is to add the behavior {stackNote}, then register it in the tool registry only if it becomes an executable workspace tool.";
        }

        if (!string.IsNullOrWhiteSpace(targetStack))
            return $"{prefix} A good direction is to implement it in the existing {targetStack} code path and persist any durable state in the workspace SQLite database when needed.";

        return $"{prefix} A good direction is to implement the feature in the existing workspace codebase before deciding whether it should become an executable tool.";
    }

    private static List<string> BuildOpenQuestions(string objective)
    {
        var questions = new List<string>();
        var text = objective.ToLowerInvariant();

        if (text.Contains("revision") || text.Contains("revisions") || text.Contains("history"))
        {
            if (!text.Contains("automatic") && !text.Contains("every save"))
            {
                questions.Add("Should every save create a revision automatically?");
            }

            if (!text.Contains("artifact id") && !text.Contains("file path") && !text.Contains("both"))
            {
                questions.Add("Should revisions be tied to artifact id, file path, or both?");
            }
        }

        return questions;
    }

    private static string? FindFirstRelevantFile(string workspaceRoot, Func<string, bool> predicate)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return null;

        var pending = new Stack<string>();
        pending.Push(workspaceRoot);
        var examinedFiles = 0;

        while (pending.Count > 0 && examinedFiles < 500)
        {
            var current = pending.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                examinedFiles++;
                if (predicate(file))
                    return file;

                if (examinedFiles >= 500)
                    break;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories)
            {
                var name = Path.GetFileName(directory);
                if (IsIgnoredDirectory(name))
                    continue;

                pending.Push(directory);
            }
        }

        return null;
    }

    private static bool IsIgnoredDirectory(string name)
    {
        return string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, ".ram", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return "";
        }
    }
}
