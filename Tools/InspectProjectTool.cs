using System.IO;
using System.Xml.Linq;
using RAM.Models;

namespace RAM.Tools;

public sealed class InspectProjectTool
{
    public string Inspect(string workspaceRoot, WorkspaceBuildItem item, string resolutionMessage)
    {
        var fullPath = Path.Combine(workspaceRoot, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var lines = new List<string>
        {
            "Project inspection:",
            $"Resolution: {resolutionMessage}",
            $"Resolved path: {item.RelativePath}",
            $"Type: {item.ItemType}",
            $"File name: {item.FileName}",
            $"Parent directory: {item.ParentDirectory}",
            $"Language/runtime: {item.LanguageHint}",
            $"Likely test project: {(item.LikelyTestProject ? "yes" : "no")}"
        };

        if (item.ItemType == "solution")
        {
            var includedProjects = CountSolutionProjects(fullPath);
            lines.Add($"Included project count: {includedProjects}");
            lines.Add("Build command candidates:");
            lines.Add($"- dotnet build \"{item.RelativePath}\"");
            lines.Add("Test command candidates:");
            lines.Add($"- dotnet test \"{item.RelativePath}\"");
            return string.Join(Environment.NewLine, lines);
        }

        if (item.ItemType == "project")
        {
            var details = ReadProjectDetails(fullPath);
            lines.Add($"Target framework(s): {details.TargetFrameworks}");
            lines.Add($"Package references: {details.PackageReferenceCount}");
            lines.Add($"Project references: {details.ProjectReferenceCount}");
            lines.Add("Build command candidates:");
            lines.Add($"- dotnet build \"{item.RelativePath}\"");
            lines.Add("Test command candidates:");
            lines.Add(item.LikelyTestProject
                ? $"- dotnet test \"{item.RelativePath}\""
                : "- none suggested from local test heuristics");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static int CountSolutionProjects(string fullPath)
    {
        if (!File.Exists(fullPath))
            return 0;

        return File.ReadLines(fullPath)
            .Count(line => line.Contains(".csproj", StringComparison.OrdinalIgnoreCase));
    }

    private static ProjectDetails ReadProjectDetails(string fullPath)
    {
        if (!File.Exists(fullPath))
            return new ProjectDetails();

        try
        {
            var document = XDocument.Load(fullPath);
            var frameworks = document.Descendants()
                .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .Select(element => (element.Value ?? "").Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            return new ProjectDetails
            {
                TargetFrameworks = frameworks.Count == 0 ? "(not detected)" : string.Join(", ", frameworks),
                PackageReferenceCount = document.Descendants().Count(element => element.Name.LocalName == "PackageReference"),
                ProjectReferenceCount = document.Descendants().Count(element => element.Name.LocalName == "ProjectReference")
            };
        }
        catch
        {
            return new ProjectDetails
            {
                TargetFrameworks = "(could not parse project XML)"
            };
        }
    }

    private sealed class ProjectDetails
    {
        public string TargetFrameworks { get; set; } = "(not detected)";
        public int PackageReferenceCount { get; set; }
        public int ProjectReferenceCount { get; set; }
    }
}
