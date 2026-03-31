using RAM.Models;
using RAM.Services;

namespace RAM.Tools;

public sealed class ListProjectsTool
{
    private readonly WorkspaceBuildIndexService _workspaceBuildIndexService;

    public ListProjectsTool(WorkspaceBuildIndexService workspaceBuildIndexService)
    {
        _workspaceBuildIndexService = workspaceBuildIndexService;
    }

    public string List(string workspaceRoot, string kind)
    {
        var items = FilterItems(_workspaceBuildIndexService.ListItems(workspaceRoot), kind);
        if (items.Count == 0)
        {
            return kind == "test_projects"
                ? "No likely test projects were found in the current workspace."
                : "No build-relevant files were found in the current workspace.";
        }

        var lines = new List<string>
        {
            $"Workspace projects ({NormalizeKind(kind)}):"
        };

        foreach (var item in items)
        {
            var testLabel = item.LikelyTestProject ? ", test" : "";
            lines.Add($"- {item.RelativePath} [{item.ItemType}{testLabel}] dir={item.ParentDirectory} hint={item.LanguageHint}");
        }

        var solutions = items.Count(item => item.ItemType == "solution");
        var projects = items.Count(item => item.ItemType == "project");
        var testProjects = items.Count(item => item.ItemType == "project" && item.LikelyTestProject);
        lines.Add($"Summary: {items.Count} item(s), {solutions} solution(s), {projects} project(s), {testProjects} likely test project(s).");

        return string.Join(Environment.NewLine, lines);
    }

    private static List<WorkspaceBuildItem> FilterItems(IReadOnlyList<WorkspaceBuildItem> items, string kind)
    {
        var normalizedKind = NormalizeKind(kind);
        return normalizedKind switch
        {
            "solutions" => items.Where(item => item.ItemType == "solution").ToList(),
            "projects" => items.Where(item => item.ItemType == "project").ToList(),
            "test_projects" => items.Where(item => item.ItemType == "project" && item.LikelyTestProject).ToList(),
            _ => items.ToList()
        };
    }

    private static string NormalizeKind(string kind)
    {
        return string.IsNullOrWhiteSpace(kind) ? "all" : kind.Trim().ToLowerInvariant();
    }
}
