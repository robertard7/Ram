using System.IO;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class GeneratedSymbolRecoveryService
{
    private static readonly Regex TypeNotFoundRegex = new("'(?<symbol>[A-Za-z_][A-Za-z0-9_]*)'\\s+could not be found", RegexOptions.CultureInvariant);
    private static readonly Regex NameNotFoundRegex = new("name\\s+'(?<symbol>[A-Za-z_][A-Za-z0-9_]*)'\\s+does not exist", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex MissingMemberRegex = new("does not contain a definition for\\s+'(?<member>[A-Za-z_][A-Za-z0-9_]*)'", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public GeneratedSymbolRecoveryAssessment Assess(string workspaceRoot, RepairPlanInput input)
    {
        if (input is null)
            throw new ArgumentNullException(nameof(input));

        var symbolName = ResolveReferencedSymbolName(input);
        var memberName = ResolveReferencedMemberName(input);
        if (string.IsNullOrWhiteSpace(symbolName) && string.IsNullOrWhiteSpace(memberName))
            return GeneratedSymbolRecoveryAssessment.None;

        var targetFullPath = ResolveWorkspacePath(workspaceRoot, input.TargetFilePath);
        var targetNamespace = ExtractNamespace(targetFullPath);
        var targetDirectory = NormalizeRelativePath(Path.GetDirectoryName(input.TargetFilePath) ?? "");
        var candidate = FindCandidate(workspaceRoot, input, symbolName, memberName, targetNamespace, targetDirectory);
        if (candidate is null)
        {
            return new GeneratedSymbolRecoveryAssessment
            {
                ReferencedSymbolName = symbolName,
                ReferencedMemberName = memberName,
                Status = "generated_symbol_not_reconciled",
                Summary = $"No bounded same-run or workspace symbol recovery candidate was found for `{FirstNonEmpty(symbolName, memberName)}`."
            };
        }

        var status = candidate.WasTouchedThisRun
            ? "same_run_symbol_recovery_ready"
            : "workspace_symbol_recovery_ready";
        var summary = candidate.NamespaceMatches
            ? $"Found `{candidate.SymbolName}` in `{candidate.RelativePath}` with compatible namespace visibility; prefer rebuild-first repair closure before manual review."
            : $"Found `{candidate.SymbolName}` in `{candidate.RelativePath}`, but namespace visibility could not be proven locally.";

        return new GeneratedSymbolRecoveryAssessment
        {
            ReferencedSymbolName = symbolName,
            ReferencedMemberName = memberName,
            Status = candidate.NamespaceMatches ? status : "generated_symbol_namespace_unproven",
            Summary = summary,
            CandidatePath = candidate.RelativePath,
            CandidateNamespace = candidate.NamespaceName,
            CandidateVisibleWithoutEdit = candidate.NamespaceMatches
        };
    }

    private static string ResolveReferencedSymbolName(RepairPlanInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.ReferencedSymbolName))
            return input.ReferencedSymbolName;

        var match = TypeNotFoundRegex.Match(input.FailureMessage ?? "");
        if (match.Success)
            return match.Groups["symbol"].Value;

        match = NameNotFoundRegex.Match(input.FailureMessage ?? "");
        if (match.Success)
            return match.Groups["symbol"].Value;

        return "";
    }

    private static string ResolveReferencedMemberName(RepairPlanInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.ReferencedMemberName))
            return input.ReferencedMemberName;

        var match = MissingMemberRegex.Match(input.FailureMessage ?? "");
        return match.Success ? match.Groups["member"].Value : "";
    }

    private static SymbolCandidate? FindCandidate(
        string workspaceRoot,
        RepairPlanInput input,
        string symbolName,
        string memberName,
        string targetNamespace,
        string targetDirectory)
    {
        var targetContent = SafeReadAllText(ResolveWorkspacePath(workspaceRoot, input.TargetFilePath));
        var candidates = new List<SymbolCandidate>();

        foreach (var file in EnumerateCandidateFiles(workspaceRoot))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, file));
            if (string.Equals(relativePath, NormalizeRelativePath(input.TargetFilePath), StringComparison.OrdinalIgnoreCase))
                continue;

            var content = SafeReadAllText(file);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            if (!ContainsSymbol(content, symbolName, memberName))
                continue;

            var namespaceName = ExtractNamespace(file);
            var wasTouchedThisRun = input.RecentRunTouchedFilePaths.Any(path => string.Equals(NormalizeRelativePath(path), relativePath, StringComparison.OrdinalIgnoreCase));
            var sameDirectoryFamily = !string.IsNullOrWhiteSpace(targetDirectory)
                && relativePath.StartsWith(targetDirectory, StringComparison.OrdinalIgnoreCase);
            var namespaceMatches = string.IsNullOrWhiteSpace(targetNamespace)
                || string.Equals(targetNamespace, namespaceName, StringComparison.Ordinal)
                || ContainsUsingForNamespace(targetContent, namespaceName);

            if (!wasTouchedThisRun && !sameDirectoryFamily && !namespaceMatches)
                continue;

            candidates.Add(new SymbolCandidate
            {
                SymbolName = FirstNonEmpty(symbolName, memberName),
                RelativePath = relativePath,
                NamespaceName = namespaceName,
                WasTouchedThisRun = wasTouchedThisRun,
                NamespaceMatches = namespaceMatches,
                SameDirectoryFamily = sameDirectoryFamily
            });
        }

        return candidates
            .OrderByDescending(candidate => candidate.NamespaceMatches)
            .ThenByDescending(candidate => candidate.WasTouchedThisRun)
            .ThenByDescending(candidate => candidate.SameDirectoryFamily)
            .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool ContainsSymbol(string content, string symbolName, string memberName)
    {
        if (!string.IsNullOrWhiteSpace(symbolName))
        {
            if (Regex.IsMatch(content, $@"\b(class|record|interface|enum)\s+{Regex.Escape(symbolName)}\b", RegexOptions.CultureInvariant)
                || Regex.IsMatch(content, $@"\b{Regex.Escape(symbolName)}\s*\.", RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(memberName))
        {
            if (Regex.IsMatch(content, $@"\b{Regex.Escape(memberName)}\s*\(", RegexOptions.CultureInvariant)
                || Regex.IsMatch(content, $@"\b{Regex.Escape(memberName)}\s*=>", RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsUsingForNamespace(string content, string namespaceName)
    {
        return !string.IsNullOrWhiteSpace(namespaceName)
            && Regex.IsMatch(content, $@"using\s+{Regex.Escape(namespaceName)}\s*;", RegexOptions.CultureInvariant);
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root))
            yield break;

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(root, file));
            if (relativePath.StartsWith(".ram/", StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    private static string ExtractNamespace(string fullPath)
    {
        var content = SafeReadAllText(fullPath);
        if (string.IsNullOrWhiteSpace(content))
            return "";

        var match = Regex.Match(content, @"namespace\s+([A-Za-z0-9_.]+)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string SafeReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch
        {
            return "";
        }
    }

    private static string ResolveWorkspacePath(string workspaceRoot, string relativePath)
    {
        return Path.GetFullPath(Path.Combine(workspaceRoot, (relativePath ?? "").Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static string NormalizeRelativePath(string value)
    {
        return (value ?? "").Replace('\\', '/');
    }

    private sealed class SymbolCandidate
    {
        public string SymbolName { get; init; } = "";
        public string RelativePath { get; init; } = "";
        public string NamespaceName { get; init; } = "";
        public bool WasTouchedThisRun { get; init; }
        public bool NamespaceMatches { get; init; }
        public bool SameDirectoryFamily { get; init; }
    }
}
