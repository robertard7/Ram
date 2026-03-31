using System.IO;
using RAM.Models;

namespace RAM.Services;

public sealed class CSharpGenerationEscalationService
{
    public CSharpGenerationEscalationDecision TryResolve(
        ToolRequest request,
        string fullPath,
        CSharpGenerationGuardrailEvaluationRecord evaluation)
    {
        if (request is null || evaluation is null || evaluation.Accepted)
            return CSharpGenerationEscalationDecision.None();

        if (!ShouldEscalateForThinImplementation(evaluation))
        {
            return CSharpGenerationEscalationDecision.NotAvailable(
                "not_eligible",
                "generation rejection is not eligible for bounded thin-output escalation");
        }

        var fileName = Path.GetFileName(fullPath);
        var namespaceName = evaluation.Contract?.NamespaceName ?? "";
        return fileName.ToLowerInvariant() switch
        {
            "checkregistry.cs" => CSharpGenerationEscalationDecision.Retry(
                "test_registry_impl",
                BuildCheckRegistry(namespaceName),
                "escalated_test_registry_impl",
                "upgraded helper generation to test_registry_impl after thin simple implementation rejection"),
            "snapshotbuilder.cs" => CSharpGenerationEscalationDecision.Retry(
                "snapshot_builder_impl",
                BuildSnapshotBuilder(namespaceName),
                "escalated_snapshot_builder_impl",
                "upgraded helper generation to snapshot_builder_impl after thin simple implementation rejection"),
            "findingsnormalizer.cs" => CSharpGenerationEscalationDecision.Retry(
                "findings_normalizer_impl",
                BuildFindingsNormalizer(namespaceName),
                "escalated_findings_normalizer_impl",
                "upgraded helper generation to findings_normalizer_impl after thin simple implementation rejection"),
            _ => CSharpGenerationEscalationDecision.NotAvailable(
                "no_stronger_generation_path_available",
                $"no stronger bounded generation path is available for `{fileName}`")
        };
    }

    private static bool ShouldEscalateForThinImplementation(CSharpGenerationGuardrailEvaluationRecord evaluation)
    {
        var currentProfile = evaluation.Contract?.Profile ?? CSharpGenerationProfile.None;
        var failedRules = evaluation.ProfileEnforcement?.FailedRules ?? [];
        if (!failedRules.Contains("simple_implementation_too_thin", StringComparer.OrdinalIgnoreCase))
            return false;

        return currentProfile is CSharpGenerationProfile.SimpleImplementation
            or CSharpGenerationProfile.TestRegistryImplementation
            or CSharpGenerationProfile.SnapshotBuilderImplementation
            or CSharpGenerationProfile.FindingsNormalizerImplementation
            or CSharpGenerationProfile.TestHelperImplementation
            or CSharpGenerationProfile.BuilderImplementation
            or CSharpGenerationProfile.NormalizerImplementation;
    }

    private static string BuildCheckRegistry(string namespaceName)
    {
        return $$"""
using System;
using System.Collections.Generic;

namespace {{namespaceName}};

public static class CheckRegistry
{
    public static IReadOnlyList<string> CreateDefaultChecks()
    {
        return
        [
            "defender",
            "firewall",
            "updates"
        ];
    }

    public static bool Contains(string checkKey)
    {
        return FindByKey(checkKey) is not null;
    }

    public static string? FindByKey(string checkKey)
    {
        if (string.IsNullOrWhiteSpace(checkKey))
            return null;

        foreach (var candidate in CreateDefaultChecks())
        {
            if (string.Equals(candidate, checkKey.Trim(), StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }
}
""";
    }

    private static string BuildSnapshotBuilder(string namespaceName)
    {
        return $$"""
using System.Collections.Generic;
using System.Text.Json;

namespace {{namespaceName}};

public static class SnapshotBuilder
{
    public static string BuildDefaultSnapshotJson()
    {
        return BuildSnapshotJson(BuildDefaultSnapshot());
    }

    public static IReadOnlyDictionary<string,object> BuildDefaultSnapshot()
    {
        return new Dictionary<string,object>
        {
            ["machine"] = "local",
            ["checks"] = new[] { "defender", "firewall", "updates" },
            ["capturedBy"] = "ram"
        };
    }

    public static string BuildSnapshotJson(IReadOnlyDictionary<string,object>? snapshot)
    {
        return JsonSerializer.Serialize(snapshot ?? BuildDefaultSnapshot());
    }
}
""";
    }

    private static string BuildFindingsNormalizer(string namespaceName)
    {
        return $$"""
using System;
using System.Collections.Generic;

namespace {{namespaceName}};

public static class FindingsNormalizer
{
    public static string NormalizeSeverity(string severity)
    {
        if (string.IsNullOrWhiteSpace(severity))
            return "info";

        return severity.Trim().ToLowerInvariant() switch
        {
            "critical" => "critical",
            "high" => "high",
            "medium" => "medium",
            "low" => "low",
            _ => "info"
        };
    }

    public static string NormalizeStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "unknown";

        return status.Trim().ToLowerInvariant() switch
        {
            "healthy" => "healthy",
            "queued" => "queued",
            "needs review" => "needs_review",
            "resolved" => "resolved",
            _ => "unknown"
        };
    }

    public static IReadOnlyList<string> NormalizeFindings(IEnumerable<string>? findings)
    {
        var normalized = new List<string>();
        if (findings is null)
            return normalized;

        foreach (var finding in findings)
        {
            if (string.IsNullOrWhiteSpace(finding))
                continue;

            normalized.Add(finding.Trim());
        }

        return normalized;
    }
}
""";
    }
}

public sealed class CSharpGenerationEscalationDecision
{
    public bool ShouldRetry { get; set; }
    public string RetryStatus { get; set; } = "";
    public string EscalationStatus { get; set; } = "";
    public string EscalationSummary { get; set; } = "";
    public string ProfileOverride { get; set; } = "";
    public string Content { get; set; } = "";

    public static CSharpGenerationEscalationDecision None()
    {
        return new CSharpGenerationEscalationDecision
        {
            ShouldRetry = false,
            RetryStatus = "not_attempted",
            EscalationStatus = "none",
            EscalationSummary = ""
        };
    }

    public static CSharpGenerationEscalationDecision Retry(
        string profileOverride,
        string content,
        string escalationStatus,
        string summary)
    {
        return new CSharpGenerationEscalationDecision
        {
            ShouldRetry = true,
            RetryStatus = "retry_with_stronger_profile",
            EscalationStatus = escalationStatus,
            EscalationSummary = summary,
            ProfileOverride = profileOverride,
            Content = content
        };
    }

    public static CSharpGenerationEscalationDecision NotAvailable(string retryStatus, string summary)
    {
        return new CSharpGenerationEscalationDecision
        {
            ShouldRetry = false,
            RetryStatus = retryStatus,
            EscalationStatus = "none",
            EscalationSummary = summary
        };
    }
}
