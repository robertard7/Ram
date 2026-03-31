using System.IO;
using System.Security.Cryptography;
using RAM.Models;

namespace RAM.Services;

public sealed class WorkspaceSnapshotService
{
    public const string ScannerVersion = "workspace_snapshot_scan.v1";

    private readonly FileIdentityService _fileIdentityService = new();
    private readonly WorkspaceStructuralExclusionPolicyService _exclusionPolicyService = new();

    public WorkspaceSnapshotRecord Capture(string workspaceRoot)
    {
        EnsureWorkspaceExists(workspaceRoot);

        var fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        var files = new List<WorkspaceFileRecord>();
        var pending = new Stack<string>();
        pending.Push(fullWorkspaceRoot);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            foreach (var directory in Directory.EnumerateDirectories(current).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (_exclusionPolicyService.ShouldExcludeDirectory(directory))
                    continue;

                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(BuildFileRecord(fullWorkspaceRoot, file));
            }
        }

        files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));

        var solutionPaths = files
            .Where(file => string.Equals(file.FileKind, "solution", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var projectPaths = files
            .Where(file => string.Equals(file.FileKind, "project", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WorkspaceSnapshotRecord
        {
            SnapshotId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = fullWorkspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            ScannerVersion = ScannerVersion,
            ExclusionPolicyVersion = WorkspaceStructuralExclusionPolicyService.PolicyVersion,
            ExcludedDirectoryNames = [.. _exclusionPolicyService.GetExcludedDirectoryNames()],
            FileCount = files.Count,
            SolutionCount = solutionPaths.Count,
            ProjectCount = projectPaths.Count,
            SolutionPaths = solutionPaths,
            ProjectPaths = projectPaths,
            Files = files
        };
    }

    private WorkspaceFileRecord BuildFileRecord(string workspaceRoot, string fullPath)
    {
        var relativePath = NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, fullPath));
        var fileName = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var parentDirectory = Path.GetDirectoryName(relativePath);
        var identity = _fileIdentityService.Identify(relativePath);
        var fileKind = ResolveFileKind(relativePath, extension, identity);
        var languageHint = ResolveLanguageHint(extension, fileKind);
        var fileInfo = new FileInfo(fullPath);
        var evidence = new List<string>
        {
            $"extension={DisplayValue(extension)}",
            $"file_kind={DisplayValue(fileKind)}",
            $"language={DisplayValue(languageHint)}"
        };

        var contentHash = TryComputeSha256(fullPath, out var hashEvidence);
        if (!string.IsNullOrWhiteSpace(hashEvidence))
            evidence.Add(hashEvidence);
        evidence.Add(identity.IdentityTrace);

        return new WorkspaceFileRecord
        {
            FileKey = relativePath,
            RelativePath = relativePath,
            FileName = fileName,
            ParentDirectory = string.IsNullOrWhiteSpace(parentDirectory)
                ? "."
                : NormalizeRelativePath(parentDirectory),
            Extension = extension,
            FileKind = fileKind,
            LanguageHint = languageHint,
            SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            LastWriteUtc = fileInfo.Exists
                ? fileInfo.LastWriteTimeUtc.ToString("O")
                : "",
            ContentSha256 = contentHash,
            Identity = identity,
            Evidence = evidence
        };
    }

    private static string ResolveFileKind(string relativePath, string extension, FileIdentityRecord identity)
    {
        if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase))
            return "solution";
        if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase))
            return "project";
        if (string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase))
            return "xaml";
        if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(identity.Role, "tests", StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains(".tests.", StringComparison.OrdinalIgnoreCase)
                ? "test"
                : "source";
        }

        if (extension is ".json" or ".config" or ".props" or ".targets" or ".editorconfig" or ".xml" or ".yaml" or ".yml")
            return "config";
        if (extension is ".resx" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico")
            return "resource";
        if (extension is ".md" or ".txt")
            return "docs";

        return "unknown";
    }

    private static string ResolveLanguageHint(string extension, string fileKind)
    {
        return fileKind switch
        {
            "solution" => ".NET solution",
            "project" => "C# / .NET",
            "source" or "test" => "C#",
            "xaml" => "XAML",
            "config" => extension switch
            {
                ".json" => "JSON",
                ".xml" or ".config" => "XML",
                ".props" or ".targets" => ".NET build configuration",
                ".yaml" or ".yml" => "YAML",
                _ => "Configuration"
            },
            "resource" => "Resource",
            "docs" => "Documentation",
            _ => extension.TrimStart('.')
        };
    }

    private static string TryComputeSha256(string fullPath, out string evidence)
    {
        try
        {
            using var stream = File.OpenRead(fullPath);
            using var sha = SHA256.Create();
            evidence = "hash_status=available";
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            evidence = $"hash_status=unavailable reason={SanitizeEvidence(ex.GetType().Name)}";
            return "";
        }
    }

    private static string SanitizeEvidence(string value)
    {
        return (value ?? "").Trim().Replace(' ', '_');
    }

    private static void EnsureWorkspaceExists(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace not found: {workspaceRoot}");
    }

    private static string NormalizeRelativePath(string value)
    {
        return (value ?? "").Replace('\\', '/').Trim().Trim('/');
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
