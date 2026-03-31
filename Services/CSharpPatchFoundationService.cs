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

    private readonly CSharpEditSurfacePlannerService _editSurfacePlannerService = new();
    private readonly CSharpGenerationArgumentResolverService _generationArgumentResolverService = new();
    private readonly CSharpModificationIntentResolverService _modificationIntentResolverService = new();
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

        var truthProject = _workspaceTruthQueryService.GetProjectByPathOrName(_ramDbService, workspaceRoot, proposal.TargetProjectPath);
        var targetSolutionPath = NormalizeRelativePath(FirstNonEmpty(
            truthProject?.SolutionPaths.FirstOrDefault(),
            ExtractSolutionPath(proposal.TargetProjectPath)));
        var intent = _modificationIntentResolverService.ResolveForExplicitIntent(
            "repair",
            proposal.TargetFilePath,
            proposal.ProposedActionType,
            targetProject: truthProject?.ProjectName ?? proposal.TargetProjectPath,
            repairCause: FirstNonEmpty(proposal.FailureSummary, proposal.FailureKind));
        var contract = BuildContractCore(
            workspaceRoot,
            ResolveMutationFamily("", proposal.FailureKind),
            ResolveOperationKind(proposal.ProposedActionType, ""),
            proposal.TargetFilePath,
            proposal.TargetProjectPath,
            targetSolutionPath,
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

        var targetSymbols = new[] { proposal.ReferencedSymbolName, proposal.ReferencedMemberName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var planner = BuildPlanner(
            workspaceRoot,
            intent,
            proposal.TargetFilePath,
            truthProject,
            [proposal.TargetProjectPath, targetSolutionPath],
            targetSymbols,
            registrationSurface: "",
            testUpdateScope: string.Equals(proposal.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase) ? "targeted_test_rerun" : "",
            requestedNamespaceConstraints: BuildNamespaceConstraints(proposal.TargetFilePath, proposal.TargetProjectPath),
            requestedDependencyUpdateRequirements: BuildDependencyRequirements(proposal.ProposedActionType));

        ApplyModificationMetadata(
            workspaceRoot,
            contract,
            modificationIntent: "repair",
            targetSurfaceType: FirstNonEmpty(intent.TargetSurfaceType, ResolveTargetSurfaceType(contract.TargetFiles.FirstOrDefault(), proposal.TargetProjectPath, proposal.ProposedActionType)),
            targetSymbols: planner.RelatedSymbols.Count == 0 ? targetSymbols : planner.RelatedSymbols,
            supportingFiles: planner.SupportingFiles.Count == 0 ? [proposal.TargetProjectPath, targetSolutionPath] : planner.SupportingFiles,
            followThroughMode: FirstNonEmpty(planner.FollowThroughMode, "repair_followthrough"),
            completionContract: planner.CompletionContract.Count == 0 ? BuildRepairCompletionContract(proposal) : planner.CompletionContract,
            preserveConstraints: planner.PreserveConstraints.Count == 0 ? ["path_identity", "project_identity", "namespace_identity", "public_api_identity"] : planner.PreserveConstraints,
            verificationRequirements: MergeVerificationRequirements(
                BuildVerificationRequirementsForContract(contract, contract.TargetProjectPath, contract.TargetSolutionPath),
                planner.VerificationSurfaces),
            retrievalContextRequirements: planner.RetrievalContextRequirements.Count == 0 ? ["workspace_truth_required", "workspace_retrieval_current_preferred", "repair_context_packet_preferred"] : planner.RetrievalContextRequirements,
            repairCause: FirstNonEmpty(intent.RepairCause, proposal.FailureSummary, proposal.FailureKind),
            featureName: "",
            registrationSurface: planner.RegistrationSurface,
            testUpdateScope: FirstNonEmpty(planner.TestUpdateScope, string.Equals(proposal.FailureKind, "test_failure", StringComparison.OrdinalIgnoreCase) ? "targeted_test_rerun" : ""),
            namespaceConstraints: planner.NamespaceConstraints.Count == 0 ? BuildNamespaceConstraints(contract.TargetFiles.FirstOrDefault(), proposal.TargetProjectPath) : planner.NamespaceConstraints,
            dependencyUpdateRequirements: planner.DependencyUpdateRequirements.Count == 0 ? BuildDependencyRequirements(proposal.ProposedActionType) : planner.DependencyUpdateRequirements);
        ApplyPlannerMetadata(contract, truthProject?.ProjectName, intent, planner);
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
        var truthProject = _workspaceTruthQueryService.GetProjectByPathOrName(_ramDbService, workspaceRoot, targetProjectPath);
        var resolvedSolutionPath = NormalizeRelativePath(FirstNonEmpty(targetSolutionPath, truthProject?.SolutionPaths.FirstOrDefault()));
        var intent = _modificationIntentResolverService.ResolveForExplicitIntent(
            "feature_update",
            targetFilePath,
            operationKind,
            roleHint: operationKind,
            targetProject: truthProject?.ProjectName ?? targetProjectPath,
            featureName: NormalizeToken(operationKind));
        var contract = BuildContractCore(
            workspaceRoot,
            ResolveMutationFamily(mutationFamily, ""),
            ResolveOperationKind(operationKind, ""),
            targetFilePath,
            targetProjectPath,
            resolvedSolutionPath,
            "",
            "",
            0,
            "",
            rationale);
        var planner = BuildPlanner(
            workspaceRoot,
            intent,
            targetFilePath,
            truthProject,
            [targetProjectPath, resolvedSolutionPath],
            [],
            registrationSurface: "",
            testUpdateScope: "",
            requestedNamespaceConstraints: BuildNamespaceConstraints(targetFilePath, targetProjectPath),
            requestedDependencyUpdateRequirements: []);

        ApplyModificationMetadata(
            workspaceRoot,
            contract,
            modificationIntent: "feature_update",
            targetSurfaceType: FirstNonEmpty(intent.TargetSurfaceType, ResolveTargetSurfaceType(targetFilePath, targetProjectPath, operationKind)),
            targetSymbols: planner.RelatedSymbols,
            supportingFiles: planner.SupportingFiles.Count == 0 ? [targetProjectPath, resolvedSolutionPath] : planner.SupportingFiles,
            followThroughMode: FirstNonEmpty(planner.FollowThroughMode, "planned_supporting_surfaces"),
            completionContract: planner.CompletionContract.Count == 0 ? ["bounded_feature_extension", "supporting_surface_followthrough", "verification_followthrough"] : planner.CompletionContract,
            preserveConstraints: planner.PreserveConstraints.Count == 0 ? ["path_identity", "project_identity", "namespace_identity", "public_api_identity"] : planner.PreserveConstraints,
            verificationRequirements: MergeVerificationRequirements(
                BuildVerificationRequirementsForContract(contract, targetProjectPath, resolvedSolutionPath),
                planner.VerificationSurfaces),
            retrievalContextRequirements: planner.RetrievalContextRequirements.Count == 0 ? ["workspace_truth_required", "workspace_retrieval_current_preferred", "scoped_feature_update_context"] : planner.RetrievalContextRequirements,
            repairCause: "",
            featureName: FirstNonEmpty(intent.FeatureName, NormalizeToken(operationKind)),
            registrationSurface: planner.RegistrationSurface,
            testUpdateScope: planner.TestUpdateScope,
            namespaceConstraints: planner.NamespaceConstraints.Count == 0 ? BuildNamespaceConstraints(targetFilePath, targetProjectPath) : planner.NamespaceConstraints,
            dependencyUpdateRequirements: planner.DependencyUpdateRequirements);
        ApplyPlannerMetadata(contract, truthProject?.ProjectName, intent, planner);
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
        var truthProject = ResolveTargetProject(workspaceRoot, request, normalizedTargetFile);
        var targetProjectPath = NormalizeRelativePath(truthProject?.RelativePath ?? "");
        var targetSolutionPath = ResolveTargetSolutionPath(request, truthProject);
        var intent = _modificationIntentResolverService.ResolveForWrite(
            request,
            targetExists,
            normalizedTargetFile,
            truthProject?.ProjectName ?? GetArgument(request, "target_project"),
            targetProjectPath);
        var modificationIntent = intent.ModificationIntent;
        if (string.IsNullOrWhiteSpace(modificationIntent))
            return null;

        var operationKind = FirstNonEmpty(
            intent.OperationKind,
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

        var generationRequest = request.Clone();
        generationRequest.Arguments["modification_intent"] = modificationIntent;
        if (!string.IsNullOrWhiteSpace(intent.FeatureName))
            generationRequest.Arguments["feature_name"] = intent.FeatureName;
        if (!string.IsNullOrWhiteSpace(intent.RepairCause))
            generationRequest.Arguments["repair_cause"] = intent.RepairCause;

        var generationArguments = _generationArgumentResolverService.Resolve(
            generationRequest,
            normalizedTargetFile,
            GetArgument(request, "namespace"),
            truthProject?.ProjectName ?? GetArgument(request, "target_project"),
            targetProjectPath,
            ResolveRetrievalReadinessStatus(workspaceRoot),
            ResolveWorkspaceTruthFingerprint(workspaceRoot));
        var targetSymbols = BuildTargetSymbols(generationArguments);
        var supportingFiles = BuildSupportingFileList(generationRequest, generationPlan);
        var registrationSurface = NormalizeRelativePath(GetArgument(generationRequest, "registration_surface"));
        var testUpdateScope = GetArgument(generationRequest, "test_update_scope");
        var planner = BuildPlanner(
            workspaceRoot,
            intent,
            normalizedTargetFile,
            truthProject,
            supportingFiles,
            targetSymbols,
            registrationSurface,
            testUpdateScope,
            BuildNamespaceConstraints(normalizedTargetFile, targetProjectPath, generationArguments.NamespaceName),
            SplitList(GetArgument(generationRequest, "dependency_update_requirements")));

        ApplyModificationMetadata(
            workspaceRoot,
            contract,
            modificationIntent,
            FirstNonEmpty(intent.TargetSurfaceType, ResolveTargetSurfaceType(normalizedTargetFile, targetProjectPath, generationArguments.Pattern)),
            planner.RelatedSymbols.Count == 0 ? targetSymbols : planner.RelatedSymbols,
            planner.SupportingFiles.Count == 0 ? supportingFiles : planner.SupportingFiles,
            FirstNonEmpty(GetArgument(generationRequest, "followthrough_mode"), planner.FollowThroughMode, generationArguments.FollowThroughMode, supportingFiles.Count > 0 ? "planned_supporting_surfaces" : "single_file"),
            planner.CompletionContract.Count == 0 ? generationArguments.CompletionContract : planner.CompletionContract,
            planner.PreserveConstraints.Count == 0 ? BuildPreserveConstraints(generationArguments, targetProjectPath) : planner.PreserveConstraints,
            MergeVerificationRequirements(
                BuildVerificationRequirementsForWrite(generationRequest, targetProjectPath, targetSolutionPath),
                planner.VerificationSurfaces),
            planner.RetrievalContextRequirements.Count == 0 ? BuildRetrievalRequirements(generationRequest, modificationIntent) : planner.RetrievalContextRequirements,
            FirstNonEmpty(GetArgument(generationRequest, "repair_cause"), intent.RepairCause),
            FirstNonEmpty(GetArgument(generationRequest, "feature_name"), generationArguments.FeatureName, intent.FeatureName),
            FirstNonEmpty(planner.RegistrationSurface, registrationSurface),
            FirstNonEmpty(planner.TestUpdateScope, testUpdateScope),
            planner.NamespaceConstraints.Count == 0 ? BuildNamespaceConstraints(normalizedTargetFile, targetProjectPath, generationArguments.NamespaceName) : planner.NamespaceConstraints,
            planner.DependencyUpdateRequirements.Count == 0 ? SplitList(GetArgument(generationRequest, "dependency_update_requirements")) : planner.DependencyUpdateRequirements);
        ApplyPlannerMetadata(contract, truthProject?.ProjectName, intent, planner);
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
        var plannedEdits = new List<CSharpPatchPlannedEditRecord>
        {
            new()
            {
                FilePath = NormalizeRelativePath(draft.TargetFilePath),
                DraftKind = draft.DraftKind,
                StartLine = draft.StartLine,
                EndLine = draft.EndLine,
                CanApplyLocally = draft.CanApplyLocally,
                IntentSummary = FirstNonEmpty(draft.RationaleSummary, proposal.Rationale, proposal.Title)
            }
        };

        foreach (var supportingFile in contract.SupportingFiles)
        {
            var normalizedSupporting = NormalizeRelativePath(supportingFile);
            if (string.IsNullOrWhiteSpace(normalizedSupporting)
                || string.Equals(normalizedSupporting, NormalizeRelativePath(draft.TargetFilePath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            plannedEdits.Add(new CSharpPatchPlannedEditRecord
            {
                FilePath = normalizedSupporting,
                DraftKind = "supporting_surface_followthrough",
                StartLine = 1,
                EndLine = 0,
                CanApplyLocally = false,
                IntentSummary = FirstNonEmpty(contract.Rationale, proposal.Rationale, proposal.Title)
            });
        }

        return new CSharpPatchPlanRecord
        {
            PlanId = Guid.NewGuid().ToString("N"),
            ContractId = contract.ContractId,
            WorkspaceRoot = contract.WorkspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            ModificationIntent = contract.ModificationIntent,
            TargetSurfaceType = contract.TargetSurfaceType,
            TargetProject = contract.TargetProject,
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
            IntentResolutionVersion = contract.IntentResolutionVersion,
            IntentClassificationReasons = contract.IntentClassificationReasons.Count == 0 ? [] : [.. contract.IntentClassificationReasons],
            EditSurfacePlannerVersion = contract.EditSurfacePlannerVersion,
            VerificationSurfaces = contract.VerificationSurfaces.Count == 0 ? [] : [.. contract.VerificationSurfaces],
            OutOfScopeSurfaces = contract.OutOfScopeSurfaces.Count == 0 ? [] : [.. contract.OutOfScopeSurfaces],
            PlanningReasons = contract.PlanningReasons.Count == 0 ? [] : [.. contract.PlanningReasons],
            EditSurfaceFiles = contract.EditSurfaceFiles.Count == 0 ? [] : [.. contract.EditSurfaceFiles],
            PlannedEdits = plannedEdits,
            ValidationSteps = validationSteps,
            RerunRequirements = [.. contract.RerunRequirements],
            SourceProposalId = proposal.ProposalId,
            SourcePatchDraftId = draft.DraftId,
            Summary = $"Planned {DisplayValue(contract.MutationFamily)} edit for `{NormalizeRelativePath(draft.TargetFilePath)}` across {plannedEdits.Count} bounded surface(s).",
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
            RelatedArtifactIds = contract.RelatedArtifactIds.Count == 0 ? [] : [.. contract.RelatedArtifactIds],
            RelatedArtifactPaths = contract.RelatedArtifactPaths.Count == 0 ? [] : [.. contract.RelatedArtifactPaths]
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

        foreach (var supportingFile in contract.SupportingFiles)
        {
            var normalizedSupporting = NormalizeRelativePath(supportingFile);
            if (string.IsNullOrWhiteSpace(normalizedSupporting)
                || string.Equals(normalizedSupporting, NormalizeRelativePath(primaryTargetPath), StringComparison.OrdinalIgnoreCase)
                || plannedEdits.Any(edit => string.Equals(edit.FilePath, normalizedSupporting, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            plannedEdits.Add(new CSharpPatchPlannedEditRecord
            {
                FilePath = normalizedSupporting,
                DraftKind = "supporting_surface_followthrough",
                StartLine = 1,
                EndLine = 0,
                CanApplyLocally = true,
                IntentSummary = contract.Rationale
            });
        }

        if (generationPlan is not null)
        {
            foreach (var companion in generationPlan.CompanionArtifacts)
            {
                if (string.IsNullOrWhiteSpace(companion.RelativePath))
                    continue;

                var normalizedCompanion = NormalizeRelativePath(companion.RelativePath);
                if (plannedEdits.Any(edit => string.Equals(edit.FilePath, normalizedCompanion, StringComparison.OrdinalIgnoreCase)))
                    continue;

                plannedEdits.Add(new CSharpPatchPlannedEditRecord
                {
                    FilePath = normalizedCompanion,
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
            TargetProject = contract.TargetProject,
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
            IntentResolutionVersion = contract.IntentResolutionVersion,
            IntentClassificationReasons = contract.IntentClassificationReasons.Count == 0 ? [] : [.. contract.IntentClassificationReasons],
            EditSurfacePlannerVersion = contract.EditSurfacePlannerVersion,
            VerificationSurfaces = contract.VerificationSurfaces.Count == 0 ? [] : [.. contract.VerificationSurfaces],
            OutOfScopeSurfaces = contract.OutOfScopeSurfaces.Count == 0 ? [] : [.. contract.OutOfScopeSurfaces],
            PlanningReasons = contract.PlanningReasons.Count == 0 ? [] : [.. contract.PlanningReasons],
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
        var normalizedTargetPath = NormalizeRelativePath(targetFilePath);
        var normalizedProjectPath = NormalizeRelativePath(targetProjectPath);
        var normalizedSolutionPath = NormalizeRelativePath(targetSolutionPath);
        var scope = EvaluateScope(workspaceRoot, normalizedTargetPath, normalizedProjectPath, normalizedSolutionPath);

        return new CSharpPatchWorkContractRecord
        {
            ContractId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            MutationFamily = mutationFamily,
            OperationKind = operationKind,
            AllowedEditScope = scope.AllowedEditScope,
            EditScope = scope.AllowedEditScope,
            ScopeApproved = scope.ScopeApproved,
            ScopeReasonCode = scope.ReasonCode,
            ScopeSummary = scope.Summary,
            ValidationRequirements = BuildValidationRequirements(sourceFailureKind, normalizedProjectPath, normalizedSolutionPath),
            RerunRequirements = BuildRerunRequirements(sourceFailureKind, normalizedProjectPath, normalizedSolutionPath),
            TargetSolutionPath = normalizedSolutionPath,
            TargetProjectPath = normalizedProjectPath,
            TargetFiles = string.IsNullOrWhiteSpace(normalizedTargetPath) ? [] : [normalizedTargetPath],
            AllowedExtensions = [.. AllowedExtensions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)],
            SourceFailureKind = sourceFailureKind,
            SourceProposalId = sourceProposalId,
            SourceArtifactId = sourceArtifactId,
            SourceArtifactType = sourceArtifactType,
            Rationale = FirstNonEmpty(rationale, BuildDefaultRationale(mutationFamily, normalizedTargetPath)),
            RelatedArtifactIds = sourceArtifactId > 0 ? [sourceArtifactId] : []
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
        contract.ModificationIntent = modificationIntent;
        contract.TargetSurfaceType = targetSurfaceType;
        contract.TargetSymbols = targetSymbols
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        contract.SupportingFiles = supportingFiles
            .Select(NormalizeRelativePath)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => !contract.TargetFiles.Any(target => string.Equals(target, value, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        contract.FollowThroughMode = FirstNonEmpty(followThroughMode, contract.SupportingFiles.Count > 0 ? "planned_supporting_surfaces" : "single_file");
        contract.CompletionContract = completionContract
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        contract.PreserveConstraints = preserveConstraints
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        contract.VerificationRequirements = verificationRequirements
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        contract.RetrievalContextRequirements = retrievalContextRequirements
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        contract.RepairCause = repairCause;
        contract.FeatureName = featureName;
        contract.RegistrationSurface = NormalizeRelativePath(registrationSurface);
        contract.TestUpdateScope = testUpdateScope;
        contract.NamespaceConstraints = namespaceConstraints
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        contract.DependencyUpdateRequirements = dependencyUpdateRequirements
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        contract.RetrievalReadinessStatus = ResolveRetrievalReadinessStatus(workspaceRoot);
        contract.WorkspaceTruthFingerprint = ResolveWorkspaceTruthFingerprint(workspaceRoot);
        contract.EditSurfaceFiles = BuildEditSurfaceFiles(
            workspaceRoot,
            contract.TargetProjectPath,
            contract.TargetFiles,
            contract.SupportingFiles,
            contract.TargetSymbols);
    }

    private void ApplyPlannerMetadata(
        CSharpPatchWorkContractRecord contract,
        string targetProjectName,
        CSharpModificationIntentRecord intent,
        CSharpEditSurfacePlanRecord planner)
    {
        contract.TargetProject = FirstNonEmpty(planner.TargetProject, targetProjectName);
        contract.IntentResolutionVersion = intent.ResolverVersion;
        contract.IntentClassificationReasons = intent.ClassificationReasons.Count == 0 ? [] : [.. intent.ClassificationReasons];
        contract.EditSurfacePlannerVersion = planner.PlannerVersion;
        contract.VerificationSurfaces = planner.VerificationSurfaces.Count == 0 ? [] : [.. planner.VerificationSurfaces];
        contract.OutOfScopeSurfaces = planner.OutOfScopeSurfaces.Count == 0 ? [] : [.. planner.OutOfScopeSurfaces];
        contract.PlanningReasons = planner.PlanningReasons.Count == 0 ? [] : [.. planner.PlanningReasons];
        if (planner.TargetFiles.Count > 0)
            contract.TargetFiles = [.. planner.TargetFiles];
        if (planner.SupportingFiles.Count > 0)
            contract.SupportingFiles = [.. planner.SupportingFiles];
        if (planner.EditSurfaceFiles.Count > 0)
            contract.EditSurfaceFiles = [.. planner.EditSurfaceFiles];
        if (!string.IsNullOrWhiteSpace(planner.EditScope))
            contract.EditScope = planner.EditScope;
        if (!string.IsNullOrWhiteSpace(planner.FollowThroughMode))
            contract.FollowThroughMode = planner.FollowThroughMode;
        if (!string.IsNullOrWhiteSpace(planner.RegistrationSurface))
            contract.RegistrationSurface = planner.RegistrationSurface;
        if (!string.IsNullOrWhiteSpace(planner.TestUpdateScope))
            contract.TestUpdateScope = planner.TestUpdateScope;
        if (planner.RetrievalContextRequirements.Count > 0)
            contract.RetrievalContextRequirements = [.. planner.RetrievalContextRequirements];
        if (planner.CompletionContract.Count > 0)
            contract.CompletionContract = [.. planner.CompletionContract];
        if (planner.PreserveConstraints.Count > 0)
            contract.PreserveConstraints = [.. planner.PreserveConstraints];
        if (planner.NamespaceConstraints.Count > 0)
            contract.NamespaceConstraints = [.. planner.NamespaceConstraints];
        if (planner.DependencyUpdateRequirements.Count > 0)
            contract.DependencyUpdateRequirements = [.. planner.DependencyUpdateRequirements];
    }

    private CSharpEditSurfacePlanRecord BuildPlanner(
        string workspaceRoot,
        CSharpModificationIntentRecord intent,
        string targetFilePath,
        WorkspaceProjectRecord? targetProject,
        IReadOnlyList<string> requestedSupportingFiles,
        IReadOnlyList<string> targetSymbols,
        string registrationSurface,
        string testUpdateScope,
        IReadOnlyList<string> requestedNamespaceConstraints,
        IReadOnlyList<string> requestedDependencyUpdateRequirements)
    {
        return _editSurfacePlannerService.BuildPlan(
            workspaceRoot,
            _workspaceTruthQueryService.LoadLatestSnapshot(_ramDbService, workspaceRoot),
            _workspaceTruthQueryService.LoadLatestProjectGraph(_ramDbService, workspaceRoot),
            _workspacePreparationQueryService.LoadLatestRetrievalCatalog(_ramDbService, workspaceRoot),
            intent,
            targetFilePath,
            targetProject,
            requestedSupportingFiles,
            targetSymbols,
            registrationSurface,
            testUpdateScope,
            requestedNamespaceConstraints,
            requestedDependencyUpdateRequirements);
    }

    private List<CSharpModificationSurfaceRecord> BuildEditSurfaceFiles(
        string workspaceRoot,
        string fallbackProjectPath,
        IReadOnlyList<string> targetFiles,
        IReadOnlyList<string> supportingFiles,
        IReadOnlyList<string> targetSymbols)
    {
        var snapshot = _workspaceTruthQueryService.LoadLatestSnapshot(_ramDbService, workspaceRoot);
        var projectGraph = _workspaceTruthQueryService.LoadLatestProjectGraph(_ramDbService, workspaceRoot);
        var retrievalCatalog = _workspacePreparationQueryService.LoadLatestRetrievalCatalog(_ramDbService, workspaceRoot);
        var surfaces = new List<CSharpModificationSurfaceRecord>();
        foreach (var targetFile in targetFiles)
            AddEditSurfaceRecord(surfaces, snapshot, projectGraph, retrievalCatalog, targetFile, "primary_target", "deterministic_target_surface", fallbackProjectPath, targetSymbols);
        foreach (var supportingFile in supportingFiles)
            AddEditSurfaceRecord(surfaces, snapshot, projectGraph, retrievalCatalog, supportingFile, "supporting_surface", "supporting_surface_followthrough", fallbackProjectPath, targetSymbols);
        return surfaces;
    }

    private void AddEditSurfaceRecord(
        ICollection<CSharpModificationSurfaceRecord> surfaces,
        WorkspaceSnapshotRecord? snapshot,
        WorkspaceProjectGraphRecord? projectGraph,
        WorkspaceRetrievalCatalogRecord? retrievalCatalog,
        string candidatePath,
        string surfaceRole,
        string inclusionReason,
        string fallbackProjectPath,
        IReadOnlyList<string> targetSymbols)
    {
        var normalizedPath = NormalizeRelativePath(candidatePath);
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

    private string ResolveWorkspaceTruthFingerprint(string workspaceRoot)
    {
        return FirstNonEmpty(
            _workspacePreparationQueryService.LoadLatestPreparationState(_ramDbService, workspaceRoot)?.TruthFingerprint,
            _workspaceTruthQueryService.LoadLatestSnapshot(_ramDbService, workspaceRoot)?.SnapshotId,
            _workspaceTruthQueryService.LoadLatestProjectGraph(_ramDbService, workspaceRoot)?.GraphId);
    }

    private string ResolveRetrievalReadinessStatus(string workspaceRoot)
    {
        return _workspacePreparationQueryService.GetRetrievalReadinessStatus(_ramDbService, workspaceRoot);
    }

    private static List<string> MergeVerificationRequirements(
        IReadOnlyList<string> existing,
        IReadOnlyList<string> verificationSurfaces)
    {
        var values = new List<string>();
        values.AddRange(existing.Where(value => !string.IsNullOrWhiteSpace(value)));
        foreach (var surface in verificationSurfaces)
        {
            var normalized = NormalizeRelativePath(surface);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;
            if (values.Any(value => value.EndsWith($":{normalized}", StringComparison.OrdinalIgnoreCase)))
                continue;
            values.Add($"verify:{normalized}");
        }

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        return (path ?? "").Replace('\\', '/').Trim().Trim('/');
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
