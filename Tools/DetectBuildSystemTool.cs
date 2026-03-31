using RAM.Models;

namespace RAM.Tools;

public sealed class DetectBuildSystemTool
{
    public string Format(BuildSystemDetectionResult detection)
    {
        var lines = new List<string>
        {
            "Build system detection:",
            $"Detected type: {detection.DetectedType}",
            $"Confidence: {DisplayValue(detection.Confidence)}",
            $"Summary: {detection.Summary}"
        };

        if (detection.PreferredProfile is not null)
        {
            lines.Add($"Preferred profile: {detection.PreferredProfile.BuildSystemType}");
            lines.Add($"Primary target: {DisplayValue(detection.PreferredProfile.PrimaryTargetPath)}");
            lines.Add($"Build tool: {DisplayValue(detection.PreferredProfile.BuildToolFamily)}");
            if (!string.IsNullOrWhiteSpace(detection.PreferredProfile.TestToolFamily))
                lines.Add($"Test tool: {detection.PreferredProfile.TestToolFamily}");
        }

        if (detection.Signals.Count > 0)
        {
            lines.Add("Signals:");
            foreach (var signal in detection.Signals.Take(10))
                lines.Add($"- {signal.RelativePath} [{signal.BuildSystemType}] {signal.Description}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
