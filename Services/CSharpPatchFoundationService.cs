using System.IO;
using RAM.Models;

namespace RAM.Services;

public sealed class CSharpPatchFoundationService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csproj",
        ".sln",
        ".props",
        ".targets",
        ".xaml",
        ".resx"
    };

    private static readonly string[] DisallowedPathSegments =
    [
        "/.git/",
        "/.ram/",
        "/bin/",
        "/obj/"
    ];

    private readonly CSharpGenerationArgumentResolverService _generationArgumentResolverService = new();
    private readonly FileIdentityService _fileIdentityService = new();
    private readonly RamDbService _ramDbService = new();
    private readonly WorkspacePreparationQueryService _workspacePreparationQueryService = new();
    private readonly WorkspaceTruthQueryService _workspaceTruthQueryService = new();

    public CSharpPatchWorkContractRecord? TryBuildRepairContract(string workspaceRoot, RepairProposalRecord proposal)
    {
        if (proposal is null)
            throw new ArgumentNullException(nameof(proposal));

        if (!IsCSharpApplicable(proposal.TargetFilePath, proposal.TargetProjectPath))
            return null;

        var contract = BuildContractCore(
            workspaceRoot,
            ResolveMutationFamily("", proposal.FailureKind),
            ResolveOperationKind(proposal.ProposedActionType, ""),
            proposal.TargetFilePath,
            proposal.TargetProjectPath,
            ExtractSolutionPath(proposal.TargetProjectPath),
            proposal.FailureKind,
            proposal.ProposalId,
            proposal.SourceArtifactId,
            proposal.SourceArtifactType,
            proposal.Rationale);
        contract.RetrievalBackend = proposal.RetrievalBackend;
        contract.RetrievalEmbedderModel = proposal.RetrievalEmbedderModel;
        contract.RetrievalQueryKind = proposal.RetrievalQueryKind;
        contract.RetrievalHitCount = proposal.RetrievalHitCount;
        contract.RetrievalSourceKinds = [.. proposal.RetrievalSourceKinds];
        contract.RetrievalQueryArtifactRelativePath = proposal.RetrievalQueryArtifactRelativePath;
        contract.RetrievalResultArtifactRelativePath = proposal.RetrievalResultArtifactRelativePath;
        contract.RetrievalContextPacketArtifactRelativePath = proposal.RetrievalContextPacketArtifactRelativePath;
        contract.RetrievalIndexBatchArtifactRelativePath = proposal.RetrievalIndexBatchArtifactRelativePath;
        ApplyModificationMetadata(
            workspaceRoot,
            contract,
            modificationIntent: "repair",
            targetSurfaceType: ResolveTargetSurfaceType(contract.TargetFiles.FirstOrDefault(), proposal.TargetProjectPath, proposal.ProposedActionType),
            targetSymbols: [proposal.ReferencedSymbolName, proposal.ReferencedMemberName],
            supportingFiles: [proposal.TargetProjectPath, contract.TargetSolutionPath],
            followThroughMode: "repair_followthrough",
            completionContract: BuildRepairCompletionContract(proposal),
            preserveConstraints: ["path_identity", "project_identity", "namespace_identity", "public_api_identity"],
            verificationRequirements: BuildVerificationRequirementsForContract(contract, contract.TargetProjectPath, contract.TargetSolutionPath),
            retrievalContextRequirements: ["workspace_truth_required", "workspace_retrieval_current_preferred", "repair_context_packet_preferred"],
            repairCause: FirstNonEmpty(proposal.FailureSummary, proposal.FailureKind),
            featureName: "",
            registrationSurface: "",
            testUpdateScope: string.Equals(proposal.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase) ? "targeted_test_rerun" : "",
            namespaceConstraints: BuildNamespaceConstraints(contract.TargetFiles.FirstOrDefault(), proposal.TargetProjectPath),
            dependencyUpdateRequirements: BuildDependencyRequirements(proposal.ProposedActionType));
        return contract;
    }

    public CSharpPatchWorkContractRecord BuildFeatureUpdateContract(
        string workspaceRoot,
        string targetFilePath,
        string targetProjectPath,
        string targetSolutionPath,
        string operationKind,
        string rationale,
        string mutationFamily = "feature_update")
    {
        var contract = BuildContractCore(
            workspaceRoot,
            ResolveMutationFamily(mutationFamily, ""),
            ResolveOperationKind(operationKind, ""),
            targetFilePath,
            targetProjectPath,
            targetSolutionPath,
            "",
            "",
            0,
            "",
            rationale);
        ApplyModificationMetadata(
            workspaceRoot,
            contract,
            modificationIntent: "feature_update",
            targetSurfaceType: ResolveTargetSurfaceType(targetFilePath, targetProjectPath, operationKind),
            targetSymbols: [],
            supportingFiles: [targetProjectPath, targetSolutionPath],
            followThroughMode: "planned_supporting_surfaces",
            completionContract: ["bounded_feature_extension", "supporting_surface_followthrough", "verification_followthrough"],
            preserveConstraints: ["path_identity", "project_identity", "namespace_identity", "public_api_identity"],
            verificationRequirements: BuildVerificationRequirementsForContract(contract, targetProjectPath, targetSolutionPath),
            retrievalContextRequirements: ["workspace_truth_required", "workspace_retrieval_current_preferred", "scoped_feature_update_context"],
            repairCause: "",
            featureName: NormalizeToken(operationKind),
            registrationSurface: "",
            testUpdateScope: "",
            namespaceConstraints: BuildNamespaceConstraints(targetFilePath, targetProjectPath),
            dependencyUpdateRequirements: []);
        return contract;
    }

    public CSharpPatchWorkContractRecord? BuildModificationContractForWrite(
        string workspaceRoot,
        ToolRequest request,
        string targetFilePath,
        bool targetExists,
        CSharpGeneratedOutputPlanRecord? generationPlan = null)
    {
        var normalizedTargetFile = NormalizeRelativePath(targetFilePath);
        var requestedIntent = _generationArgumentResolverService.NormalizeModificationIntent(GetArgument(request, "modification_intent"));
        var modificationIntent = FirstNonEmpty(
            requestedIntent,
            targetExists ? "patch" : "");
        if (string.IsNullOrWhiteSpace(modificationIntent))
            return null;

        var truthProject = ResolveTargetProject(workspaceRoot, request, normalizedTargetFile);
        var targetProjectPath = NormalizeRelativePath(truthProject?.RelativePath ?? "");
        var targetSolutionPath = ResolveTargetSolutionPath(request, truthProject);
        var operationKind = FirstNonEmpty(
            NormalizeToken(GetArgument(request, "pattern")),
            NormalizeToken(GetArgument(request, "file_role")),
            "write_file");
        var mutationFamily = modificationIntent switch
        {
            "feature_update" => "feature_update",
            "repair" => "repair_patch",
            _ => "bug_patch"
        };
        var contract = BuildContractCore(
            workspaceRoot,
            mutationFamily,
            operationKind,
            normalizedTargetFile,
            targetProjectPath,
            targetSolutionPath,
            "",
            "",
            0,
            "",
            BuildWriteRationale(request, normalizedTargetFile, modificationIntent));

        var generationArguments = _generationArgumentResolverService.Resolve(
            request,
            normalizedTargetFile,
            GetArgument(request, "namespace"),
            truthProject?.ProjectName ?? GetArgument(request, "target_project"),
            targetProjectPath,
            _workspacePreparationQueryService.GetRetrievalReadinessStatus(_ramDbService, workspaceRoot),
            FirstNonEmpty(
                _workspacePreparationQueryService.LoadLatestPreparationState(_ramDbService, workspaceRoot)?.TruthFingerprint,
                _workspaceTruthQueryService.LoadLatestSnapshot(_ramDbService, workspaceRoot)?.SnapshotId,
                _workspaceTruthQueryService.LoadLatestProjectGraph(_ramDbService, workspaceRoot)?.GraphId));
        var supportingFiles = BuildSupportingFileList(request, generationPlan);
        var registrationSurface = NormalizeRelativePath(GetArgument(request, "registration_surface"));
        var testUpdateScope = GetArgument(request, "test_update_scope");
        ApplyModificationMetadata(
            workspaceRoot,
            contract,
            modificationIntent,
            ResolveTargetSurfaceType(normalizedTargetFile, targetProjectPath, generationArguments.Pattern),
            BuildTargetSymbols(generationArguments),
            supportingFiles,
            FirstNonEmpty(GetArgument(request, "followthrough_mode"), generationArguments.FollowThroughMode, supportingFiles.Count > 0 ? "planned_supporting_surfaces" : "single_file"),
            generationArguments.CompletionContract,
            BuildPreserveConstraints(generationArguments, targetProjectPath),
            BuildVerificationRequirementsForWrite(request, targetProjectPath, targetSolutionPath),
            BuildRetrievalRequirements(request, modificationIntent),
            GetArgument(request, "repair_cause"),
            FirstNonEmpty(GetArgument(request, "feature_name"), generationArguments.FeatureName),
            registrationSurface,
            testUpdateScope,
            BuildNamespaceConstraints(normalizedTargetFile, targetProjectPath, generationArguments.NamespaceName),
            SplitList(GetArgument(request, "dependency_update_requirements")));
        return contract;
    }

    public CSharpPatchScopeDecisionRecord ValidateDraft(
        string workspaceRoot,
        CSharpPatchWorkContractRecord contract,
        PatchDraftRecord draft)
    {
        if (contract is null)
            throw new ArgumentNullException(nameof(contract));

        if (draft is null)
            throw new ArgumentNullException(nameof(draft));

        if (!contract.ScopeApproved)
        {
            return new CSharpPatchScopeDecisionRecord
            {
                IsApplicable = true,
                ScopeApproved = false,
                ReasonCode = FirstNonEmpty(contract.ScopeReasonCode, "scope_not_approved"),
                Summary = FirstNonEmpty(contract.ScopeSummary, "The C# patch contract is not approved for local mutation."),
                AllowedEditScope = contract.AllowedEditScope,
                AllowedTargetPaths = [.. contract.TargetFiles]
            };
        }

        var normalizedDraftPath = NormalizeRelativePath(draft.TargetFilePath);
        if (contract.TargetFiles.Count == 0)
        {
            return new CSharpPatchScopeDecisionRecord
            {
                IsApplicable = true,
                ScopeApproved = false,
                ReasonCode = "missing_contract_target",
                Summary = "The C# patch contract does not declare a deterministic target file.",
                AllowedEditScope = contract.AllowedEditScope,
                BlockedTargetPaths = string.IsNullOrWhiteSpace(normalizedDraftPath) ? [] : [normalizedDraftPath]
            };
        }

        if (!contract.TargetFiles.Any(current =>
                string.Equals(NormalizeRelativePath(current), normalizedDraftPath, StringComparison.OrdinalIgnoreCase)))
        {
            return new CSharpPatchScopeDecisionRecord
            {
                IsApplicable = true,
                ScopeApproved = false,
                ReasonCode = "draft_target_outside_contract",
                Summary = $"Draft target `{normalizedDraftPath}` falls outside the approved C# patch scope.",
                AllowedEditScope = contract.AllowedEditScope,
                AllowedTargetPaths = [.. contract.TargetFiles],
                BlockedTargetPaths = [normalizedDraftPath]
            };
        }

        return EvaluateScope(workspaceRoot, normalizedDraftPath, contract.TargetProjectPath, contract.TargetSolutionPath);
    }

    public CSharpPatchPlanRecord BuildPlan(
        CSharpPatchWorkContractRecord contract,
        RepairProposalRecord proposal,
        PatchDraftRecord draft)
    {
        if (contract is null)
            throw new ArgumentNullException(nameof(contract));

        if (proposal is null)
            throw new ArgumentNullException(nameof(proposal));

        if (draft is null)
            throw new ArgumentNullException(nameof(draft));

        var validationSteps = new List<string>
        {
            "preview_patch_draft",
            draft.CanApplyLocally ? "apply_patch_draft" : "manual_review_required",
            "verify_patch_draft"
        };

        return new CSharpPatchPlanRecord
        {
            PlanId = Guid.NewGuid().ToString("N"),
            ContractId = contract.ContractId,
            WorkspaceRoot = contract.WorkspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            ModificationIntent = contract.ModificationIntent,
            TargetSurfaceType = contract.TargetSurfaceType,
            MutationFamily = contract.MutationFamily,
            OperationKind = contract.OperationKind,
            AllowedEditScope = contract.AllowedEditScope,
            EditScope = contract.EditScope,
            WarningPolicyMode = contract.WarningPolicyMode,
            TargetSolutionPath = contract.TargetSolutionPath,
            TargetProjectPath = contract.TargetProjectPath,
            TargetFiles = [.. contract.TargetFiles],
            SupportingFiles = [.. contract.SupportingFiles],
            TargetSymbols = [.. contract.TargetSymbols],
            FollowThroughMode = contract.FollowThroughMode,
            CompletionContract = [.. contract.CompletionContract],
            PreserveConstraints = [.. contract.PreserveConstraints],
            VerificationRequirements = [.. contract.VerificationRequirements],
            PreviewRequired = contract.PreviewRequired,
            RepairCause = contract.RepairCause,
            FeatureName = contract.FeatureName,
            RegistrationSurface = contract.RegistrationSurface,
            TestUpdateScope = contract.TestUpdateScope,
            NamespaceConstraints = [.. contract.NamespaceConstraints],
            DependencyUpdateRequirements = [.. contract.DependencyUpdateRequirements],
            RetrievalReadinessStatus = contract.RetrievalReadinessStatus,
            WorkspaceTruthFingerprint = contract.WorkspaceTruthFingerprint,
            EditSurfaceFiles = contract.EditSurfaceFiles.Count == 0
                ? []
                : [.. contract.EditSurfaceFiles],
            PlannedEdits =
            [
                new CSharpPatchPlannedEditRecord
                {
                    FilePath = NormalizeRelativePath(draft.TargetFilePath),
                    DraftKind = draft.DraftKind,
                    StartLine = draft.StartLine,
                    EndLine = draft.EndLine,
                    CanApplyLocally = draft.CanApplyLocally,
                    IntentSummary = FirstNonEmpty(draft.RationaleSummary, proposal.Rationale, proposal.Title)
                }
            ],
            ValidationSteps = validationSteps,
            RerunRequirements = [.. contract.RerunRequirements],
            SourceProposalId = proposal.ProposalId,
            SourcePatchDraftId = draft.DraftId,
            Summary = $"Planned {DisplayValue(contract.MutationFamily)} edit for `{NormalizeRelativePath(draft.TargetFilePath)}` within `{DisplayValue(contract.AllowedEditScope)}`.",
            Rationale = FirstNonEmpty(draft.RationaleSummary, proposal.Rationale, contract.Rationale),
            RetrievalBackend = contract.RetrievalBackend,
            RetrievalEmbedderModel = contract.RetrievalEmbedderModel,
            RetrievalQueryKind = contract.RetrievalQueryKind,
            RetrievalHitCount = contract.RetrievalHitCount,
            RetrievalSourceKinds = [.. contract.RetrievalSourceKinds],
            RetrievalQueryArtifactRelativePath = contract.RetrievalQueryArtifactRelativePath,
            RetrievalResultArtifactRelativePath = contract.RetrievalResultArtifactRelativePath,
            RetrievalContextPacketArtifactRelativePath = contract.RetrievalContextPacketArtifactRelativePath,
            RetrievalIndexBatchArtifactRelativePath = contract.RetrievalIndexBatchArtifactRelativePath,
            RelatedArtifactIds = contract.RelatedArtifactIds.Count == 0
                ? []
                : [.. contract.RelatedArtifactIds],
            RelatedArtifactPaths = contract.RelatedArtifactPaths.Count == 0
                ? []
                : [.. contract.RelatedArtifactPaths]
        };
    }

    public CSharpPatchPlanRecord BuildWritePlan(
        CSharpPatchWorkContractRecord contract,
        string primaryTargetPath,
        bool targetExisted,
        CSharpGeneratedOutputPlanRecord? generationPlan,
        string rationale)
    {
        if (contract is null)
            throw new ArgumentNullException(nameof(contract));

        var validationSteps = new List<string>
        {
            "preview_modification_scope",
            targetExisted ? "apply_existing_file_update" : "apply_new_file_generation"
        };
        if (contract.VerificationRequirements.Count > 0)
            validationSteps.Add("verify_modified_surface");

        var plannedEdits = new List<CSharpPatchPlannedEditRecord>
        {
            new()
            {
                FilePath = NormalizeRelativePath(primaryTargetPath),
                DraftKind = targetExisted ? "replace_file" : "create_file",
                StartLine = 1,
                EndLine = 0,
                CanApplyLocally = true,
                IntentSummary = FirstNonEmpty(rationale, contract.Rationale)
            }
        };

        if (generationPlan is not null)
        {
            foreach (var companion in generationPlan.CompanionArtifacts)
            {
                if (string.IsNullOrWhiteSpace(companion.RelativePath))
                    continue;

                plannedEdits.Add(new CSharpPatchPlannedEditRecord
                {
                    FilePath = NormalizeRelativePath(companion.RelativePath),
                    DraftKind = "supporting_surface_write",
                    StartLine = 1,
                    EndLine = 0,
                    CanApplyLocally = true,
                    IntentSummary = FirstNonEmpty(companion.Summary, contract.Rationale)
                });
            }
        }

        return new CSharpPatchPlanRecord
        {
            PlanId = Guid.NewGuid().ToString("N"),
            ContractId = contract.ContractId,
            WorkspaceRoot = contract.WorkspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            ModificationIntent = contract.ModificationIntent,
            TargetSurfaceType = contract.TargetSurfaceType,
            MutationFamily = contract.MutationFamily,
            OperationKind = contract.OperationKind,
            AllowedEditScope = contract.AllowedEditScope,
            EditScope = contract.EditScope,
            WarningPolicyMode = contract.WarningPolicyMode,
            TargetSolutionPath = contract.TargetSolutionPath,
            TargetProjectPath = contract.TargetProjectPath,
            TargetFiles = [.. contract.TargetFiles],
            SupportingFiles = [.. contract.SupportingFiles],
            TargetSymbols = [.. contract.TargetSymbols],
            FollowThroughMode = contract.FollowThroughMode,
            CompletionContract = [.. contract.CompletionContract],
            PreserveConstraints = [.. contract.PreserveConstraints],
            VerificationRequirements = [.. contract.VerificationRequirements],
            PreviewRequired = contract.PreviewRequired,
            RepairCause = contract.RepairCause,
            FeatureName = contract.FeatureName,
            RegistrationSurface = contract.RegistrationSurface,
            TestUpdateScope = contract.TestUpdateScope,
            NamespaceConstraints = [.. contract.NamespaceConstraints],
            DependencyUpdateRequirements = [.. contract.DependencyUpdateRequirements],
            RetrievalReadinessStatus = contract.RetrievalReadinessStatus,
            WorkspaceTruthFingerprint = contract.WorkspaceTruthFingerprint,
            EditSurfaceFiles = contract.EditSurfaceFiles.Count == 0 ? [] : [.. contract.EditSurfaceFiles],
            PlannedEdits = plannedEdits,
            ValidationSteps = validationSteps,
            RerunRequirements = [.. contract.RerunRequirements],
            Summary = $"Planned {DisplayValue(contract.ModificationIntent)} update for `{DisplayValue(primaryTargetPath)}` with {plannedEdits.Count} bounded surface(s).",
            Rationale = FirstNonEmpty(rationale, contract.Rationale),
            RetrievalBackend = contract.RetrievalBackend,
            RetrievalEmbedderModel = contract.RetrievalEmbedderModel,
            RetrievalQueryKind = contract.RetrievalQueryKind,
            RetrievalHitCount = contract.RetrievalHitCount,
            RetrievalSourceKinds = [.. contract.RetrievalSourceKinds],
            RetrievalQueryArtifactRelativePath = contract.RetrievalQueryArtifactRelativePath,
            RetrievalResultArtifactRelativePath = contract.RetrievalResultArtifactRelativePath,
            RetrievalContextPacketArtifactRelativePath = contract.RetrievalContextPacketArtifactRelativePath,
            RetrievalIndexBatchArtifactRelativePath = contract.RetrievalIndexBatchArtifactRelativePath,
            RelatedArtifactIds = contract.RelatedArtifactIds.Count == 0 ? [] : [.. contract.RelatedArtifactIds],
            RelatedArtifactPaths = contract.RelatedArtifactPaths.Count == 0 ? [] : [.. contract.RelatedArtifactPaths]
        };
    }

    private CSharpPatchWorkContractRecord BuildContractCore(
        string workspaceRoot,
        string mutationFamily,
        string operationKind,
        string targetFilePath,
        string targetProjectPath,
        string targetSolutionPath,
        string sourceFailureKind,
        string sourceProposalId,
        long sourceArtifactId,
        string sourceArtifactType,
        string rationale)
    {
        var normalizedFile = NormalizeRelativePath(targetFilePath);
        var normalizedProject = NormalizeRelativePath(targetProjectPath);
        var normalizedSolution = NormalizeRelativePath(targetSolutionPath);
        var scopeDecision = EvaluateScope(workspaceRoot, normalizedFile, normalizedProject, normalizedSolution);

        return new CSharpPatchWorkContractRecord
        {
            ContractId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            MutationFamily = mutationFamily,
            OperationKind = operationKind,
            AllowedEditScope = FirstNonEmpty(scopeDecision.AllowedEditScope, ResolveEditScope(normalizedFile)),
            ScopeApproved = scopeDecision.ScopeApproved,
            ScopeReasonCode = scopeDecision.ReasonCode,
            ScopeSummary = scopeDecision.Summary,
            ValidationRequirements = BuildValidationRequirements(sourceFailureKind, normalizedProject, normalizedSolution),
            RerunRequirements = BuildRerunRequirements(sourceFailureKind, normalizedProject, normalizedSolution),
            WarningPolicyMode = "track_only",
            TargetSolutionPath = normalizedSolution,
            TargetProjectPath = normalizedProject,
            TargetFiles = string.IsNullOrWhiteSpace(normalizedFile) ? [] : [normalizedFile],
            AllowedExtensions = [.. AllowedExtensions.OrderBy(current => current, StringComparer.OrdinalIgnoreCase)],
            SourceFailureKind = sourceFailureKind ?? "",
            SourceProposalId = sourceProposalId ?? "",
            SourceArtifactId = sourceArtifactId,
            SourceArtifactType = sourceArtifactType ?? "",
            Rationale = FirstNonEmpty(rationale, BuildDefaultRationale(mutationFamily, normalizedFile))
        };
    }

    private void ApplyModificationMetadata(
        string workspaceRoot,
        CSharpPatchWorkContractRecord contract,
        string modificationIntent,
        string targetSurfaceType,
        IReadOnlyList<string> targetSymbols,
        IReadOnlyList<string> supportingFiles,
        string followThroughMode,
        IReadOnlyList<string> completionContract,
        IReadOnlyList<string> preserveConstraints,
        IReadOnlyList<string> verificationRequirements,
        IReadOnlyList<string> retrievalContextRequirements,
        string repairCause,
        string featureName,
        string registrationSurface,
        string testUpdateScope,
        IReadOnlyList<string> namespaceConstraints,
        IReadOnlyList<string> dependencyUpdateRequirements)
    {
        var snapshot = _workspaceTruthQueryService.LoadLatestSnapshot(_ramDbService, workspaceRoot);
        var projectGraph = _workspaceTruthQueryService.LoadLatestProjectGraph(_ramDbService, workspaceRoot);
        var preparationState = _workspacePreparationQueryService.LoadLatestPreparationState(_ramDbService, workspaceRoot);
        var retrievalCatalog = _workspacePreparationQueryService.LoadLatestRetrievalCatalog(_ramDbService, workspaceRoot);
        var normalizedTargetFile = NormalizeRelativePath(contract.TargetFiles.FirstOrDefault());
        var normalizedSupportingFiles = supportingFiles
            .Select(NormalizeRelativePath)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => !string.Equals(value, normalizedTargetFile, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var normalizedRegistrationSurface = NormalizeRelativePath(registrationSurface);
        var normalizedVerificationRequirements = verificationRequirements
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var targetProject = ResolveProjectRecord(projectGraph, contract.TargetProjectPath, normalizedTargetFile);
        var targetProjectPath = NormalizeRelativePath(FirstNonEmpty(contract.TargetProjectPath, targetProject?.RelativePath));
        var targetSolutionPath = NormalizeRelativePath(FirstNonEmpty(contract.TargetSolutionPath, targetProject?.SolutionPaths.FirstOrDefault()));

        contract.ModificationIntent = NormalizeModificationIntent(modificationIntent, contract.SourceFailureKind);
        contract.TargetSurfaceType = FirstNonEmpty(targetSurfaceType, ResolveTargetSurfaceType(normalizedTargetFile, targetProjectPath, contract.OperationKind));
        contract.TargetProjectPath = targetProjectPath;
        contract.TargetSolutionPath = targetSolutionPath;
        contract.SupportingFiles = normalizedSupportingFiles;
        contract.TargetSymbols = targetSymbols
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        contract.EditScope = normalizedSupportingFiles.Count > 0 || !string.IsNullOrWhiteSpace(normalizedRegistrationSurface)
            ? "bounded_multi_surface_edit"
            : contract.AllowedEditScope;
        contract.FollowThroughMode = FirstNonEmpty(followThroughMode, normalizedSupportingFiles.Count > 0 ? "planned_supporting_surfaces" : "single_file");
        contract.CompletionContract = completionContract
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        contract.PreserveConstraints = preserveConstraints
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        contract.VerificationRequirements = normalizedVerificationRequirements.Count == 0
            ? [.. contract.ValidationRequirements]
            : normalizedVerificationRequirements;
        contract.RetrievalContextRequirements = retrievalContextRequirements
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        contract.PreviewRequired = true;
        contract.RepairCause = repairCause ?? "";
        contract.FeatureName = featureName ?? "";
        contract.RegistrationSurface = normalizedRegistrationSurface;
        contract.TestUpdateScope = testUpdateScope ?? "";
        contract.NamespaceConstraints = namespaceConstraints
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        contract.DependencyUpdateRequirements = dependencyUpdateRequirements
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        contract.RetrievalReadinessStatus = FirstNonEmpty(
            preparationState?.SyncStatus,
            preparationState?.PreparationStatus,
            _workspacePreparationQueryService.GetRetrievalReadinessStatus(_ramDbService, workspaceRoot));
        contract.WorkspaceTruthFingerprint = FirstNonEmpty(
            preparationState?.TruthFingerprint,
            snapshot?.SnapshotId,
            projectGraph?.GraphId);
        contract.EditSurfaceFiles = BuildEditSurfaceFiles(
            normalizedTargetFile,
            normalizedSupportingFiles,
            normalizedRegistrationSurface,
            contract.TargetSolutionPath,
            contract.TargetProjectPath,
            contract.TargetSymbols,
            snapshot,
            projectGraph,
            retrievalCatalog);
    }

    private List<CSharpModificationSurfaceRecord> BuildEditSurfaceFiles(
        string targetFilePath,
        IReadOnlyList<string> supportingFiles,
        string registrationSurface,
        string targetSolutionPath,
        string targetProjectPath,
        IReadOnlyList<string> targetSymbols,
        WorkspaceSnapshotRecord? snapshot,
        WorkspaceProjectGraphRecord? projectGraph,
        WorkspaceRetrievalCatalogRecord? retrievalCatalog)
    {
        var surfaces = new List<CSharpModificationSurfaceRecord>();
        AddSurfaceRecord(surfaces, targetFilePath, "primary_target", "requested_target", targetSymbols, snapshot, projectGraph, retrievalCatalog, targetProjectPath);

        foreach (var supportingFile in supportingFiles)
            AddSurfaceRecord(surfaces, supportingFile, "supporting_surface", "completion_contract_support", targetSymbols, snapshot, projectGraph, retrievalCatalog, targetProjectPath);

        if (!string.IsNullOrWhiteSpace(registrationSurface))
            AddSurfaceRecord(surfaces, registrationSurface, "registration_surface", "explicit_registration_surface", targetSymbols, snapshot, projectGraph, retrievalCatalog, targetProjectPath);

        if (!string.IsNullOrWhiteSpace(targetProjectPath))
            AddSurfaceRecord(surfaces, targetProjectPath, "project_identity", "stage0_project_truth", targetSymbols, snapshot, projectGraph, retrievalCatalog, targetProjectPath);

        if (!string.IsNullOrWhiteSpace(targetSolutionPath))
            AddSurfaceRecord(surfaces, targetSolutionPath, "verification_target", "solution_scope_verification", targetSymbols, snapshot, projectGraph, retrievalCatalog, targetProjectPath);

        return surfaces;
    }

    private void AddSurfaceRecord(
        ICollection<CSharpModificationSurfaceRecord> surfaces,
        string relativePath,
        string surfaceRole,
        string inclusionReason,
        IReadOnlyList<string> targetSymbols,
        WorkspaceSnapshotRecord? snapshot,
        WorkspaceProjectGraphRecord? projectGraph,
        WorkspaceRetrievalCatalogRecord? retrievalCatalog,
        string fallbackProjectPath)
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalizedPath)
            || surfaces.Any(current => string.Equals(current.RelativePath, normalizedPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var fileRecord = snapshot?.Files.FirstOrDefault(file => string.Equals(file.RelativePath, normalizedPath, StringComparison.OrdinalIgnoreCase));
        var projectRecord = ResolveProjectRecord(projectGraph, fileRecord?.OwningProjectPath ?? fallbackProjectPath, normalizedPath);
        var retrievalChunkCount = retrievalCatalog?.Chunks.Count(chunk => string.Equals(chunk.RelativePath, normalizedPath, StringComparison.OrdinalIgnoreCase)) ?? 0;
        var identity = fileRecord?.Identity ?? _fileIdentityService.Identify(normalizedPath);
        var evidence = new List<string>
        {
            $"inclusion={inclusionReason}",
            $"surface_role={surfaceRole}",
            $"truth_file_kind={FirstNonEmpty(fileRecord?.FileKind, identity.FileType, "(unknown)")}",
            $"truth_project={FirstNonEmpty(fileRecord?.OwningProjectPath, projectRecord?.RelativePath, "(none)")}",
            $"retrieval_chunks={retrievalChunkCount}"
        };

        surfaces.Add(new CSharpModificationSurfaceRecord
        {
            RelativePath = normalizedPath,
            SurfaceRole = surfaceRole,
            InclusionReason = inclusionReason,
            ProjectPath = FirstNonEmpty(fileRecord?.OwningProjectPath, projectRecord?.RelativePath, fallbackProjectPath),
            ProjectName = FirstNonEmpty(projectRecord?.ProjectName, identity.ProjectName),
            FileKind = FirstNonEmpty(fileRecord?.FileKind, identity.FileType),
            LogicalRole = FirstNonEmpty(identity.Role, identity.FileType),
            RetrievalChunkCount = retrievalChunkCount,
            RelatedSymbols = targetSymbols
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Evidence = evidence
        });
    }

    private CSharpPatchScopeDecisionRecord EvaluateScope(
        string workspaceRoot,
        string targetFilePath,
        string targetProjectPath,
        string targetSolutionPath)
    {
        var allowedScope = ResolveEditScope(targetFilePath);
        var normalizedTargetPath = NormalizeRelativePath(targetFilePath);
        if (string.IsNullOrWhiteSpace(normalizedTargetPath))
        {
            return Block("missing_target_file", "A deterministic C# patch target file is required before mutation.", allowedScope);
        }

        var extension = Path.GetExtension(normalizedTargetPath);
        if (!AllowedExtensions.Contains(extension))
        {
            return Block(
                "disallowed_target_extension",
                $"Target `{normalizedTargetPath}` is outside the allowed C# patch extensions for this phase.",
                allowedScope,
                blockedPaths: [normalizedTargetPath]);
        }

        var normalizedWithSeparators = "/" + normalizedTargetPath.Trim('/').Replace('\\', '/') + "/";
        if (DisallowedPathSegments.Any(segment => normalizedWithSeparators.Contains(segment, StringComparison.OrdinalIgnoreCase)))
        {
            return Block(
                "disallowed_system_path",
                $"Target `{normalizedTargetPath}` is inside a generated or system path that this phase does not allow RAM to patch directly.",
                allowedScope,
                blockedPaths: [normalizedTargetPath]);
        }

        if (!IsInsideWorkspace(workspaceRoot, normalizedTargetPath))
        {
            return Block(
                "outside_workspace",
                $"Target `{normalizedTargetPath}` falls outside the active workspace.",
                allowedScope,
                blockedPaths: [normalizedTargetPath]);
        }

        var allowedPaths = new List<string> { normalizedTargetPath };
        AddIfMeaningful(allowedPaths, targetProjectPath);
        AddIfMeaningful(allowedPaths, targetSolutionPath);

        return new CSharpPatchScopeDecisionRecord
        {
            IsApplicable = true,
            ScopeApproved = true,
            ReasonCode = "scope_approved",
            Summary = $"Approved `{allowedScope}` for `{normalizedTargetPath}` inside the active C# workspace scope.",
            AllowedEditScope = allowedScope,
            AllowedTargetPaths = allowedPaths
        };
    }

    private static string ResolveMutationFamily(string requestedFamily, string failureKind)
    {
        var normalizedRequested = NormalizeToken(requestedFamily);
        if (normalizedRequested is "feature_update" or "bug_patch" or "repair_patch" or "refactor_for_supporting_change")
            return normalizedRequested;

        return NormalizeToken(failureKind) switch
        {
            "build_failure" or "test_failure" => "repair_patch",
            _ => "bug_patch"
        };
    }

    private static string ResolveOperationKind(string proposedActionType, string fallback)
    {
        var normalized = FirstNonEmpty(NormalizeToken(proposedActionType), NormalizeToken(fallback));
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static List<string> BuildValidationRequirements(string sourceFailureKind, string targetProjectPath, string targetSolutionPath)
    {
        var requirements = new List<string> { "mandatory_post_mutation_verification" };
        if (!string.IsNullOrWhiteSpace(targetSolutionPath))
            requirements.Add("solution_scope_must_remain_buildable");
        else if (!string.IsNullOrWhiteSpace(targetProjectPath))
            requirements.Add("project_scope_must_remain_buildable");

        switch (NormalizeToken(sourceFailureKind))
        {
            case "build_failure":
                requirements.Add("rerun_dotnet_build");
                break;
            case "test_failure":
                requirements.Add("rerun_dotnet_test");
                break;
            default:
                requirements.Add("rerun_build_or_test_as_planned");
                break;
        }

        return requirements;
    }

    private static List<string> BuildRerunRequirements(string sourceFailureKind, string targetProjectPath, string targetSolutionPath)
    {
        var target = FirstNonEmpty(targetSolutionPath, targetProjectPath, "(workspace)");
        return NormalizeToken(sourceFailureKind) switch
        {
            "build_failure" => [$"dotnet_build:{target}"],
            "test_failure" => [$"dotnet_test:{target}"],
            _ => [$"verify_patch_draft:{target}"]
        };
    }

    private static List<string> BuildVerificationRequirementsForContract(
        CSharpPatchWorkContractRecord contract,
        string targetProjectPath,
        string targetSolutionPath)
    {
        var values = new List<string>();
        var verificationTarget = FirstNonEmpty(targetSolutionPath, targetProjectPath, contract.TargetFiles.FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(verificationTarget))
        {
            if (string.Equals(contract.SourceFailureKind, "test_failure", StringComparison.OrdinalIgnoreCase))
                values.Add($"dotnet_test:{verificationTarget}");
            else
                values.Add($"dotnet_build:{verificationTarget}");
        }

        return values;
    }

    private static List<string> BuildVerificationRequirementsForWrite(
        ToolRequest request,
        string targetProjectPath,
        string targetSolutionPath)
    {
        var values = SplitList(GetArgument(request, "verification_requirements"));
        var validationTarget = NormalizeRelativePath(FirstNonEmpty(GetArgument(request, "validation"), GetArgument(request, "validation_target")));
        var fallbackTarget = FirstNonEmpty(validationTarget, targetSolutionPath, targetProjectPath);
        if (values.Count == 0 && !string.IsNullOrWhiteSpace(fallbackTarget))
            values.Add($"verify:{fallbackTarget}");

        return values;
    }

    private static List<string> BuildRepairCompletionContract(RepairProposalRecord proposal)
    {
        var values = new List<string>
        {
            "bounded_mutation",
            "verification_followthrough"
        };

        if (string.Equals(proposal.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase))
            values.Add("test_target_recovery");
        else
            values.Add("build_target_recovery");

        if (!string.IsNullOrWhiteSpace(proposal.ReferencedSymbolName)
            || !string.IsNullOrWhiteSpace(proposal.ReferencedMemberName))
        {
            values.Add("symbol_continuity");
        }

        return values;
    }

    private static List<string> BuildDependencyRequirements(string operationKind)
    {
        return NormalizeToken(operationKind) switch
        {
            "update_reference" => ["preserve_reference_direction"],
            "ensure_wpf_project_settings" or "ensure_library_project_settings" => ["preserve_project_identity"],
            _ => []
        };
    }

    private static List<string> BuildRetrievalRequirements(ToolRequest request, string modificationIntent)
    {
        var values = SplitList(GetArgument(request, "retrieval_context_requirements"));
        if (values.Count > 0)
            return values;

        values.Add("workspace_truth_required");
        values.Add("workspace_retrieval_current_preferred");
        if (string.Equals(modificationIntent, "feature_update", StringComparison.OrdinalIgnoreCase))
            values.Add("scoped_feature_update_context");
        return values;
    }

    private static List<string> BuildTargetSymbols(CSharpGenerationArgumentContractRecord arguments)
    {
        return new[]
            {
                arguments.ClassName,
                arguments.ServiceName,
                arguments.DomainEntity
            }
            .Concat(arguments.Interfaces)
            .Concat(arguments.BaseTypes)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildSupportingFileList(ToolRequest request, CSharpGeneratedOutputPlanRecord? generationPlan)
    {
        var values = new List<string>();
        values.AddRange(SplitList(GetArgument(request, "target_files")));
        values.AddRange(SplitList(GetArgument(request, "supporting_files")));
        values.AddRange(SplitList(GetArgument(request, "supporting_surfaces")));
        var registrationSurface = NormalizeRelativePath(GetArgument(request, "registration_surface"));
        if (!string.IsNullOrWhiteSpace(registrationSurface))
            values.Add(registrationSurface);

        if (generationPlan is not null)
            values.AddRange(generationPlan.CompanionArtifacts.Select(item => item.RelativePath));

        return values
            .Select(value => value.Contains(':', StringComparison.Ordinal) ? value[(value.IndexOf(':', StringComparison.Ordinal) + 1)..] : value)
            .Select(NormalizeRelativePath)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildPreserveConstraints(CSharpGenerationArgumentContractRecord arguments, string targetProjectPath)
    {
        var values = new List<string>
        {
            "path_identity",
            "project_identity",
            "namespace_identity"
        };

        if (!string.IsNullOrWhiteSpace(arguments.ClassName))
            values.Add("type_identity");
        if (arguments.Interfaces.Count > 0)
            values.Add("public_contract_identity");
        if (!string.IsNullOrWhiteSpace(targetProjectPath))
            values.Add($"project_scope:{targetProjectPath}");

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildNamespaceConstraints(string? targetFilePath, string targetProjectPath, string namespaceName = "")
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(namespaceName))
            values.Add(namespaceName);

        var inferred = NormalizeRelativePath(targetFilePath);
        if (inferred.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || inferred.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileNameWithoutExtension(inferred);
            if (!string.IsNullOrWhiteSpace(fileName))
                values.Add(fileName);
        }

        if (!string.IsNullOrWhiteSpace(targetProjectPath))
            values.Add(targetProjectPath);

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private WorkspaceProjectRecord? ResolveTargetProject(string workspaceRoot, ToolRequest request, string targetFilePath)
    {
        var requestedProject = FirstNonEmpty(
            GetArgument(request, "target_project"),
            GetArgument(request, "project"));
        var truthProject = _workspaceTruthQueryService.GetProjectByPathOrName(_ramDbService, workspaceRoot, requestedProject);
        if (truthProject is not null)
            return truthProject;

        var fileRecord = _workspaceTruthQueryService.GetFileClassification(_ramDbService, workspaceRoot, targetFilePath);
        if (!string.IsNullOrWhiteSpace(fileRecord?.OwningProjectPath))
            return _workspaceTruthQueryService.GetProjectByPathOrName(_ramDbService, workspaceRoot, fileRecord.OwningProjectPath);

        return null;
    }

    private static WorkspaceProjectRecord? ResolveProjectRecord(
        WorkspaceProjectGraphRecord? projectGraph,
        string projectPathOrName,
        string targetFilePath)
    {
        if (projectGraph is null)
            return null;

        var normalized = NormalizeRelativePath(projectPathOrName);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            var match = projectGraph.Projects.FirstOrDefault(project =>
                string.Equals(project.RelativePath, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(project.ProjectName, normalized, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        var normalizedTarget = NormalizeRelativePath(targetFilePath);
        return projectGraph.Projects.FirstOrDefault(project =>
            !string.IsNullOrWhiteSpace(project.ProjectDirectory)
            && normalizedTarget.StartsWith(project.ProjectDirectory.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveTargetSolutionPath(ToolRequest request, WorkspaceProjectRecord? project)
    {
        var validationTarget = NormalizeRelativePath(FirstNonEmpty(GetArgument(request, "validation"), GetArgument(request, "validation_target")));
        if (validationTarget.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return validationTarget;

        return NormalizeRelativePath(project?.SolutionPaths.FirstOrDefault());
    }

    private static string ResolveTargetSurfaceType(string? targetFilePath, string targetProjectPath, string operationKind)
    {
        var normalizedTarget = NormalizeRelativePath(targetFilePath);
        if (normalizedTarget.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return "project";
        if (normalizedTarget.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return "solution";
        if (normalizedTarget.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            return "xaml";
        if (normalizedTarget.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return FirstNonEmpty(NormalizeToken(operationKind), "source");
        if (!string.IsNullOrWhiteSpace(targetProjectPath))
            return "project_surface";
        return "workspace_surface";
    }

    private static string NormalizeModificationIntent(string requestedIntent, string sourceFailureKind)
    {
        var normalized = NormalizeToken(requestedIntent);
        if (normalized is "repair" or "patch" or "feature_update")
            return normalized;

        return NormalizeToken(sourceFailureKind) switch
        {
            "build_failure" or "test_failure" => "repair",
            _ => "patch"
        };
    }

    private static string BuildWriteRationale(ToolRequest request, string targetFilePath, string modificationIntent)
    {
        var featureName = FirstNonEmpty(GetArgument(request, "feature_name"), GetArgument(request, "class_name"));
        var pattern = FirstNonEmpty(GetArgument(request, "pattern"), GetArgument(request, "file_role"));
        return modificationIntent switch
        {
            "feature_update" => $"Apply bounded feature update `{DisplayValue(featureName)}` to `{DisplayValue(targetFilePath)}` using the `{DisplayValue(pattern)}` contract and supporting-surface follow-through.",
            "repair" => $"Repair `{DisplayValue(targetFilePath)}` using the `{DisplayValue(pattern)}` contract while preserving project and namespace identity.",
            _ => $"Patch `{DisplayValue(targetFilePath)}` using the `{DisplayValue(pattern)}` contract and bounded supporting surfaces."
        };
    }

    private static string GetArgument(ToolRequest request, string key)
    {
        return request.TryGetArgument(key, out var value)
            ? value
            : "";
    }

    private static List<string> SplitList(string value)
    {
        return (value ?? "")
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(current => !string.IsNullOrWhiteSpace(current))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsCSharpApplicable(string targetFilePath, string targetProjectPath)
    {
        var normalizedTargetFile = NormalizeRelativePath(targetFilePath);
        var normalizedProject = NormalizeRelativePath(targetProjectPath);
        var fileExtension = Path.GetExtension(normalizedTargetFile);
        return AllowedExtensions.Contains(fileExtension)
            || normalizedProject.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || normalizedProject.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveEditScope(string targetFilePath)
    {
        var normalized = NormalizeRelativePath(targetFilePath);
        var extension = Path.GetExtension(normalized);
        if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            return "solution_linked_edit";

        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase))
        {
            return "project_local_edit";
        }

        if (normalized.Contains("/generated/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\generated\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(".g.", StringComparison.OrdinalIgnoreCase))
        {
            return "generated_scaffold_file_edit";
        }

        return "file_local_edit";
    }

    private static string ExtractSolutionPath(string targetProjectPath)
    {
        var normalized = NormalizeRelativePath(targetProjectPath);
        return normalized.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ? normalized : "";
    }

    private static bool IsInsideWorkspace(string workspaceRoot, string targetPath)
    {
        try
        {
            var workspace = Path.GetFullPath(workspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullTarget = Path.GetFullPath(Path.Combine(workspaceRoot, targetPath));
            return fullTarget.StartsWith(workspace, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static CSharpPatchScopeDecisionRecord Block(
        string reasonCode,
        string summary,
        string allowedEditScope,
        IReadOnlyList<string>? blockedPaths = null)
    {
        return new CSharpPatchScopeDecisionRecord
        {
            IsApplicable = true,
            ScopeApproved = false,
            ReasonCode = reasonCode,
            Summary = summary,
            AllowedEditScope = allowedEditScope,
            BlockedTargetPaths = blockedPaths is null ? [] : [.. blockedPaths]
        };
    }

    private static void AddIfMeaningful(ICollection<string> values, string candidate)
    {
        var normalized = NormalizeRelativePath(candidate);
        if (!string.IsNullOrWhiteSpace(normalized)
            && !values.Any(current => string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            values.Add(normalized);
        }
    }

    private static string BuildDefaultRationale(string mutationFamily, string targetFilePath)
    {
        return $"Apply a bounded {DisplayValue(mutationFamily)} change to `{DisplayValue(targetFilePath)}` and rerun deterministic verification.";
    }

    private static string NormalizeRelativePath(string? path)
    {
        return (path ?? "").Replace('\\', '/').Trim();
    }

    private static string NormalizeToken(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant();
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

    private static string DisplayValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
