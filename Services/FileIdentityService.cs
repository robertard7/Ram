using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class FileIdentityService
{
    public const string ResolverContractVersion = "file_identity.v2";

    public FileIdentityRecord Identify(string? relativePath)
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        var normalizedLower = normalizedPath.ToLowerInvariant();
        var fileName = Path.GetFileName(normalizedPath);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var projectName = ResolveProjectName(normalizedPath, fileName, extension);
        var role = ResolveRole(normalizedLower, fileName);
        var fileType = ResolveFileType(normalizedPath, extension, role);
        var namespaceHint = BuildNamespaceHint(projectName, normalizedPath, role, fileType);

        return new FileIdentityRecord
        {
            Path = normalizedPath,
            FileType = fileType,
            Role = role,
            ProjectName = projectName,
            NamespaceHint = namespaceHint,
            IdentityTrace = $"path={DisplayValue(normalizedPath)} type={DisplayValue(fileType)} role={DisplayValue(role)} project={DisplayValue(projectName)} namespace={DisplayValue(namespaceHint)}"
        };
    }

    private static string ResolveFileType(string normalizedPath, string extension, string role)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return "";
        if (!Path.HasExtension(normalizedPath))
            return "directory";
        if (extension == ".sln")
            return "solution";
        if (extension == ".csproj")
            return string.Equals(role, "tests", StringComparison.OrdinalIgnoreCase)
                ? "test_project"
                : "csproj";
        if (extension == ".cs")
            return "cs";
        if (extension == ".xaml")
            return "xaml";

        return extension.TrimStart('.');
    }

    private static string ResolveRole(string normalizedLower, string fileName)
    {
        if (normalizedLower.Contains("/tests/") || normalizedLower.Contains(".tests.", StringComparison.OrdinalIgnoreCase))
            return "tests";
        if (normalizedLower.Contains("/storage/") || fileName.Contains(".Storage", StringComparison.OrdinalIgnoreCase))
            return "storage";
        if (normalizedLower.Contains("/services/") || fileName.Contains(".Services", StringComparison.OrdinalIgnoreCase))
            return "services";
        if (normalizedLower.Contains("/state/") || fileName.Contains("State", StringComparison.OrdinalIgnoreCase))
            return "state";
        if (normalizedLower.Contains("/contracts/") || fileName.Contains(".Contracts", StringComparison.OrdinalIgnoreCase))
            return "contracts";
        if (normalizedLower.Contains("/views/"))
            return "views";
        if (normalizedLower.Contains("/viewmodels/") || fileName.Contains("ViewModel", StringComparison.OrdinalIgnoreCase))
            return "ui";
        if (normalizedLower.Contains("/core/") || fileName.Contains(".Core", StringComparison.OrdinalIgnoreCase))
            return "core";
        if (normalizedLower.Contains("/models/"))
            return "models";
        if (normalizedLower.Contains("/ui/"))
            return "ui";
        if (normalizedLower.Contains("/repository/") || fileName.Contains("Repository", StringComparison.OrdinalIgnoreCase))
            return "repository";

        return "";
    }

    private static string ResolveProjectName(string normalizedPath, string fileName, string extension)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return "";

        if (extension is ".csproj" or ".sln")
            return Path.GetFileNameWithoutExtension(fileName);

        var segments = normalizedPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (!string.Equals(segment, "src", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 < segments.Length)
                return SanitizeProjectName(segments[index + 1]);
        }

        return "";
    }

    private static string BuildNamespaceHint(string projectName, string normalizedPath, string role, string fileType)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return "";

        var suffix = role switch
        {
            "storage" => ".Storage",
            "services" => ".Services",
            "state" => ".State",
            "contracts" => ".Contracts",
            "models" => ".Models",
            "views" => ".Views",
            "ui" when string.Equals(fileType, "xaml", StringComparison.OrdinalIgnoreCase) => ".Views",
            "ui" => ".ViewModels",
            "tests" => "",
            _ => ""
        };

        if (!string.IsNullOrWhiteSpace(suffix))
            return projectName + suffix;

        if (normalizedPath.Contains("/viewmodels/", StringComparison.OrdinalIgnoreCase))
            return projectName + ".ViewModels";

        return projectName;
    }

    private static string SanitizeProjectName(string value)
    {
        var cleaned = (value ?? "")
            .Trim()
            .Trim('"', '\'')
            .Replace('\\', '.')
            .Replace('/', '.')
            .Replace(' ', '.');
        var parts = cleaned
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeIdentifier)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
        return parts.Count == 0 ? "" : string.Join(".", parts);
    }

    private static string SanitizeIdentifier(string value)
    {
        var parts = Regex.Matches(value ?? "", @"[A-Za-z0-9]+")
            .Select(match => match.Value)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
        if (parts.Count == 0)
            return "";

        return string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string NormalizeRelativePath(string? value)
    {
        return (value ?? "").Replace('\\', '/').Trim().Trim('/');
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
