using System.IO;
using RAM.Models;

namespace RAM.Services;

public sealed class AutoValidationPolicyService
{
    public AutoValidationPolicyDecision Assess(
        BuildSystemType buildFamily,
        string changedFilePath,
        string selectedValidationTool,
        string selectedTargetPath,
        BuildScopeAssessmentRecord? scopeAssessment,
        bool isPatchApply)
    {
        if (buildFamily == BuildSystemType.Dotnet)
        {
            return new AutoValidationPolicyDecision
            {
                Mode = AutoValidationPolicyMode.AutoAllowed,
                Reason = "The workspace uses the existing .NET auto-validation path.",
                SuggestedNextStep = ""
            };
        }

        if (!IsNativeFamily(buildFamily))
        {
            return new AutoValidationPolicyDecision
            {
                Mode = AutoValidationPolicyMode.NotApplicable,
                Reason = "RAM could not determine a supported auto-validation family for this workspace change.",
                SuggestedNextStep = "Inspect build profiles and choose a safe manual validation target."
            };
        }

        if (buildFamily == BuildSystemType.Script)
        {
            return new AutoValidationPolicyDecision
            {
                Mode = AutoValidationPolicyMode.ManualOnly,
                Reason = "Native script-based auto-validation is manual-only by default because repo-local build scripts are the highest-risk family.",
                SuggestedNextStep = "Inspect build profiles and choose a narrower non-script validation target manually if one exists."
            };
        }

        if (IsNativeBuildSystemFile(changedFilePath))
        {
            return new AutoValidationPolicyDecision
            {
                Mode = AutoValidationPolicyMode.ManualOnly,
                Reason = $"The change touched {Path.GetFileName(changedFilePath)}, so native auto-validation is manual-only until you choose a tiny safe target explicitly.",
                SuggestedNextStep = BuildFamilySpecificNextStep(buildFamily, buildSystemFile: true)
            };
        }

        if (string.IsNullOrWhiteSpace(selectedValidationTool) || string.Equals(selectedValidationTool, "read_only_check", StringComparison.OrdinalIgnoreCase))
        {
            return new AutoValidationPolicyDecision
            {
                Mode = AutoValidationPolicyMode.ManualOnly,
                Reason = isPatchApply
                    ? "A native patch was applied, but RAM could not prove a tiny safe automatic validation target."
                    : "The change is native build-relevant, but RAM could not prove a tiny safe automatic validation target.",
                SuggestedNextStep = BuildFamilySpecificNextStep(buildFamily, buildSystemFile: false)
            };
        }

        if (!IsTinySafeTarget(buildFamily, selectedValidationTool, selectedTargetPath, scopeAssessment))
        {
            return new AutoValidationPolicyDecision
            {
                Mode = AutoValidationPolicyMode.ManualOnly,
                Reason = isPatchApply
                    ? "A native patch was applied, but automatic validation is manual-only unless RAM already has an explicitly tiny safe target."
                    : "Native auto-validation is manual-only unless RAM already has an explicitly tiny safe target for the changed file.",
                SuggestedNextStep = BuildFamilySpecificNextStep(buildFamily, buildSystemFile: false)
            };
        }

        return new AutoValidationPolicyDecision
        {
            Mode = AutoValidationPolicyMode.AutoAllowed,
            Reason = "RAM found an explicitly tiny safe native validation target, so automatic validation is allowed.",
            SuggestedNextStep = ""
        };
    }

    private static bool IsTinySafeTarget(
        BuildSystemType buildFamily,
        string selectedValidationTool,
        string selectedTargetPath,
        BuildScopeAssessmentRecord? scopeAssessment)
    {
        if (scopeAssessment is null || !scopeAssessment.LiveExecutionAllowed)
            return false;

        if (scopeAssessment.RiskLevel != BuildScopeRiskLevel.SafeNarrow)
            return false;

        if (string.IsNullOrWhiteSpace(selectedTargetPath) || selectedTargetPath is "." or "./")
            return false;

        if (string.Equals(selectedValidationTool, "run_build_script", StringComparison.OrdinalIgnoreCase))
            return false;

        var normalized = NormalizePath(selectedTargetPath).Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (normalized.Equals("build", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("out", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var depth = normalized.Count(ch => ch == '/');
        return buildFamily switch
        {
            BuildSystemType.CMake => depth >= 1,
            BuildSystemType.Make or BuildSystemType.Ninja => depth >= 1,
            _ => false
        };
    }

    private static bool IsNativeBuildSystemFile(string path)
    {
        var fileName = Path.GetFileName(path ?? "");
        return string.Equals(fileName, "CMakeLists.txt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "Makefile", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "build.ninja", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("build.", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("configure.", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFamilySpecificNextStep(BuildSystemType buildFamily, bool buildSystemFile)
    {
        return buildFamily switch
        {
            BuildSystemType.CMake => buildSystemFile
                ? "Run `configure cmake` manually or inspect build profiles before choosing a tiny safe CMake build directory."
                : "Inspect build profiles or run CMake manually on a tiny safe build directory such as build/debug/subtarget.",
            BuildSystemType.Make => "Inspect build profiles and choose a narrow make directory or explicit target manually.",
            BuildSystemType.Ninja => "Inspect build profiles and choose a narrow ninja build directory or explicit target manually.",
            BuildSystemType.Script => "Inspect build profiles and choose a narrower non-script validation path manually if one exists.",
            _ => "Inspect build profiles and choose a safe manual validation target."
        };
    }

    private static bool IsNativeFamily(BuildSystemType buildFamily)
    {
        return buildFamily is BuildSystemType.CMake
            or BuildSystemType.Make
            or BuildSystemType.Ninja
            or BuildSystemType.Script;
    }

    private static string NormalizePath(string path)
    {
        return (path ?? "").Replace('\\', '/');
    }
}
