using System.IO;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardMaintenanceBaselineService
{
    private static readonly Regex BacktickedPathPattern = new(
        @"`(?<path>(?:src|tests)/[^`]+?|[A-Za-z0-9_.-]+\.sln)`",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex InlinePathPattern = new(
        @"(?<path>\b(?:src|tests)/[A-Za-z0-9_.-]+/?\b|\b[A-Za-z0-9_.-]+\.sln\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public TaskboardMaintenanceBaselineRecord Resolve(
        string workspaceRoot,
        TaskboardDocument? document,
        WorkspaceExecutionStateRecord? executionState = null)
    {
        return ResolveCore(
            workspaceRoot,
            document?.Title ?? "",
            document?.ObjectiveText ?? "",
            CollectSectionTexts(document),
            executionState);
    }

    public TaskboardMaintenanceBaselineRecord ResolveFromRawText(
        string workspaceRoot,
        string planTitle,
        string rawText,
        WorkspaceExecutionStateRecord? executionState = null)
    {
        return ResolveCore(workspaceRoot, planTitle, rawText, [], executionState);
    }

    public TaskboardMaintenanceBaselineRecord ResolveFromRequestContext(string workspaceRoot, ToolRequest? request)
    {
        if (request is null)
            return new TaskboardMaintenanceBaselineRecord();

        var solutionPath = NormalizePath(ReadArgument(request, "baseline_solution_path"));
        var allowedRoots = SplitMultiValueArgument(request, "baseline_allowed_roots")
            .Select(NormalizeRootPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var excludedRoots = SplitMultiValueArgument(request, "baseline_excluded_roots")
            .Select(NormalizeRootPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.IsNullOrWhiteSpace(solutionPath)
            && allowedRoots.Count == 0
            && excludedRoots.Count == 0)
        {
            return new TaskboardMaintenanceBaselineRecord();
        }

        var record = new TaskboardMaintenanceBaselineRecord
        {
            IsMaintenanceMode = true,
            BaselineDeclared = !string.IsNullOrWhiteSpace(solutionPath) || allowedRoots.Count > 0,
            BaselineResolved = !string.IsNullOrWhiteSpace(solutionPath) || allowedRoots.Count > 0,
            BaselineAutoBound = true,
            PlanTitle = request.TaskboardPlanTitle ?? "",
            DeclaredSolutionPath = solutionPath,
            PrimarySolutionPath = solutionPath,
            BindingSource = "tool_request_context",
            DeclaredPaths = BuildDeclaredPathList(solutionPath, allowedRoots),
            DeclaredMutationRoots = [.. allowedRoots],
            AllowedMutationRoots = [.. allowedRoots],
            ExcludedGeneratedRoots = [.. excludedRoots],
            DiscoveredProjectRoots = EnumerateWorkspaceProjectRoots(workspaceRoot)
        };

        record.PrimaryUiProjectPath = ResolveUiProjectPath(workspaceRoot, record.AllowedMutationRoots);
        record.CoreProjectPath = ResolveProjectPath(workspaceRoot, record.AllowedMutationRoots, ".Core");
        record.ServicesProjectPath = ResolveProjectPath(workspaceRoot, record.AllowedMutationRoots, ".Services");
        record.StorageProjectPath = ResolveProjectPath(workspaceRoot, record.AllowedMutationRoots, ".Storage");
        record.TestsProjectPath = ResolveProjectPath(workspaceRoot, record.AllowedMutationRoots, ".Tests");
        ApplyRequestedStorageContext(record, request);
        ResolveStorageAuthority(workspaceRoot, record);
        record.Summary = BuildSummary(record);
        return record;
    }

    public TaskboardMaintenanceMutationGuardResult EvaluateMutationGuard(
        string workspaceRoot,
        TaskboardMaintenanceBaselineRecord baseline,
        ToolRequest? request)
    {
        if (!baseline.IsMaintenanceMode || request is null)
            return new TaskboardMaintenanceMutationGuardResult();

        var toolName = (request.ToolName ?? "").Trim().ToLowerInvariant();
        if (!IsMutationOrTargetSelectionTool(toolName))
        {
            return new TaskboardMaintenanceMutationGuardResult
            {
                Applies = true,
                Allowed = true,
                BaselineSolutionPath = baseline.PrimarySolutionPath,
                AllowedRoots = [.. baseline.AllowedMutationRoots],
                DeclaredRoots = [.. baseline.DeclaredMutationRoots],
                DiscoveredRoots = [.. baseline.DiscoveredProjectRoots],
                CompatibleStorageRoots = [.. baseline.CompatibleStorageRoots],
                ExcludedRoots = [.. baseline.ExcludedGeneratedRoots],
                StorageResolutionKind = baseline.StorageResolutionKind,
                StorageResolutionSummary = baseline.StorageResolutionSummary,
                Summary = baseline.Summary
            };
        }

        var targetPath = ResolveGuardTargetPath(request, toolName);
        var result = new TaskboardMaintenanceMutationGuardResult
        {
            Applies = true,
            Allowed = true,
            TargetPath = targetPath,
            BaselineSolutionPath = baseline.PrimarySolutionPath,
            AllowedRoots = [.. baseline.AllowedMutationRoots],
            DeclaredRoots = [.. baseline.DeclaredMutationRoots],
            DiscoveredRoots = [.. baseline.DiscoveredProjectRoots],
            CompatibleStorageRoots = [.. baseline.CompatibleStorageRoots],
            ExcludedRoots = [.. baseline.ExcludedGeneratedRoots],
            StorageResolutionKind = baseline.StorageResolutionKind,
            StorageResolutionSummary = baseline.StorageResolutionSummary,
            Summary = baseline.Summary
        };

        if (!baseline.BaselineResolved)
        {
            result.Allowed = false;
            result.ReasonCode = "maintenance_baseline_missing";
            result.Summary = BuildMissingBaselineMessage(baseline, targetPath, toolName);
            return result;
        }

        if (IsScaffoldMaterializationTool(toolName))
        {
            result.Allowed = false;
            result.ReasonCode = "maintenance_scaffold_blocked";
            result.Summary = BuildScaffoldBlockedMessage(baseline, targetPath, toolName);
            return result;
        }

        if (string.IsNullOrWhiteSpace(targetPath))
            return result;

        if (IsAllowedBaselinePath(targetPath, baseline))
            return result;

        if (IsDeclaredWorkspaceArtifactPath(targetPath))
            return result;

        result.Allowed = false;
        result.ReasonCode = "maintenance_target_outside_baseline";
        result.Summary = BuildOutsideBaselineMessage(baseline, targetPath, toolName);
        return result;
    }

    public void StampBaselineContext(ToolRequest? request, TaskboardMaintenanceBaselineRecord baseline)
    {
        if (request is null || !baseline.IsMaintenanceMode)
            return;

        if (!string.IsNullOrWhiteSpace(baseline.PrimarySolutionPath))
            request.Arguments["baseline_solution_path"] = baseline.PrimarySolutionPath;
        if (baseline.AllowedMutationRoots.Count > 0)
            request.Arguments["baseline_allowed_roots"] = string.Join("|", baseline.AllowedMutationRoots);
        if (baseline.ExcludedGeneratedRoots.Count > 0)
            request.Arguments["baseline_excluded_roots"] = string.Join("|", baseline.ExcludedGeneratedRoots);
        if (!string.IsNullOrWhiteSpace(baseline.StorageProjectPath))
            request.Arguments["baseline_storage_project_path"] = baseline.StorageProjectPath;
        if (!string.IsNullOrWhiteSpace(baseline.StorageAuthorityRoot))
            request.Arguments["baseline_storage_authority_root"] = baseline.StorageAuthorityRoot;
        if (!string.IsNullOrWhiteSpace(baseline.StorageResolutionKind))
            request.Arguments["baseline_storage_resolution_kind"] = baseline.StorageResolutionKind;
        if (!string.IsNullOrWhiteSpace(baseline.StorageResolutionSummary))
            request.Arguments["baseline_storage_resolution_summary"] = baseline.StorageResolutionSummary;
    }

    private TaskboardMaintenanceBaselineRecord ResolveCore(
        string workspaceRoot,
        string planTitle,
        string objectiveText,
        IReadOnlyList<string> sectionTexts,
        WorkspaceExecutionStateRecord? executionState)
    {
        var combined = string.Join(
            Environment.NewLine,
            new[] { planTitle, objectiveText }
                .Concat(sectionTexts)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        var isMaintenanceMode = LooksLikeMaintenancePlan(combined);
        var declaredPaths = ExtractDeclaredPaths(planTitle, objectiveText, sectionTexts);
        var normalizedDeclared = declaredPaths
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var declaredSolutionPath = normalizedDeclared.FirstOrDefault(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)) ?? "";
        var declaredProjectRoots = normalizedDeclared
            .Where(path => path.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
            .Select(NormalizeRootPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existingProjectRoots = EnumerateWorkspaceProjectRoots(workspaceRoot);
        var exactAllowedRoots = declaredProjectRoots
            .Where(root => WorkspaceRootExists(workspaceRoot, root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var record = new TaskboardMaintenanceBaselineRecord
        {
            IsMaintenanceMode = isMaintenanceMode,
            BaselineDeclared = declaredSolutionPath.Length > 0 || declaredProjectRoots.Count > 0,
            PlanTitle = planTitle,
            DeclaredPaths = normalizedDeclared,
            DeclaredSolutionPath = declaredSolutionPath,
            DeclaredMutationRoots = [.. declaredProjectRoots],
            AllowedMutationRoots = [.. exactAllowedRoots],
            DiscoveredProjectRoots = [.. existingProjectRoots],
            PrimarySolutionPath = ResolveExistingWorkspaceFile(workspaceRoot, declaredSolutionPath)
        };

        AutoBindMaintenanceBaseline(workspaceRoot, executionState, existingProjectRoots, record);
        record.PrimaryUiProjectPath = ResolveUiProjectPath(workspaceRoot, record.AllowedMutationRoots);
        record.CoreProjectPath = ResolveProjectPath(workspaceRoot, record.AllowedMutationRoots, ".Core");
        record.ServicesProjectPath = ResolveProjectPath(workspaceRoot, record.AllowedMutationRoots, ".Services");
        record.StorageProjectPath = ResolveProjectPath(workspaceRoot, record.AllowedMutationRoots, ".Storage");
        record.TestsProjectPath = ResolveProjectPath(workspaceRoot, record.AllowedMutationRoots, ".Tests");
        ResolveStorageAuthority(workspaceRoot, record);
        record.ExcludedGeneratedRoots = existingProjectRoots
            .Where(root => !record.AllowedMutationRoots.Any(allowed => string.Equals(allowed, root, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        record.BaselineResolved = !string.IsNullOrWhiteSpace(record.PrimarySolutionPath)
            || record.AllowedMutationRoots.Any(root => WorkspaceRootExists(workspaceRoot, root));
        record.Summary = BuildSummary(record);
        return record;
    }

    private static IReadOnlyList<string> CollectSectionTexts(TaskboardDocument? document)
    {
        if (document is null)
            return [];

        var results = new List<string>();
        void AddSection(TaskboardSectionContent? section)
        {
            if (section is null)
                return;

            AddIfMeaningful(results, section.Title);
            foreach (var paragraph in section.Paragraphs)
                AddIfMeaningful(results, paragraph);
            foreach (var bullet in section.BulletItems)
                AddIfMeaningful(results, bullet);
            foreach (var numbered in section.NumberedItems)
                AddIfMeaningful(results, numbered);
            foreach (var subsection in section.Subsections)
                AddSection(subsection);
        }

        foreach (var batch in document.Batches)
        {
            AddIfMeaningful(results, batch.Title);
            AddSection(batch.Content);
            foreach (var step in batch.Steps)
            {
                AddIfMeaningful(results, step.Title);
                AddSection(step.Content);
            }
        }

        foreach (var section in document.AdditionalSections)
            AddSection(section);

        foreach (var bucket in document.Guardrails.Buckets)
            AddSection(bucket);
        foreach (var section in document.AcceptanceCriteria.Sections)
            AddSection(section);
        foreach (var section in document.Invariants.Sections)
            AddSection(section);

        return results;
    }

    private static void AutoBindMaintenanceBaseline(
        string workspaceRoot,
        WorkspaceExecutionStateRecord? executionState,
        IReadOnlyList<string> existingProjectRoots,
        TaskboardMaintenanceBaselineRecord record)
    {
        if (!record.IsMaintenanceMode)
            return;

        var initialSolutionPath = record.PrimarySolutionPath;
        var initialAllowedRoots = record.AllowedMutationRoots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bindingSources = new List<string>();

        if (string.IsNullOrWhiteSpace(record.PrimarySolutionPath))
        {
            var resolvedSolution = ResolveAuthoritativeSolutionPath(workspaceRoot, executionState);
            if (!string.IsNullOrWhiteSpace(resolvedSolution))
            {
                record.PrimarySolutionPath = resolvedSolution;
                bindingSources.Add(IsPersistedExecutionPathCandidate(executionState, resolvedSolution)
                    ? "workspace_execution_state"
                    : "workspace_solution_scan");
            }
        }

        var resolvedRoots = new List<string>();
        foreach (var root in record.AllowedMutationRoots)
            AddIfMeaningful(resolvedRoots, NormalizeRootPath(root));

        AddIfMeaningful(resolvedRoots, ResolveUiRoot(workspaceRoot, existingProjectRoots));
        AddIfMeaningful(resolvedRoots, ResolveRoleRoot(workspaceRoot, existingProjectRoots, "src/", ".Core"));
        AddIfMeaningful(resolvedRoots, ResolveRoleRoot(workspaceRoot, existingProjectRoots, "src/", ".Services"));
        AddIfMeaningful(resolvedRoots, ResolveRoleRoot(workspaceRoot, existingProjectRoots, "src/", ".Storage"));
        AddIfMeaningful(resolvedRoots, ResolveRoleRoot(workspaceRoot, existingProjectRoots, "tests/", ".Tests", requireProjectFile: false));

        record.AllowedMutationRoots = resolvedRoots
            .Select(NormalizeRootPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (record.AllowedMutationRoots.Count > 0
            && !SetEquals(initialAllowedRoots, record.AllowedMutationRoots))
        {
            bindingSources.Add("workspace_project_roles");
        }

        if (!string.Equals(initialSolutionPath, record.PrimarySolutionPath, StringComparison.OrdinalIgnoreCase)
            || bindingSources.Count > 0)
        {
            record.BaselineAutoBound = !string.IsNullOrWhiteSpace(record.PrimarySolutionPath)
                || record.AllowedMutationRoots.Count > 0;
            record.BindingSource = bindingSources.Count == 0
                ? "workspace_alias"
                : string.Join("+", bindingSources.Distinct(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static void ApplyRequestedStorageContext(TaskboardMaintenanceBaselineRecord record, ToolRequest request)
    {
        record.StorageProjectPath = FirstNonEmpty(
            NormalizePath(ReadArgument(request, "baseline_storage_project_path")),
            record.StorageProjectPath);
        record.StorageAuthorityRoot = FirstNonEmpty(
            NormalizeRootPath(ReadArgument(request, "baseline_storage_authority_root")),
            record.StorageAuthorityRoot);
        record.StorageResolutionKind = FirstNonEmpty(
            ReadArgument(request, "baseline_storage_resolution_kind"),
            record.StorageResolutionKind);
        record.StorageResolutionSummary = FirstNonEmpty(
            ReadArgument(request, "baseline_storage_resolution_summary"),
            record.StorageResolutionSummary);
    }

    private static void ResolveStorageAuthority(string workspaceRoot, TaskboardMaintenanceBaselineRecord record)
    {
        record.CompatibleStorageRoots = [];

        if (!string.IsNullOrWhiteSpace(record.StorageProjectPath))
        {
            var explicitStorageRoot = NormalizeRootPath(Path.GetDirectoryName(record.StorageProjectPath) ?? "");
            AddIfMeaningful(record.CompatibleStorageRoots, explicitStorageRoot);

            if (string.IsNullOrWhiteSpace(record.StorageAuthorityRoot))
                record.StorageAuthorityRoot = explicitStorageRoot;
            if (string.IsNullOrWhiteSpace(record.StorageResolutionKind))
                record.StorageResolutionKind = "storage_project";
            if (string.IsNullOrWhiteSpace(record.StorageResolutionSummary))
                record.StorageResolutionSummary = $"Expected Storage responsibility resolves to project `{DisplayValue(record.StorageProjectPath)}` under `{DisplayValue(record.StorageAuthorityRoot)}`.";
            return;
        }

        var aliasCandidates = new List<(string Root, string ProjectPath, string Kind, string Summary)>();
        AddStorageAliasCandidate(aliasCandidates, workspaceRoot, record.CoreProjectPath, "aliased_core_storage_surface");
        AddStorageAliasCandidate(aliasCandidates, workspaceRoot, record.ServicesProjectPath, "aliased_services_storage_surface");
        AddStorageAliasCandidate(aliasCandidates, workspaceRoot, record.PrimaryUiProjectPath, "aliased_ui_storage_surface");

        foreach (var candidate in aliasCandidates)
            AddIfMeaningful(record.CompatibleStorageRoots, candidate.Root);

        var authoritativeCandidate = aliasCandidates
            .OrderBy(candidate => candidate.Kind, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(authoritativeCandidate.Root))
        {
            record.StorageProjectPath = authoritativeCandidate.ProjectPath;
            record.StorageAuthorityRoot = authoritativeCandidate.Root;
            record.StorageResolutionKind = authoritativeCandidate.Kind;
            record.StorageResolutionSummary = authoritativeCandidate.Summary;
            return;
        }

        record.StorageResolutionKind = "storage_surface_missing";
        record.StorageResolutionSummary =
            $"Expected Storage responsibility is not mapped yet. Declared roots `{DisplayRoots(record.DeclaredMutationRoots)}` allowed roots `{DisplayRoots(record.AllowedMutationRoots)}` discovered roots `{DisplayRoots(record.DiscoveredProjectRoots)}` compatible storage roots `{DisplayRoots(record.CompatibleStorageRoots)}`.";
    }

    private static void AddStorageAliasCandidate(
        ICollection<(string Root, string ProjectPath, string Kind, string Summary)> candidates,
        string workspaceRoot,
        string projectPath,
        string kind)
    {
        var normalizedProjectPath = NormalizePath(projectPath);
        if (string.IsNullOrWhiteSpace(normalizedProjectPath))
            return;

        var projectRoot = NormalizeRootPath(Path.GetDirectoryName(normalizedProjectPath) ?? "");
        if (string.IsNullOrWhiteSpace(projectRoot))
            return;

        var nestedStorageRoot = NormalizeRootPath(Path.Combine(projectRoot, "Storage"));
        if (HasCompatibleStorageSurface(workspaceRoot, nestedStorageRoot))
        {
            candidates.Add((
                nestedStorageRoot,
                normalizedProjectPath,
                kind,
                $"Expected Storage responsibility is satisfied by `{nestedStorageRoot}` inside `{normalizedProjectPath}`."));
            return;
        }

        if (HasCompatibleStorageSurface(workspaceRoot, projectRoot))
        {
            candidates.Add((
                projectRoot,
                normalizedProjectPath,
                kind,
                $"Expected Storage responsibility is satisfied directly inside `{normalizedProjectPath}` without a separate Storage project root."));
        }
    }

    private static bool HasCompatibleStorageSurface(string workspaceRoot, string root)
    {
        var normalizedRoot = NormalizeRootPath(root);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
            return false;

        var fullRoot = Path.Combine(workspaceRoot, normalizedRoot.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(fullRoot))
            return false;

        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ISettingsStore.cs",
            "FileSettingsStore.cs",
            "ISnapshotRepository.cs",
            "SqliteSnapshotRepository.cs"
        };

        if (Directory.EnumerateFiles(fullRoot, "*.cs", SearchOption.AllDirectories)
            .Any(path => expectedFiles.Contains(Path.GetFileName(path))))
        {
            return true;
        }

        return Directory.EnumerateFiles(fullRoot, "*.cs", SearchOption.AllDirectories)
            .Select(SafeReadAllText)
            .Any(content =>
                ContainsAny(content, "interface ISettingsStore", "class FileSettingsStore", "interface ISnapshotRepository", "class SqliteSnapshotRepository")
                || Regex.IsMatch(content ?? "", @"namespace\s+[A-Za-z0-9_.]*Storage", RegexOptions.CultureInvariant));
    }

    private static List<string> ExtractDeclaredPaths(string planTitle, string objectiveText, IReadOnlyList<string> sectionTexts)
    {
        var results = new List<string>();
        foreach (var source in new[] { planTitle, objectiveText }.Concat(sectionTexts))
        {
            if (string.IsNullOrWhiteSpace(source))
                continue;

            foreach (Match match in BacktickedPathPattern.Matches(source))
                AddIfMeaningful(results, match.Groups["path"].Value);
            foreach (Match match in InlinePathPattern.Matches(source))
                AddIfMeaningful(results, match.Groups["path"].Value);
        }

        return results;
    }

    private static bool LooksLikeMaintenancePlan(string text)
    {
        return ContainsAny(
            text,
            "existing project",
            "maintenance loop",
            "maintenance mode",
            "feature update",
            "patch repair",
            "maintenance baseline",
            "reuse the existing",
            "existing baseline",
            "reuse the clean",
            "baseline reuse",
            "update the existing",
            "pre-data / base-state truth",
            "do not re-scaffold",
            "not another greenfield scaffold");
    }

    private static bool IsMutationOrTargetSelectionTool(string toolName)
    {
        return toolName is
            "create_dotnet_solution"
            or "create_dotnet_project"
            or "add_project_to_solution"
            or "add_dotnet_project_reference"
            or "dotnet_build"
            or "dotnet_test"
            or "plan_repair"
            or "preview_patch_draft"
            or "apply_patch_draft"
            or "verify_patch_draft"
            or "make_dir"
            or "create_file"
            or "write_file"
            or "append_file"
            or "replace_in_file"
            or "create_dotnet_page_view"
            or "create_dotnet_viewmodel"
            or "register_navigation"
            or "register_di_service"
            or "initialize_sqlite_storage_boundary";
    }

    private static bool IsScaffoldMaterializationTool(string toolName)
    {
        return toolName is
            "create_dotnet_solution"
            or "create_dotnet_project"
            or "add_project_to_solution";
    }

    private static string ResolveGuardTargetPath(ToolRequest request, string toolName)
    {
        if (toolName == "create_dotnet_project")
        {
            var outputPath = ReadArgument(request, "output_path");
            var projectName = ReadArgument(request, "project_name");
            if (!string.IsNullOrWhiteSpace(outputPath) && !string.IsNullOrWhiteSpace(projectName))
                return NormalizePath(Path.Combine(outputPath, $"{projectName}.csproj"));
            return NormalizePath(outputPath);
        }

        if (toolName == "create_dotnet_solution")
        {
            var solutionName = ReadArgument(request, "solution_name");
            return string.IsNullOrWhiteSpace(solutionName)
                ? ""
                : NormalizePath($"{solutionName}.sln");
        }

        return FirstNonEmpty(
            NormalizePath(ReadArgument(request, "project")),
            NormalizePath(ReadArgument(request, "solution_path")),
            NormalizePath(ReadArgument(request, "project_path")),
            NormalizePath(ReadArgument(request, "reference_path")),
            NormalizePath(ReadArgument(request, "path")),
            NormalizePath(ReadArgument(request, "output_path")));
    }

    private static bool IsAllowedBaselinePath(string targetPath, TaskboardMaintenanceBaselineRecord baseline)
    {
        var normalized = NormalizePath(targetPath);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        if (string.Equals(normalized, baseline.PrimarySolutionPath, StringComparison.OrdinalIgnoreCase))
            return true;

        return baseline.AllowedMutationRoots.Any(root =>
            string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDeclaredWorkspaceArtifactPath(string path)
    {
        var normalized = NormalizePath(path);
        return normalized.StartsWith(".ram/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(".ram\\", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("artifacts/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("artifacts\\", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> EnumerateWorkspaceProjectRoots(string workspaceRoot)
    {
        var roots = new List<string>();
        foreach (var parent in new[] { "src", "tests" })
        {
            var fullParent = Path.Combine(workspaceRoot, parent);
            if (!Directory.Exists(fullParent))
                continue;

            foreach (var directory in Directory.GetDirectories(fullParent))
            {
                var relative = NormalizePath(Path.Combine(parent, Path.GetFileName(directory)));
                AddIfMeaningful(roots, relative);
            }
        }

        return roots;
    }

    private static string ResolveAuthoritativeSolutionPath(string workspaceRoot, WorkspaceExecutionStateRecord? executionState)
    {
        foreach (var candidate in EnumeratePersistedSolutionCandidates(executionState))
        {
            var resolved = ResolveExistingWorkspaceFile(workspaceRoot, candidate);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
        }

        var rootSolutionCandidates = Directory.Exists(workspaceRoot)
            ? Directory.GetFiles(workspaceRoot, "*.sln", SearchOption.TopDirectoryOnly)
                .Select(path => NormalizePath(Path.GetRelativePath(workspaceRoot, path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        return rootSolutionCandidates.Count == 1
            ? rootSolutionCandidates[0]
            : "";
    }

    private static IEnumerable<string> EnumeratePersistedSolutionCandidates(WorkspaceExecutionStateRecord? executionState)
    {
        if (executionState is null)
            return [];

        return new[]
            {
                executionState.LastSelectedBuildProfileTargetPath,
                executionState.LastVerificationTargetPath,
                executionState.LastSuccessTargetPath,
                executionState.LastFailureTargetPath
            }
            .Select(NormalizePath)
            .Where(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsPersistedExecutionPathCandidate(WorkspaceExecutionStateRecord? executionState, string path)
    {
        if (executionState is null || string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = NormalizePath(path);
        return EnumeratePersistedSolutionCandidates(executionState)
            .Any(candidate => string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveUiRoot(string workspaceRoot, IReadOnlyList<string> existingProjectRoots)
    {
        var candidates = existingProjectRoots
            .Where(root => root.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
                && !root.EndsWith(".Core", StringComparison.OrdinalIgnoreCase)
                && !root.EndsWith(".Services", StringComparison.OrdinalIgnoreCase)
                && !root.EndsWith(".Storage", StringComparison.OrdinalIgnoreCase)
                && !root.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                && (HasUiIndicators(workspaceRoot, root) || HasProjectFiles(workspaceRoot, root)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : "";
    }

    private static string ResolveRoleRoot(
        string workspaceRoot,
        IReadOnlyList<string> existingProjectRoots,
        string requiredPrefix,
        string requiredSuffix,
        bool requireProjectFile = true)
    {
        var candidates = existingProjectRoots
            .Where(root => root.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase)
                && root.EndsWith(requiredSuffix, StringComparison.OrdinalIgnoreCase)
                && (!requireProjectFile || HasProjectFiles(workspaceRoot, root)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : "";
    }

    private static string ResolveProjectPath(string workspaceRoot, IReadOnlyList<string> roots, string suffix)
    {
        var matchingRoot = roots.FirstOrDefault(root =>
            Path.GetFileName(root).EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(matchingRoot))
            return "";

        return ResolveProjectPathForRoot(workspaceRoot, matchingRoot);
    }

    private static string ResolveUiProjectPath(string workspaceRoot, IReadOnlyList<string> roots)
    {
        var matchingRoot = roots.FirstOrDefault(root =>
            root.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
            && !root.EndsWith(".Core", StringComparison.OrdinalIgnoreCase)
            && !root.EndsWith(".Services", StringComparison.OrdinalIgnoreCase)
            && !root.EndsWith(".Storage", StringComparison.OrdinalIgnoreCase)
            && !root.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(matchingRoot)
            ? ""
            : ResolveProjectPathForRoot(workspaceRoot, matchingRoot);
    }

    private static string ResolveProjectPathForRoot(string workspaceRoot, string root)
    {
        var normalizedRoot = NormalizeRootPath(root);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
            return "";

        var fullRoot = Path.Combine(workspaceRoot, normalizedRoot.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(fullRoot))
            return "";

        var projectFiles = Directory.GetFiles(fullRoot, "*.csproj", SearchOption.TopDirectoryOnly)
            .Select(path => NormalizePath(Path.GetRelativePath(workspaceRoot, path)))
            .ToList();
        if (projectFiles.Count == 0)
            return "";

        var expectedName = $"{Path.GetFileName(normalizedRoot)}.csproj";
        return projectFiles.FirstOrDefault(path =>
                   string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
               ?? projectFiles[0];
    }

    private static bool HasProjectFiles(string workspaceRoot, string root)
    {
        var normalizedRoot = NormalizeRootPath(root);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
            return false;

        var fullRoot = Path.Combine(workspaceRoot, normalizedRoot.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(fullRoot)
            && Directory.GetFiles(fullRoot, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0;
    }

    private static bool HasUiIndicators(string workspaceRoot, string root)
    {
        var normalizedRoot = NormalizeRootPath(root);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
            return false;

        var fullRoot = Path.Combine(workspaceRoot, normalizedRoot.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(fullRoot))
            return false;

        return Directory.GetFiles(fullRoot, "*.xaml", SearchOption.TopDirectoryOnly).Length > 0
            || File.Exists(Path.Combine(fullRoot, "App.xaml"))
            || Directory.Exists(Path.Combine(fullRoot, "Views"));
    }

    private static string ResolveExistingWorkspaceFile(string workspaceRoot, string relativePath)
    {
        var normalized = NormalizePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        var fullPath = Path.Combine(workspaceRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(fullPath) ? normalized : "";
    }

    private static bool WorkspaceRootExists(string workspaceRoot, string root)
    {
        var normalized = NormalizeRootPath(root);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var fullPath = Path.Combine(workspaceRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(fullPath);
    }

    private static string BuildSummary(TaskboardMaintenanceBaselineRecord record)
    {
        if (!record.IsMaintenanceMode)
            return "";

        var solution = DisplayValue(record.PrimarySolutionPath);
        var declaredSolution = DisplayValue(record.DeclaredSolutionPath);
        var allowedRoots = DisplayRoots(record.AllowedMutationRoots);
        var declaredRoots = DisplayRoots(record.DeclaredMutationRoots);
        var discoveredRoots = DisplayRoots(record.DiscoveredProjectRoots);
        var excludedRoots = DisplayRoots(record.ExcludedGeneratedRoots);
        var compatibleStorageRoots = DisplayRoots(record.CompatibleStorageRoots);
        var storageResolution = DisplayValue(record.StorageResolutionKind);
        var storageAuthorityRoot = DisplayValue(record.StorageAuthorityRoot);
        var storageProject = DisplayValue(record.StorageProjectPath);

        if (record.BaselineResolved)
        {
            if (record.BaselineAutoBound)
            {
                return $"Maintenance baseline: bound solution={solution} allowed_roots={allowedRoots} source={DisplayValue(record.BindingSource)} declared_solution={declaredSolution} declared_roots={declaredRoots} discovered_roots={discoveredRoots} storage_resolution={storageResolution} storage_authority_root={storageAuthorityRoot} storage_project={storageProject} compatible_storage_roots={compatibleStorageRoots} excluded_roots={excludedRoots}";
            }

            return $"Maintenance baseline: resolved solution={solution} allowed_roots={allowedRoots} declared_roots={declaredRoots} discovered_roots={discoveredRoots} storage_resolution={storageResolution} storage_authority_root={storageAuthorityRoot} storage_project={storageProject} compatible_storage_roots={compatibleStorageRoots} excluded_roots={excludedRoots}";
        }

        if (!record.BaselineDeclared)
            return $"Maintenance baseline: unresolved solution={solution} allowed_roots={allowedRoots} discovered_roots={discoveredRoots} storage_resolution={storageResolution} compatible_storage_roots={compatibleStorageRoots} source={DisplayValue(record.BindingSource)}";

        return $"Maintenance baseline: unresolved declared_solution={declaredSolution} declared_roots={declaredRoots} allowed_roots={allowedRoots} discovered_roots={discoveredRoots} storage_resolution={storageResolution} compatible_storage_roots={compatibleStorageRoots} excluded_roots={excludedRoots}";
    }

    private static string BuildMissingBaselineMessage(TaskboardMaintenanceBaselineRecord baseline, string targetPath, string toolName)
    {
        var targetSuffix = string.IsNullOrWhiteSpace(targetPath) ? "" : $" target={targetPath}";
        var declaredSolution = FirstNonEmpty(baseline.DeclaredSolutionPath, baseline.DeclaredPaths.FirstOrDefault(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)));
        var declaredRoots = baseline.DeclaredMutationRoots.Count > 0
            ? baseline.DeclaredMutationRoots
            : baseline.AllowedMutationRoots;
        return $"Taskboard auto-run blocked by maintenance baseline guard: `{toolName}` cannot run in maintenance mode{targetSuffix} because RAM could not bind an authoritative maintenance baseline. Declared solution `{DisplayValue(declaredSolution)}` declared roots `{DisplayRoots(declaredRoots)}` allowed roots `{DisplayRoots(baseline.AllowedMutationRoots)}` discovered roots `{DisplayRoots(baseline.DiscoveredProjectRoots)}` storage resolution `{DisplayValue(baseline.StorageResolutionKind)}` compatible storage roots `{DisplayRoots(baseline.CompatibleStorageRoots)}`. Ignored generated roots: {DisplayRoots(baseline.ExcludedGeneratedRoots)}.";
    }

    private static string BuildScaffoldBlockedMessage(TaskboardMaintenanceBaselineRecord baseline, string targetPath, string toolName)
    {
        var targetSuffix = string.IsNullOrWhiteSpace(targetPath) ? "" : $" target={targetPath}";
        return $"Taskboard auto-run blocked by maintenance baseline guard: `{toolName}` cannot materialize a new project tree in maintenance mode{targetSuffix}. Reuse the declared baseline solution `{DisplayValue(baseline.PrimarySolutionPath)}` allowed roots `{DisplayRoots(baseline.AllowedMutationRoots)}` discovered roots `{DisplayRoots(baseline.DiscoveredProjectRoots)}` storage resolution `{DisplayValue(baseline.StorageResolutionKind)}`.";
    }

    private static string BuildOutsideBaselineMessage(TaskboardMaintenanceBaselineRecord baseline, string targetPath, string toolName)
    {
        return $"Taskboard auto-run blocked by maintenance baseline guard: `{toolName}` targeted `{targetPath}`, which falls outside the declared maintenance baseline. Authoritative solution `{DisplayValue(baseline.PrimarySolutionPath)}` declared roots `{DisplayRoots(baseline.DeclaredMutationRoots)}` allowed roots `{DisplayRoots(baseline.AllowedMutationRoots)}` discovered roots `{DisplayRoots(baseline.DiscoveredProjectRoots)}` storage resolution `{DisplayValue(baseline.StorageResolutionKind)}` storage authority root `{DisplayValue(baseline.StorageAuthorityRoot)}` compatible storage roots `{DisplayRoots(baseline.CompatibleStorageRoots)}`. Ignored generated roots: {DisplayRoots(baseline.ExcludedGeneratedRoots)}.";
    }

    private static string DisplayRoots(IReadOnlyList<string> roots)
    {
        return roots.Count == 0 ? "(none)" : string.Join(", ", roots);
    }

    private static List<string> BuildDeclaredPathList(string solutionPath, IReadOnlyList<string> allowedRoots)
    {
        var results = new List<string>();
        AddIfMeaningful(results, solutionPath);
        foreach (var root in allowedRoots)
            AddIfMeaningful(results, root);
        return results;
    }

    private static string DisplayValue(params string[] values)
    {
        var value = FirstNonEmpty(values);
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static string NormalizeRootPath(string? path)
    {
        return NormalizePath(path).TrimEnd('/');
    }

    private static string NormalizePath(string? path)
    {
        return (path ?? "").Replace('\\', '/').Trim();
    }

    private static IReadOnlyList<string> SplitMultiValueArgument(ToolRequest request, string key)
    {
        return ReadArgument(request, key)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string ReadArgument(ToolRequest request, string key)
    {
        return request.Arguments.TryGetValue(key, out var value) ? value ?? "" : "";
    }

    private static void AddIfMeaningful(ICollection<string> values, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
            values.Add(candidate.Trim());
    }

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
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

    private static bool SetEquals(ISet<string> left, IReadOnlyList<string> right)
    {
        return left.SetEquals(right.Select(NormalizeRootPath).Where(path => !string.IsNullOrWhiteSpace(path)));
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
