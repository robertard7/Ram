using System.IO;
using System.Xml.Linq;
using RAM.Models;

namespace RAM.Services;

public sealed class LocalRepairPlanningService
{
    private static readonly HashSet<string> ReferenceCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CS0246",
        "CS0234",
        "CS0103",
        "CS1061"
    };

    private static readonly HashSet<string> SyntaxCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CS1002",
        "CS1513",
        "CS1022",
        "CS1519",
        "CS1003",
        "CS1026"
    };

    private readonly ProjectGraphRepairInferenceService _projectGraphRepairInferenceService = new();
    private readonly GeneratedSymbolRecoveryService _generatedSymbolRecoveryService = new();

    public RepairProposalRecord Plan(string workspaceRoot, RepairPlanInput input)
    {
        if (input is null)
            throw new ArgumentNullException(nameof(input));

        var proposal = new RepairProposalRecord
        {
            ProposalId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            SourceArtifactId = input.SourceArtifactId,
            SourceArtifactType = input.SourceArtifactType,
            TargetFilePath = input.TargetFilePath,
            TargetLineNumber = input.TargetLineNumber,
            TargetColumnNumber = input.TargetColumnNumber,
            FailureKind = input.FailureKind,
            Confidence = string.IsNullOrWhiteSpace(input.Confidence) ? "none" : input.Confidence,
            FailureSummary = input.FailureMessage,
            FileExcerpt = input.FileExcerpt,
            TargetProjectPath = input.TargetProjectPath,
            TargetingStrategy = input.TargetingStrategy,
            TargetingSummary = input.TargetingSummary,
            HasAmbiguity = input.HasAmbiguity,
            AmbiguitySummary = input.AmbiguitySummary,
            CandidatePaths = input.CandidatePaths.ToList(),
            RetrievalBackend = input.RetrievalBackend,
            RetrievalEmbedderModel = input.RetrievalEmbedderModel,
            RetrievalQueryKind = input.RetrievalQueryKind,
            RetrievalHitCount = input.RetrievalHitCount,
            RetrievalSourceKinds = input.RetrievalSourceKinds.ToList(),
            RetrievalSourcePaths = input.RetrievalSourcePaths.ToList(),
            RetrievalQueryArtifactRelativePath = input.RetrievalQueryArtifactRelativePath,
            RetrievalResultArtifactRelativePath = input.RetrievalResultArtifactRelativePath,
            RetrievalContextPacketArtifactRelativePath = input.RetrievalContextPacketArtifactRelativePath,
            RetrievalIndexBatchArtifactRelativePath = input.RetrievalIndexBatchArtifactRelativePath,
            RetrievalSummary = BuildRetrievalSummary(input),
            ReferencedSymbolName = input.ReferencedSymbolName,
            ReferencedMemberName = input.ReferencedMemberName,
            SymbolRecoveryStatus = input.SymbolRecoveryStatus,
            SymbolRecoverySummary = input.SymbolRecoverySummary,
            SymbolRecoveryCandidatePath = input.SymbolRecoveryCandidatePath,
            SymbolRecoveryCandidateNamespace = input.SymbolRecoveryCandidateNamespace
        };

        if (input.HasAmbiguity)
            return BuildAmbiguousProposal(proposal, input);

        if (string.Equals(input.FailureKind, "build_failure", StringComparison.OrdinalIgnoreCase))
            return BuildBuildFailureProposal(workspaceRoot, proposal, input);

        if (string.Equals(input.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase))
            return BuildTestFailureProposal(workspaceRoot, proposal, input);

        return BuildFallbackProposal(proposal, input, true);
    }

    private RepairProposalRecord BuildBuildFailureProposal(string workspaceRoot, RepairProposalRecord proposal, RepairPlanInput input)
    {
        var extension = Path.GetExtension(input.TargetFilePath).ToLowerInvariant();
        if (string.Equals(input.FailureCode, "MSB4006", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(input.FailureMessage, "MSB4006", "circular dependency", "target dependency graph"))
        {
            var circularProposal = TryBuildCircularDependencyRepairProposal(workspaceRoot, proposal, input);
            if (circularProposal is not null)
                return circularProposal;

            proposal.Title = $"Inspect solution and project wiring for circular build dependency in {DisplayTarget(input.TargetFilePath)}";
            proposal.Rationale = "The latest build failure reports a circular dependency in the MSBuild target graph, so the next step is to inspect the solution and project wiring that introduced the recursive dependency.";
            proposal.ProposedActionType = "inspect_only";
            proposal.RequiresModel = false;
            proposal.Steps = BuildSteps(
                $"Open {DisplayTarget(input.TargetFilePath)} and the attached project files that participate in the failing build graph.",
                "Inspect project references, imported targets, and generated solution wiring for recursive dependencies that match the recorded MSB4006 failure.",
                $"Re-run dotnet build for {DisplayTarget(input.TargetProjectPath)} after removing the smallest recursive dependency.");
            return proposal;
        }

        if (extension is ".csproj" or ".props" or ".targets" or ".xml")
        {
            var deterministicProjectRepair = TryBuildDeterministicProjectFileRepairProposal(workspaceRoot, proposal, input);
            if (deterministicProjectRepair is not null)
                return deterministicProjectRepair;

            proposal.Title = $"Inspect project configuration in {DisplayTarget(input.TargetFilePath)}";
            proposal.Rationale = "The latest build failure points at a project or XML file, so the immediate next step is to inspect the malformed configuration near the reported location.";
            proposal.ProposedActionType = "inspect_only";
            proposal.RequiresModel = false;
            proposal.Steps = BuildSteps(
                $"Open {DisplayTarget(input.TargetFilePath)} near line {DisplayLine(input.TargetLineNumber)} and inspect the XML or project configuration around the failure.",
                "Correct the malformed element, missing attribute, or misplaced item that matches the reported failure message.",
                $"Re-run dotnet build for {DisplayTarget(input.TargetProjectPath)} after the focused change.");
            return proposal;
        }

        if (ReferenceCodes.Contains(input.FailureCode))
        {
            var symbolRecoveryProposal = TryBuildGeneratedSymbolRecoveryProposal(workspaceRoot, proposal, input);
            if (symbolRecoveryProposal is not null)
                return symbolRecoveryProposal;

            proposal.Title = $"Resolve missing reference or symbol in {DisplayTarget(input.TargetFilePath)}";
            proposal.Rationale = "The compiler reported a missing type, namespace, symbol, or member at a known location, so the first repair should focus on the reference or using/import at that line.";
            proposal.ProposedActionType = "update_reference";
            proposal.RequiresModel = false;
            proposal.Steps = BuildSteps(
                $"Inspect the failing line in {DisplayTarget(input.TargetFilePath)} and confirm the exact symbol or namespace named in the compiler error.",
                "Check nearby using directives, project references, and the surrounding type/member names for the smallest correction that satisfies the compiler.",
                $"Re-run dotnet build for {DisplayTarget(input.TargetProjectPath)} to confirm the reference issue is resolved.");
            return proposal;
        }

        if (SyntaxCodes.Contains(input.FailureCode))
        {
            proposal.Title = $"Fix syntax around the reported compiler location in {DisplayTarget(input.TargetFilePath)}";
            proposal.Rationale = "The compiler reported a syntax-level error at a precise location, so the smallest next move is to repair the local code block around that line.";
            proposal.ProposedActionType = "replace_block";
            proposal.RequiresModel = false;
            proposal.Steps = BuildSteps(
                $"Inspect the code block around line {DisplayLine(input.TargetLineNumber)} in {DisplayTarget(input.TargetFilePath)}.",
                "Correct the smallest malformed statement, delimiter, brace, or argument list that matches the compiler message.",
                $"Re-run dotnet build for {DisplayTarget(input.TargetProjectPath)} to verify the syntax repair.");
            return proposal;
        }

        return BuildFallbackProposal(proposal, input, true);
    }

    private RepairProposalRecord? TryBuildGeneratedSymbolRecoveryProposal(
        string workspaceRoot,
        RepairProposalRecord proposal,
        RepairPlanInput input)
    {
        var assessment = _generatedSymbolRecoveryService.Assess(workspaceRoot, input);
        if (string.IsNullOrWhiteSpace(assessment.Status))
            return null;

        input.ReferencedSymbolName = assessment.ReferencedSymbolName;
        input.ReferencedMemberName = assessment.ReferencedMemberName;
        input.SymbolRecoveryStatus = assessment.Status;
        input.SymbolRecoverySummary = assessment.Summary;
        input.SymbolRecoveryCandidatePath = assessment.CandidatePath;
        input.SymbolRecoveryCandidateNamespace = assessment.CandidateNamespace;

        proposal.ReferencedSymbolName = assessment.ReferencedSymbolName;
        proposal.ReferencedMemberName = assessment.ReferencedMemberName;
        proposal.SymbolRecoveryStatus = assessment.Status;
        proposal.SymbolRecoverySummary = assessment.Summary;
        proposal.SymbolRecoveryCandidatePath = assessment.CandidatePath;
        proposal.SymbolRecoveryCandidateNamespace = assessment.CandidateNamespace;

        if (!assessment.CandidateVisibleWithoutEdit)
            return null;

        proposal.Title = $"Reconcile generated symbol `{DisplayTarget(FirstNonEmpty(assessment.ReferencedSymbolName, assessment.ReferencedMemberName))}` in {DisplayTarget(input.TargetFilePath)}";
        proposal.Rationale = assessment.Summary;
        proposal.ProposedActionType = "reconcile_generated_symbol";
        proposal.RequiresModel = false;
        var reconciliationInstruction = string.Equals(input.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
            ? "Prefer rerun-first repair closure when the generated symbol is now present and visible without adding a broader patch."
            : "Prefer rebuild-first repair closure when the generated symbol is now present and visible without adding a broader patch.";
        var rerunInstruction = string.Equals(input.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase)
            ? $"Only fall back to a smaller local reference fix if rerunning dotnet test for {DisplayTarget(input.TargetProjectPath)} still fails."
            : $"Only fall back to a smaller local reference fix if rebuild still fails for {DisplayTarget(input.TargetProjectPath)}.";
        proposal.Steps = BuildSteps(
            $"Re-read {DisplayTarget(input.TargetFilePath)} and confirm the recorded missing symbol `{DisplayTarget(FirstNonEmpty(assessment.ReferencedSymbolName, assessment.ReferencedMemberName))}` now exists in `{DisplayTarget(assessment.CandidatePath)}`.",
            reconciliationInstruction,
            rerunInstruction);
        return proposal;
    }

    private RepairProposalRecord? TryBuildCircularDependencyRepairProposal(
        string workspaceRoot,
        RepairProposalRecord proposal,
        RepairPlanInput input)
    {
        if (!input.TargetFilePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return null;

        var issue = _projectGraphRepairInferenceService.InspectCircularDependencyTarget(workspaceRoot, input.TargetFilePath);
        if (issue is null)
            return null;

        proposal.TargetFilePath = input.TargetFilePath;
        proposal.TargetLineNumber = issue.LineNumber;
        proposal.TargetColumnNumber = 0;
        proposal.RequiresModel = false;
        proposal.Confidence = FirstNonEmpty(issue.Confidence, proposal.Confidence, input.Confidence, "medium");

        if (string.Equals(issue.IssueKind, "self_project_reference", StringComparison.OrdinalIgnoreCase))
        {
            proposal.Title = $"Remove self project reference from {DisplayTarget(input.TargetFilePath)}";
            proposal.Rationale = "The failing project references itself, which creates a restore graph cycle and triggers MSB4006 during dotnet build.";
            proposal.ProposedActionType = "remove_self_project_reference";
            proposal.Steps = BuildSteps(
                $"Remove the self <ProjectReference> entry from {DisplayTarget(input.TargetFilePath)} at line {DisplayLine(issue.LineNumber)}.",
                "Keep the repair local to the project file and do not rewrite unrelated project configuration.",
                $"Re-run dotnet build for {DisplayTarget(input.TargetProjectPath)} to verify the circular dependency is gone.");
            return proposal;
        }

        if (string.Equals(issue.IssueKind, "duplicate_project_reference", StringComparison.OrdinalIgnoreCase))
        {
            proposal.Title = $"Remove duplicate project reference from {DisplayTarget(input.TargetFilePath)}";
            proposal.Rationale = "The failing project file contains the same project reference more than once, which can create an invalid restore graph for the generated build wiring.";
            proposal.ProposedActionType = "remove_duplicate_project_reference";
            proposal.Steps = BuildSteps(
                $"Remove the duplicate <ProjectReference> entry from {DisplayTarget(input.TargetFilePath)} at line {DisplayLine(issue.LineNumber)}.",
                "Keep one canonical reference and leave the rest of the project file unchanged.",
                $"Re-run dotnet build for {DisplayTarget(input.TargetProjectPath)} to verify the project graph is stable.");
            return proposal;
        }

        return null;
    }

    private RepairProposalRecord? TryBuildDeterministicProjectFileRepairProposal(
        string workspaceRoot,
        RepairProposalRecord proposal,
        RepairPlanInput input)
    {
        if (!input.TargetFilePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return null;

        var fullPath = ResolveWorkspaceFilePath(workspaceRoot, input.TargetFilePath);
        if (!File.Exists(fullPath))
            return null;

        if (IsLikelyDesktopAppProject(input.TargetFilePath)
            && NeedsDeterministicWpfProjectSettings(fullPath))
        {
            proposal.Title = $"Normalize WPF project settings in {DisplayTarget(input.TargetFilePath)}";
            proposal.Rationale = "The failing desktop app project is missing one or more deterministic WPF build settings required for bounded XAML and shell compilation.";
            proposal.ProposedActionType = "ensure_wpf_project_settings";
            proposal.RequiresModel = false;
            proposal.Steps = BuildSteps(
                $"Normalize the bounded WPF project settings in {DisplayTarget(input.TargetFilePath)}.",
                "Ensure the desktop app project uses a Windows target framework, UseWPF=true, and OutputType=WinExe without touching unrelated project configuration.",
                $"Re-run dotnet build for {DisplayTarget(input.TargetProjectPath)} after the focused project-file repair.");
            return proposal;
        }

        if (IsLikelyCoreLibraryProject(input.TargetFilePath)
            && NeedsDeterministicLibraryProjectSettings(fullPath))
        {
            proposal.Title = $"Normalize library project settings in {DisplayTarget(input.TargetFilePath)}";
            proposal.Rationale = "The failing core/contracts library project contains deterministic project-file drift that can be corrected locally without changing unrelated build wiring.";
            proposal.ProposedActionType = "ensure_library_project_settings";
            proposal.RequiresModel = false;
            proposal.Steps = BuildSteps(
                $"Normalize the bounded library project settings in {DisplayTarget(input.TargetFilePath)}.",
                "Ensure the core library uses a non-Windows target framework, keeps Nullable and ImplicitUsings enabled, and does not carry WPF or executable-only settings.",
                $"Re-run dotnet build for {DisplayTarget(input.TargetProjectPath)} after the focused project-file repair.");
            return proposal;
        }

        return null;
    }

    private RepairProposalRecord BuildTestFailureProposal(string workspaceRoot, RepairProposalRecord proposal, RepairPlanInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.TargetFilePath))
        {
            var symbolRecoveryProposal = TryBuildGeneratedSymbolRecoveryProposal(workspaceRoot, proposal, input);
            if (symbolRecoveryProposal is not null)
                return symbolRecoveryProposal;

            var isLikelyTestTarget = IsLikelyTestCodePath(input.TargetFilePath, input.TargetProjectPath);
            proposal.Title = isLikelyTestTarget
                ? $"Repair the failing test in {DisplayTarget(input.TargetFilePath)}"
                : $"Repair the implementation surfaced by the failing test in {DisplayTarget(input.TargetFilePath)}";
            proposal.Rationale = isLikelyTestTarget
                ? "The latest test failure resolves to a single test file, so the next bounded move is to repair the failing assertion or setup block and rerun the test."
                : "The latest test failure resolves to a single source file under test, so the next bounded move is to repair that implementation path and rerun the affected test target.";
            proposal.ProposedActionType = "replace_block";
            proposal.RequiresModel = false;
            proposal.Steps = BuildSteps(
                $"Open {DisplayTarget(input.TargetFilePath)} near line {DisplayLine(input.TargetLineNumber)} and inspect the failing block recorded by the test output.",
                isLikelyTestTarget
                    ? "Prefer the smallest local assertion or setup repair that matches the recorded failure before changing unrelated test structure."
                    : "Prefer the smallest local implementation repair that matches the recorded test failure before changing unrelated runtime structure.",
                $"Re-run dotnet test for {DisplayTarget(input.TargetProjectPath)} after the smallest focused change.");
            return proposal;
        }

        return BuildFallbackProposal(proposal, input, true);
    }

    private RepairProposalRecord BuildAmbiguousProposal(RepairProposalRecord proposal, RepairPlanInput input)
    {
        proposal.Title = "Choose the first repair target before editing";
        proposal.Rationale = string.IsNullOrWhiteSpace(input.AmbiguitySummary)
            ? "RAM found multiple plausible file targets for the current failure and should not guess which one to edit first."
            : input.AmbiguitySummary;
        proposal.ProposedActionType = "inspect_only";
        proposal.RequiresModel = true;
        proposal.Steps = BuildSteps(
            "Review the listed candidate files and pick the file that best matches the current failure message.",
            "Open the selected file or rerun `open_failure_context` with the more specific target in mind.",
            "Run `plan_repair` again for the chosen file to generate a tighter repair proposal.");
        proposal.ModelBrief = BuildModelBrief(input);
        return proposal;
    }

    private RepairProposalRecord BuildFallbackProposal(RepairProposalRecord proposal, RepairPlanInput input, bool requiresModel)
    {
        proposal.Title = $"Inspect {DisplayTarget(input.TargetFilePath)} before editing";
        proposal.Rationale = "RAM can identify the failure target and excerpt, but the next change is not safe to reduce further without a focused human or model review.";
        proposal.ProposedActionType = "inspect_only";
        proposal.RequiresModel = requiresModel;
        proposal.Steps = BuildSteps(
            $"Inspect the bounded excerpt from {DisplayTarget(input.TargetFilePath)} and correlate it with the recorded failure summary.",
            "Confirm the smallest local change that addresses the reported error without touching unrelated code.",
            $"Re-run the original build or test for {DisplayTarget(input.TargetProjectPath)} after the focused edit.");
        proposal.ModelBrief = requiresModel ? BuildModelBrief(input) : "";
        return proposal;
    }

    private static string BuildModelBrief(RepairPlanInput input)
    {
        var lines = new List<string>
        {
            "Repair task:",
            $"Target file: {DisplayTarget(input.TargetFilePath)}",
            $"Failure kind: {DisplayTarget(input.FailureKind)}",
            $"Failure summary: {DisplayTarget(input.FailureMessage)}"
        };

        if (!string.IsNullOrWhiteSpace(input.TargetProjectPath))
            lines.Add($"Target project: {input.TargetProjectPath}");

        if (!string.IsNullOrWhiteSpace(input.BaselineSolutionPath))
            lines.Add($"Authoritative baseline solution: {input.BaselineSolutionPath}");

        if (input.BaselineAllowedRoots.Count > 0)
            lines.Add($"Allowed maintenance roots: {string.Join(", ", input.BaselineAllowedRoots)}");

        if (input.BaselineExcludedRoots.Count > 0)
            lines.Add($"Ignored generated roots: {string.Join(", ", input.BaselineExcludedRoots)}");

        if (input.TargetLineNumber > 0)
            lines.Add($"Location: line {input.TargetLineNumber}" + (input.TargetColumnNumber > 0 ? $", column {input.TargetColumnNumber}" : ""));

        if (input.HasAmbiguity && input.CandidatePaths.Count > 0)
        {
            lines.Add("Candidate files:");
            foreach (var candidate in input.CandidatePaths.Take(5))
                lines.Add($"- {candidate}");
        }

        if (!string.IsNullOrWhiteSpace(input.FileExcerpt))
        {
            lines.Add("Bounded excerpt:");
            lines.Add(input.FileExcerpt);
        }

        if (!string.IsNullOrWhiteSpace(input.RetrievalContextText))
        {
            lines.Add("Retrieved maintenance context:");
            lines.Add(input.RetrievalContextText);
        }

        lines.Add("Requested task: propose the smallest sane repair or next inspection step for this failure.");
        lines.Add("Constraints: stay inside the selected workspace, prefer the target file above, do not create a new project tree, and do not change unrelated code.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildRetrievalSummary(RepairPlanInput input)
    {
        if (input.RetrievalHitCount <= 0 && string.IsNullOrWhiteSpace(input.RetrievalBackend))
            return "";

        var sourceKinds = input.RetrievalSourceKinds.Count == 0
            ? "(none)"
            : string.Join(", ", input.RetrievalSourceKinds);
        return $"backend={DisplayTarget(input.RetrievalBackend)} embedder={DisplayTarget(input.RetrievalEmbedderModel)} query={DisplayTarget(input.RetrievalQueryKind)} hits={input.RetrievalHitCount} sources={sourceKinds}";
    }

    private static List<RepairProposalStep> BuildSteps(params string[] instructions)
    {
        return instructions
            .Where(instruction => !string.IsNullOrWhiteSpace(instruction))
            .Select((instruction, index) => new RepairProposalStep
            {
                Order = index + 1,
                Instruction = instruction
            })
            .ToList();
    }

    private static string ResolveWorkspaceFilePath(string workspaceRoot, string relativePath)
    {
        return Path.GetFullPath(Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool IsLikelyDesktopAppProject(string targetFilePath)
    {
        var identity = new FileIdentityService().Identify(targetFilePath);
        if (!string.IsNullOrWhiteSpace(identity.Role))
            return !string.Equals(identity.Role, "core", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(identity.Role, "storage", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(identity.Role, "tests", StringComparison.OrdinalIgnoreCase);

        var fileName = Path.GetFileNameWithoutExtension(targetFilePath);
        return !string.IsNullOrWhiteSpace(fileName)
            && !fileName.Contains(".Core", StringComparison.OrdinalIgnoreCase)
            && !fileName.Contains(".Storage", StringComparison.OrdinalIgnoreCase)
            && !fileName.Contains(".Tests", StringComparison.OrdinalIgnoreCase)
            && !targetFilePath.Contains("/Core/", StringComparison.OrdinalIgnoreCase)
            && !targetFilePath.Contains("/Storage/", StringComparison.OrdinalIgnoreCase)
            && !targetFilePath.Contains("/Tests/", StringComparison.OrdinalIgnoreCase)
            && !targetFilePath.Contains("\\Core\\", StringComparison.OrdinalIgnoreCase)
            && !targetFilePath.Contains("\\Storage\\", StringComparison.OrdinalIgnoreCase)
            && !targetFilePath.Contains("\\Tests\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyCoreLibraryProject(string targetFilePath)
    {
        var identity = new FileIdentityService().Identify(targetFilePath);
        if (string.Equals(identity.Role, "core", StringComparison.OrdinalIgnoreCase))
            return true;

        var fileName = Path.GetFileNameWithoutExtension(targetFilePath);
        return !string.IsNullOrWhiteSpace(fileName)
            && (fileName.Contains(".Core", StringComparison.OrdinalIgnoreCase)
                || targetFilePath.Contains("/Core/", StringComparison.OrdinalIgnoreCase)
                || targetFilePath.Contains("\\Core\\", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLikelyTestCodePath(string targetFilePath, string targetProjectPath)
    {
        var fileIdentityService = new FileIdentityService();
        if (string.Equals(fileIdentityService.Identify(targetFilePath).Role, "tests", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileIdentityService.Identify(targetProjectPath).Role, "tests", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsAny(targetFilePath, "/Tests/", "\\Tests\\", "/Test/", "\\Test\\", ".Tests.cs", ".Test.cs", "Tests.cs", "Test.cs")
            || ContainsAny(targetProjectPath, ".Tests.csproj", ".Test.csproj", "/Tests/", "\\Tests\\");
    }

    private static bool NeedsDeterministicWpfProjectSettings(string fullPath)
    {
        try
        {
            var document = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
            if (!string.Equals(document.Root?.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase))
                return false;

            var targetFramework = document.Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "TargetFramework", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim() ?? "";
            var useWpf = document.Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "UseWPF", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim() ?? "";
            var outputType = document.Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "OutputType", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim() ?? "";

            return string.IsNullOrWhiteSpace(targetFramework)
                || !targetFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(useWpf, "true", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool NeedsDeterministicLibraryProjectSettings(string fullPath)
    {
        try
        {
            var document = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
            if (!string.Equals(document.Root?.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase))
                return false;

            var targetFramework = document.Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "TargetFramework", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim() ?? "";
            var useWpf = document.Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "UseWPF", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim() ?? "";
            var outputType = document.Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "OutputType", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim() ?? "";
            var implicitUsings = document.Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "ImplicitUsings", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim() ?? "";
            var nullable = document.Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "Nullable", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim() ?? "";

            return string.IsNullOrWhiteSpace(targetFramework)
                || targetFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase)
                || string.Equals(useWpf, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(implicitUsings, "enable", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(nullable, "enable", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string DisplayTarget(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static string DisplayLine(int lineNumber)
    {
        return lineNumber > 0 ? lineNumber.ToString() : "the reported location";
    }

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
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
}
