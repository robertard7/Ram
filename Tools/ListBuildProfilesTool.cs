using RAM.Models;

namespace RAM.Tools;

public sealed class ListBuildProfilesTool
{
    public string Format(IReadOnlyList<WorkspaceBuildProfileRecord> profiles)
    {
        if (profiles.Count == 0)
            return "No build profiles were detected in the current workspace.";

        var lines = new List<string>
        {
            "Workspace build profiles:"
        };

        foreach (var profile in profiles)
        {
            lines.Add($"- {profile.BuildSystemType} target={DisplayValue(profile.PrimaryTargetPath)} confidence={DisplayValue(profile.Confidence)} preferred={(profile.PreferredProfile ? "yes" : "no")}");
            lines.Add($"  build={DisplayValue(profile.BuildToolFamily)} configure={DisplayValue(profile.ConfigureToolFamily)} test={DisplayValue(profile.TestToolFamily)}");
            if (!string.IsNullOrWhiteSpace(profile.BuildDirectoryPath))
                lines.Add($"  build_dir={profile.BuildDirectoryPath}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
