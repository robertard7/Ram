using System.IO;
using RAM.Models;

namespace RAM.Services;

public sealed class CSharpEditSurfacePlannerService
{
    public const string PlannerVersion = "csharp_edit_surface_plan.v1";

    private static readonly string[] RegistrationFileNames =
    [
        "Program.cs",
        "Startup.cs",
        "DependencyInjection.cs",
        "ServiceCollectionExtensions.cs",
        "App.xaml.cs",
        "App.xaml",
        "MauiProgram.cs"
    ];

    private static readonly string[] ConfigFileNames =
    [
        "appsettings.json",
        "appsettings.Development.json"
    ];

    private readonly FileIdentityService _fileIdentityService = new();

    public CSharpEditSurfacePlanRecord BuildPlan(
        string workspaceRoot,
        WorkspaceSnapshotRecord? snapshot,
        WorkspaceProjectGraphRecord? projectGraph,
        WorkspaceRetrievalCatalogRecord? retrievalCatalog,
        CSharpModificationIntentRecord intent,
        string targetFilePath,
        WorkspaceProjectRecord? targetProject,
        IReadOnlyList<string>? requestedSupportingFiles = null,
        IReadOnlyList<string>? targetSymbols = null,
        string registrationSurface = "",
        string testUpdateScope = "",
        IReadOnlyList<string>? requestedNamespaceConstraints = null,
        IReadOnlyList<string>? requestedDependencyUpdateRequirements = null)
    {
        var normalizedTargetPath = NormalizePath(targetFilePath);
        var requestedSurfaceHints = (requestedSupportingFiles ?? [])
            .Select(NormalizePath)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fileRecord = snapshot?.Files.FirstOrDefault(file =>
            string.Equals(file.RelativePath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase));
        var identity = fileRecord?.Identity ?? _fileIdentityService.Identify(normalizedTargetPath);
        var resolvedProject = ResolveProjectRecord(projectGraph, targetProject, fileRecord, normalizedTargetPath, identity);
        var resolvedProjectPath = NormalizePath(FirstNonEmpty(resolvedProject?.RelativePath, fileRecord?.OwningProjectPath));
        var resolvedProjectName = FirstNonEmpty(resolvedProject?.ProjectName, intent.TargetProject, identity.ProjectName);
        var relatedProjects = ResolveAllowedProjects(projectGraph, resolvedProject);
        var relatedProjectPaths = relatedProjects
            .Select(project => project.RelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var targetFiles = new List<string>();
        var supportingFiles = new List<string>();
        var verificationSurfaces = new List<string>();
        var planningReasons = new List<string>();
        var inclusionReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var surfaceRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var symbolHints = BuildSymbolHints(targetSymbols, normalizedTargetPath, identity);
        var targetStem = Path.GetFileNameWithoutExtension(normalizedTargetPath);
        var subjectStem = NormalizeSubjectStem(targetStem);
        var explicitRegistrationSurface = ResolveRequestedSurfacePath(
            registrationSurface,
            snapshot,
            relatedProjects,
            relatedProjectPaths);

        void AddSurface(string candidatePath, string surfaceRole, string inclusionReason)
        {
            var normalized = NormalizePath(candidatePath);
            if (string.IsNullOrWhiteSpace(normalized))
                return;
            if (!selectedPaths.Add(normalized))
                return;

            inclusionReasons[normalized] = inclusionReason;
            surfaceRoles[normalized] = surfaceRole;
            if (string.Equals(surfaceRole, "primary_target", StringComparison.OrdinalIgnoreCase))
                targetFiles.Add(normalized);
            else
                supportingFiles.Add(normalized);
        }

        AddSurface(normalizedTargetPath, "primary_target", "deterministic_target_surface");

        foreach (var requestedSurface in requestedSurfaceHints)
        {
            var resolved = ResolveRequestedSurfacePath(requestedSurface, snapshot, relatedProjects, relatedProjectPaths);
            if (!string.IsNullOrWhiteSpace(resolved))
                AddSurface(resolved, "supporting_surface", "request_declared_supporting_surface");
        }

        foreach (var companion in ResolveInterfaceAndImplementationCompanions(subjectStem, targetStem, intent.TargetSurfaceType, snapshot, relatedProjects))
            AddSurface(companion, "supporting_surface", "interface_or_implementation_companion");

        foreach (var companion in ResolveFeatureCompanions(subjectStem, intent, snapshot, relatedProjects))
            AddSurface(companion, "supporting_surface", "bounded_feature_companion");

        foreach (var companion in ResolveViewModelCompanions(subjectStem, intent.TargetSurfaceType, snapshot, relatedProjects))
            AddSurface(companion, "supporting_surface", "viewmodel_companion");

        foreach (var companion in ResolveWorkerCompanions(subjectStem, targetStem, intent, snapshot, relatedProjects))
            AddSurface(companion, "supporting_surface", "worker_support_companion");

        if (!string.IsNullOrWhiteSpace(explicitRegistrationSurface))
        {
            AddSurface(explicitRegistrationSurface, "supporting_surface", "explicit_registration_surface");
        }
        else if (ShouldIncludeRegistrationSurface(intent.TargetSurfaceType, intent.ModificationIntent, requestedSurfaceHints))
        {
            var registrationCandidate = ResolveRegistrationSurface(snapshot, relatedProjects);
            if (!string.IsNullOrWhiteSpace(registrationCandidate))
                AddSurface(registrationCandidate, "supporting_surface", "registration_followthrough_surface");
        }

        foreach (var testSurface in ResolveTestCompanions(subjectStem, targetStem, resolvedProject, projectGraph, snapshot))
            AddSurface(testSurface, "verification_surface", "targeted_test_surface");

        if (!string.IsNullOrWhiteSpace(resolvedProjectPath))
            AddVerificationSurface(verificationSurfaces, resolvedProjectPath);
        foreach (var solutionPath in resolvedProject?.SolutionPaths ?? [])
            AddVerificationSurface(verificationSurfaces, solutionPath);
        foreach (var supportingSurface in supportingFiles.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            AddVerificationSurface(verificationSurfaces, supportingSurface);

        foreach (var verificationCandidate in supportingFiles.Where(path => path.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)))
        {
            var owningProject = snapshot?.Files.FirstOrDefault(file =>
                string.Equals(file.RelativePath, verificationCandidate, StringComparison.OrdinalIgnoreCase))?.OwningProjectPath;
            AddVerificationSurface(verificationSurfaces, owningProject);
        }

        var registrationCandidatePath = supportingFiles.FirstOrDefault(path =>
            RegistrationFileNames.Any(fileName => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase)));
        var resolvedTestUpdateScope = FirstNonEmpty(
            testUpdateScope,
            supportingFiles.Any(path => path.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase))
                ? "targeted_subject_tests"
                : "");
        var retrievalRequirements = BuildRetrievalContextRequirements(
            retrievalCatalog,
            normalizedTargetPath,
            supportingFiles,
            intent.ModificationIntent);
        var preserveConstraints = BuildPreserveConstraints(intent, resolvedProjectPath, targetStem);
        var completionContract = BuildCompletionContract(intent, supportingFiles, registrationCandidatePath, resolvedTestUpdateScope);
        var namespaceConstraints = BuildNamespaceConstraints(
            requestedNamespaceConstraints,
            identity,
            resolvedProjectPath,
            targetStem,
            symbolHints);
        var dependencyRequirements = BuildDependencyRequirements(
            requestedDependencyUpdateRequirements,
            registrationCandidatePath,
            resolvedTestUpdateScope,
            resolvedProjectPath);
        var outOfScopeSurfaces = ResolveOutOfScopeSurfaces(
            resolvedProject,
            selectedPaths,
            symbolHints);

        planningReasons.Add($"workspace_root={DisplayValue(workspaceRoot)}");
        planningReasons.Add($"target_project={DisplayValue(resolvedProjectName)}");
        planningReasons.Add($"allowed_projects={DisplayValue(string.Join(",", relatedProjects.Select(project => project.ProjectName)))}");
        planningReasons.Add($"selected_surface_count={targetFiles.Count + supportingFiles.Count}");
        planningReasons.Add($"verification_surface_count={verificationSurfaces.Count}");

        var orderedSurfacePaths = targetFiles
            .Concat(supportingFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var editSurfaceFiles = orderedSurfacePaths
            .Select(path => BuildSurfaceRecord(
                snapshot,
                projectGraph,
                retrievalCatalog,
                path,
                surfaceRoles.TryGetValue(path, out var surfaceRole) ? surfaceRole : "supporting_surface",
                inclusionReasons.TryGetValue(path, out var inclusionReason) ? inclusionReason : "bounded_edit_surface",
                resolvedProjectPath,
                symbolHints))
            .ToList();

        return new CSharpEditSurfacePlanRecord
        {
            PlannerVersion = PlannerVersion,
            ModificationIntent = intent.ModificationIntent,
            TargetSurfaceType = FirstNonEmpty(intent.TargetSurfaceType, InferSurfaceType(identity, normalizedTargetPath)),
            TargetProject = resolvedProjectName,
            TargetProjectPath = resolvedProjectPath,
            TargetFiles = targetFiles,
            SupportingFiles = supportingFiles,
            RelatedSymbols = symbolHints,
            EditScope = ResolveEditScope(normalizedTargetPath, supportingFiles),
            RetrievalContextRequirements = retrievalRequirements,
            FollowThroughMode = ResolveFollowThroughMode(intent, supportingFiles),
            CompletionContract = completionContract,
            PreserveConstraints = preserveConstraints,
            VerificationSurfaces = verificationSurfaces,
            RegistrationSurface = registrationCandidatePath,
            TestUpdateScope = resolvedTestUpdateScope,
            NamespaceConstraints = namespaceConstraints,
            DependencyUpdateRequirements = dependencyRequirements,
            OutOfScopeSurfaces = outOfScopeSurfaces,
            EditSurfaceFiles = editSurfaceFiles,
            PlanningReasons = planningReasons,
            Summary = $"Planned `{DisplayValue(intent.ModificationIntent)}` scope for `{DisplayValue(normalizedTargetPath)}` with {editSurfaceFiles.Count} bounded surface(s)."
        };
    }

    private static WorkspaceProjectRecord? ResolveProjectRecord(
        WorkspaceProjectGraphRecord? projectGraph,
        WorkspaceProjectRecord? requestedProject,
        WorkspaceFileRecord? fileRecord,
        string normalizedTargetPath,
        FileIdentityRecord identity)
    {
        if (requestedProject is not null)
            return requestedProject;
        if (projectGraph is null)
            return null;

        var projectPath = NormalizePath(fileRecord?.OwningProjectPath);
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            var exact = projectGraph.Projects.FirstOrDefault(project =>
                string.Equals(project.RelativePath, projectPath, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;
        }

        var byDirectory = projectGraph.Projects.FirstOrDefault(project =>
            !string.IsNullOrWhiteSpace(project.ProjectDirectory)
            && normalizedTargetPath.StartsWith(project.ProjectDirectory.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
        if (byDirectory is not null)
            return byDirectory;

        return projectGraph.Projects.FirstOrDefault(project =>
            string.Equals(project.ProjectName, identity.ProjectName, StringComparison.OrdinalIgnoreCase));
    }

    private static List<WorkspaceProjectRecord> ResolveAllowedProjects(
        WorkspaceProjectGraphRecord? projectGraph,
        WorkspaceProjectRecord? targetProject)
    {
        var values = new List<WorkspaceProjectRecord>();
        if (targetProject is not null)
            values.Add(targetProject);
        if (projectGraph is null || targetProject is null)
            return values;

        foreach (var reference in targetProject.ProjectReferences)
        {
            var referencedProject = projectGraph.Projects.FirstOrDefault(project =>
                string.Equals(project.RelativePath, reference.TargetPath, StringComparison.OrdinalIgnoreCase));
            if (referencedProject is not null && !values.Any(current => string.Equals(current.RelativePath, referencedProject.RelativePath, StringComparison.OrdinalIgnoreCase)))
                values.Add(referencedProject);
        }

        foreach (var project in projectGraph.Projects)
        {
            if (project.IsTestProject
                && (project.TestedProjectPaths.Any(path => string.Equals(path, targetProject.RelativePath, StringComparison.OrdinalIgnoreCase))
                    || project.ProjectReferences.Any(reference => string.Equals(reference.TargetPath, targetProject.RelativePath, StringComparison.OrdinalIgnoreCase))))
            {
                if (!values.Any(current => string.Equals(current.RelativePath, project.RelativePath, StringComparison.OrdinalIgnoreCase)))
                    values.Add(project);
            }
        }

        foreach (var project in projectGraph.Projects)
        {
            if (!project.ProjectReferences.Any(reference => string.Equals(reference.TargetPath, targetProject.RelativePath, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!values.Any(current => string.Equals(current.RelativePath, project.RelativePath, StringComparison.OrdinalIgnoreCase)))
                values.Add(project);
        }

        return values;
    }

    private static IEnumerable<string> ResolveInterfaceAndImplementationCompanions(
        string subjectStem,
        string targetStem,
        string targetSurfaceType,
        WorkspaceSnapshotRecord? snapshot,
        IReadOnlyList<WorkspaceProjectRecord> allowedProjects)
    {
        if (string.IsNullOrWhiteSpace(targetStem))
            yield break;

        var candidateNames = new List<string>();
        if (targetStem.StartsWith("I", StringComparison.Ordinal)
            && targetStem.Length > 1
            && char.IsUpper(targetStem[1]))
        {
            candidateNames.Add($"{targetStem[1..]}.cs");
        }
        else if (!string.IsNullOrWhiteSpace(subjectStem))
        {
            candidateNames.Add($"I{subjectStem}.cs");
            if (targetSurfaceType == "service")
                candidateNames.Add($"I{subjectStem}Service.cs");
            if (targetSurfaceType == "repository")
                candidateNames.Add($"I{subjectStem}Repository.cs");
        }

        foreach (var path in ResolveExistingFilesByName(snapshot, allowedProjects, candidateNames))
            yield return path;
    }

    private static IEnumerable<string> ResolveFeatureCompanions(
        string subjectStem,
        CSharpModificationIntentRecord intent,
        WorkspaceSnapshotRecord? snapshot,
        IReadOnlyList<WorkspaceProjectRecord> allowedProjects)
    {
        if (string.IsNullOrWhiteSpace(subjectStem))
            yield break;

        var candidateNames = new List<string>();
        if (intent.TargetSurfaceType is "controller" or "service" or "repository" or "dto"
            || string.Equals(intent.ModificationIntent, "feature_update", StringComparison.OrdinalIgnoreCase))
        {
            candidateNames.Add($"{subjectStem}Service.cs");
            candidateNames.Add($"I{subjectStem}Service.cs");
            candidateNames.Add($"{subjectStem}Repository.cs");
            candidateNames.Add($"I{subjectStem}Repository.cs");
            candidateNames.Add($"{subjectStem}Controller.cs");
            candidateNames.Add($"Create{subjectStem}Request.cs");
            candidateNames.Add($"Update{subjectStem}Request.cs");
            candidateNames.Add($"{subjectStem}Response.cs");
            candidateNames.Add($"{subjectStem}Dto.cs");
        }

        if (string.Equals(intent.FeatureName, "search", StringComparison.OrdinalIgnoreCase))
            candidateNames.Add($"Search{subjectStem}Request.cs");

        foreach (var path in ResolveExistingFilesByName(snapshot, allowedProjects, candidateNames))
            yield return path;
    }

    private static IEnumerable<string> ResolveViewModelCompanions(
        string subjectStem,
        string targetSurfaceType,
        WorkspaceSnapshotRecord? snapshot,
        IReadOnlyList<WorkspaceProjectRecord> allowedProjects)
    {
        if (!string.Equals(targetSurfaceType, "viewmodel", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(targetSurfaceType, "xaml", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var candidateNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(subjectStem))
            candidateNames.Add($"{subjectStem}.xaml");
        candidateNames.Add("DelegateCommand.cs");

        foreach (var path in ResolveExistingFilesByName(snapshot, allowedProjects, candidateNames))
            yield return path;
    }

    private static IEnumerable<string> ResolveWorkerCompanions(
        string subjectStem,
        string targetStem,
        CSharpModificationIntentRecord intent,
        WorkspaceSnapshotRecord? snapshot,
        IReadOnlyList<WorkspaceProjectRecord> allowedProjects)
    {
        if (intent.TargetSurfaceType != "worker_support"
            && !targetStem.Contains("Worker", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(intent.FeatureName, "logging", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(intent.FeatureName, "options", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var candidateNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(subjectStem))
            candidateNames.Add($"{subjectStem}Options.cs");
        candidateNames.AddRange(ConfigFileNames);

        foreach (var path in ResolveExistingFilesByName(snapshot, allowedProjects, candidateNames))
            yield return path;
    }

    private static bool ShouldIncludeRegistrationSurface(
        string targetSurfaceType,
        string modificationIntent,
        IReadOnlyList<string> requestedSupportingFiles)
    {
        return targetSurfaceType is "service" or "repository" or "controller" or "worker_support"
            || string.Equals(modificationIntent, "repair", StringComparison.OrdinalIgnoreCase)
            || requestedSupportingFiles.Any(path => path.Contains("registration", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveRegistrationSurface(
        WorkspaceSnapshotRecord? snapshot,
        IReadOnlyList<WorkspaceProjectRecord> allowedProjects)
    {
        return ResolveExistingFilesByName(snapshot, allowedProjects, RegistrationFileNames).FirstOrDefault() ?? "";
    }

    private static IEnumerable<string> ResolveTestCompanions(
        string subjectStem,
        string targetStem,
        WorkspaceProjectRecord? targetProject,
        WorkspaceProjectGraphRecord? projectGraph,
        WorkspaceSnapshotRecord? snapshot)
    {
        if (targetProject is null || projectGraph is null || snapshot is null)
            yield break;

        var testProjects = projectGraph.Projects
            .Where(project => project.IsTestProject
                && (project.TestedProjectPaths.Any(path => string.Equals(path, targetProject.RelativePath, StringComparison.OrdinalIgnoreCase))
                    || project.ProjectReferences.Any(reference => string.Equals(reference.TargetPath, targetProject.RelativePath, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (testProjects.Count == 0)
            yield break;

        var candidateNames = new[]
        {
            $"{targetStem}Tests.cs",
            $"{subjectStem}Tests.cs",
            $"{subjectStem}ServiceTests.cs",
            $"{subjectStem}RepositoryTests.cs",
            $"{subjectStem}ControllerTests.cs"
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        foreach (var path in ResolveExistingFilesByName(snapshot, testProjects, candidateNames))
            yield return path;
    }

    private static IEnumerable<string> ResolveExistingFilesByName(
        WorkspaceSnapshotRecord? snapshot,
        IReadOnlyList<WorkspaceProjectRecord> allowedProjects,
        IReadOnlyList<string> candidateNames)
    {
        if (snapshot is null || candidateNames.Count == 0 || allowedProjects.Count == 0)
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in allowedProjects)
        {
            foreach (var path in project.OwnedFilePaths)
            {
                var fileName = Path.GetFileName(path);
                if (!candidateNames.Any(candidate => string.Equals(candidate, fileName, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var normalized = NormalizePath(path);
                if (seen.Add(normalized))
                    yield return normalized;
            }
        }

        foreach (var file in snapshot.Files)
        {
            if (!allowedProjects.Any(project => string.Equals(project.RelativePath, file.OwningProjectPath, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!candidateNames.Any(candidate => string.Equals(candidate, file.FileName, StringComparison.OrdinalIgnoreCase)))
                continue;
            var normalized = NormalizePath(file.RelativePath);
            if (seen.Add(normalized))
                yield return normalized;
        }
    }

    private static string ResolveRequestedSurfacePath(
        string requestedSurface,
        WorkspaceSnapshotRecord? snapshot,
        IReadOnlyList<WorkspaceProjectRecord> allowedProjects,
        IReadOnlySet<string> allowedProjectPaths)
    {
        var normalized = NormalizePath(requestedSurface);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";
        if (snapshot is null)
            return normalized;

        if (snapshot.Files.Any(file => string.Equals(file.RelativePath, normalized, StringComparison.OrdinalIgnoreCase)))
            return normalized;
        if (normalized.Contains('/'))
            return "";

        foreach (var project in allowedProjects)
        {
            var match = project.OwnedFilePaths.FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path), normalized, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
                return NormalizePath(match);
        }

        var snapshotMatch = snapshot.Files.FirstOrDefault(file =>
            allowedProjectPaths.Contains(file.OwningProjectPath)
            && string.Equals(file.FileName, normalized, StringComparison.OrdinalIgnoreCase));
        return NormalizePath(snapshotMatch?.RelativePath);
    }

    private static List<string> BuildSymbolHints(
        IReadOnlyList<string>? targetSymbols,
        string normalizedTargetPath,
        FileIdentityRecord identity)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in targetSymbols ?? [])
        {
            if (!string.IsNullOrWhiteSpace(symbol))
                values.Add(symbol);
        }

        var targetStem = Path.GetFileNameWithoutExtension(normalizedTargetPath);
        if (!string.IsNullOrWhiteSpace(targetStem))
        {
            values.Add(targetStem);
            values.Add(NormalizeSubjectStem(targetStem));
        }

        if (!string.IsNullOrWhiteSpace(identity.ProjectName))
            values.Add(identity.ProjectName);

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildRetrievalContextRequirements(
        WorkspaceRetrievalCatalogRecord? retrievalCatalog,
        string normalizedTargetPath,
        IReadOnlyList<string> supportingFiles,
        string modificationIntent)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "workspace_truth_required"
        };

        if (retrievalCatalog is null)
        {
            values.Add("workspace_retrieval_current_preferred");
        }
        else
        {
            values.Add("workspace_retrieval_current");
            if (retrievalCatalog.Chunks.Any(chunk => string.Equals(chunk.RelativePath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase)))
                values.Add("primary_target_chunks_available");
            if (supportingFiles.Any(path => retrievalCatalog.Chunks.Any(chunk => string.Equals(chunk.RelativePath, path, StringComparison.OrdinalIgnoreCase))))
                values.Add("supporting_surface_chunks_available");
        }

        if (string.Equals(modificationIntent, "feature_update", StringComparison.OrdinalIgnoreCase))
            values.Add("scoped_feature_update_context");
        if (string.Equals(modificationIntent, "repair", StringComparison.OrdinalIgnoreCase))
            values.Add("repair_context_packet_preferred");

        return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> BuildPreserveConstraints(
        CSharpModificationIntentRecord intent,
        string targetProjectPath,
        string targetStem)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var constraint in intent.SuggestedPreserveConstraints)
            values.Add(constraint);

        if (!string.IsNullOrWhiteSpace(targetStem))
            values.Add("type_identity");
        if (!string.IsNullOrWhiteSpace(targetProjectPath))
            values.Add($"project_scope:{targetProjectPath}");

        return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> BuildCompletionContract(
        CSharpModificationIntentRecord intent,
        IReadOnlyList<string> supportingFiles,
        string registrationSurface,
        string testUpdateScope)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var completionItem in intent.SuggestedCompletionContract)
            values.Add(completionItem);

        if (supportingFiles.Count > 0)
            values.Add("multi_surface_followthrough");
        if (!string.IsNullOrWhiteSpace(registrationSurface))
            values.Add("registration_followthrough");
        if (!string.IsNullOrWhiteSpace(testUpdateScope))
            values.Add("test_update_followthrough");

        return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> BuildNamespaceConstraints(
        IReadOnlyList<string>? requestedNamespaceConstraints,
        FileIdentityRecord identity,
        string targetProjectPath,
        string targetStem,
        IReadOnlyList<string> symbolHints)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in requestedNamespaceConstraints ?? [])
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                values.Add(candidate);
        }

        if (!string.IsNullOrWhiteSpace(identity.NamespaceHint))
            values.Add(identity.NamespaceHint);
        if (!string.IsNullOrWhiteSpace(targetStem))
            values.Add(targetStem);
        if (!string.IsNullOrWhiteSpace(targetProjectPath))
            values.Add(targetProjectPath);
        foreach (var symbol in symbolHints.Take(4))
            values.Add(symbol);

        return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> BuildDependencyRequirements(
        IReadOnlyList<string>? requestedDependencyUpdateRequirements,
        string registrationSurface,
        string testUpdateScope,
        string targetProjectPath)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in requestedDependencyUpdateRequirements ?? [])
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                values.Add(candidate);
        }

        if (!string.IsNullOrWhiteSpace(registrationSurface))
            values.Add("preserve_registration_direction");
        if (!string.IsNullOrWhiteSpace(testUpdateScope))
            values.Add("preserve_test_reference_direction");
        if (!string.IsNullOrWhiteSpace(targetProjectPath))
            values.Add("preserve_project_identity");

        return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ResolveOutOfScopeSurfaces(
        WorkspaceProjectRecord? targetProject,
        IReadOnlySet<string> selectedPaths,
        IReadOnlyList<string> symbolHints)
    {
        if (targetProject is null)
            return [];

        return targetProject.OwnedFilePaths
            .Select(NormalizePath)
            .Where(path => !selectedPaths.Contains(path))
            .Where(path => !MatchesAnySymbolHint(path, symbolHints))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private CSharpModificationSurfaceRecord BuildSurfaceRecord(
        WorkspaceSnapshotRecord? snapshot,
        WorkspaceProjectGraphRecord? projectGraph,
        WorkspaceRetrievalCatalogRecord? retrievalCatalog,
        string path,
        string surfaceRole,
        string inclusionReason,
        string fallbackProjectPath,
        IReadOnlyList<string> symbolHints)
    {
        var normalizedPath = NormalizePath(path);
        var fileRecord = snapshot?.Files.FirstOrDefault(file =>
            string.Equals(file.RelativePath, normalizedPath, StringComparison.OrdinalIgnoreCase));
        var projectRecord = ResolveProjectRecord(projectGraph, null, fileRecord, normalizedPath, fileRecord?.Identity ?? _fileIdentityService.Identify(normalizedPath));
        var retrievalChunkCount = retrievalCatalog?.Chunks.Count(chunk =>
            string.Equals(chunk.RelativePath, normalizedPath, StringComparison.OrdinalIgnoreCase)) ?? 0;
        var identity = fileRecord?.Identity ?? _fileIdentityService.Identify(normalizedPath);

        return new CSharpModificationSurfaceRecord
        {
            RelativePath = normalizedPath,
            SurfaceRole = surfaceRole,
            InclusionReason = inclusionReason,
            ProjectPath = FirstNonEmpty(fileRecord?.OwningProjectPath, projectRecord?.RelativePath, fallbackProjectPath),
            ProjectName = FirstNonEmpty(projectRecord?.ProjectName, identity.ProjectName),
            FileKind = FirstNonEmpty(fileRecord?.FileKind, identity.FileType),
            LogicalRole = FirstNonEmpty(identity.Role, identity.FileType),
            RetrievalChunkCount = retrievalChunkCount,
            RelatedSymbols = symbolHints.ToList(),
            Evidence =
            [
                $"surface_role={surfaceRole}",
                $"inclusion_reason={inclusionReason}",
                $"truth_project={DisplayValue(FirstNonEmpty(fileRecord?.OwningProjectPath, projectRecord?.RelativePath, fallbackProjectPath))}",
                $"retrieval_chunks={retrievalChunkCount}"
            ]
        };
    }

    private static string ResolveEditScope(string targetFilePath, IReadOnlyList<string> supportingFiles)
    {
        if (supportingFiles.Any())
            return "bounded_multi_surface_edit";

        var normalized = NormalizePath(targetFilePath);
        var extension = Path.GetExtension(normalized);
        if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            return "solution_linked_edit";
        if (extension is ".csproj" or ".props" or ".targets")
            return "project_local_edit";
        return "file_local_edit";
    }

    private static string ResolveFollowThroughMode(CSharpModificationIntentRecord intent, IReadOnlyList<string> supportingFiles)
    {
        if (supportingFiles.Count > 0)
            return "planned_supporting_surfaces";
        return string.IsNullOrWhiteSpace(intent.FollowThroughMode) ? "single_file" : intent.FollowThroughMode;
    }

    private static string InferSurfaceType(FileIdentityRecord identity, string normalizedTargetPath)
    {
        if (normalizedTargetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return "project";
        if (normalizedTargetPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return "solution";
        if (normalizedTargetPath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            return "xaml";
        return identity.Role switch
        {
            "services" => "service",
            "repository" => "repository",
            "ui" => "viewmodel",
            _ => "source"
        };
    }

    private static string NormalizeSubjectStem(string value)
    {
        var stem = value ?? "";
        foreach (var suffix in new[]
                 {
                     "Controller",
                     "Service",
                     "Repository",
                     "ViewModel",
                     "Worker",
                     "HostedService",
                     "Options",
                     "Request",
                     "Response",
                     "Record",
                     "Model"
                 })
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[..^suffix.Length];
                break;
            }
        }

        if (stem.StartsWith("I", StringComparison.Ordinal)
            && stem.Length > 1
            && char.IsUpper(stem[1]))
        {
            stem = stem[1..];
        }

        return stem;
    }

    private static bool MatchesAnySymbolHint(string path, IReadOnlyList<string> symbolHints)
    {
        var fileStem = Path.GetFileNameWithoutExtension(path);
        var looseStem = ToLooseKey(fileStem);
        return symbolHints.Any(symbol =>
            !string.IsNullOrWhiteSpace(symbol)
            && (looseStem.Contains(ToLooseKey(symbol), StringComparison.OrdinalIgnoreCase)
                || ToLooseKey(symbol).Contains(looseStem, StringComparison.OrdinalIgnoreCase)));
    }

    private static void AddVerificationSurface(ICollection<string> values, string candidate)
    {
        var normalized = NormalizePath(candidate);
        if (string.IsNullOrWhiteSpace(normalized))
            return;
        if (!values.Any(current => string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase)))
            values.Add(normalized);
    }

    private static string NormalizePath(string value)
    {
        return (value ?? "").Replace('\\', '/').Trim().Trim('/');
    }

    private static string ToLooseKey(string value)
    {
        return new string((value ?? "")
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
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

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
