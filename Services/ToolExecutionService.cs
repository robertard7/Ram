using System.IO;
using System.Text;
using System.Text.Json;
using RAM.Models;
using RAM.Tools;

namespace RAM.Services;

public sealed class ToolExecutionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private static readonly Dictionary<string, DateTime> ManualOnlyCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExternalExecutionToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "run_command",
        "git_status",
        "git_diff",
        "create_dotnet_solution",
        "create_dotnet_project",
        "add_project_to_solution",
        "add_dotnet_project_reference",
        "dotnet_build",
        "dotnet_test",
        "cmake_configure",
        "cmake_build",
        "make_build",
        "ninja_build",
        "run_build_script",
        "ctest_run"
    };

    private readonly ArtifactClassificationService _artifactClassificationService = new();
    private readonly AutoValidationPlanner _autoValidationPlanner = new();
    private readonly AppendFileTool _appendFileTool = new();
    private readonly BuildOutputParsingService _buildOutputParsingService = new();
    private readonly BuildScopeAssessmentService _buildScopeAssessmentService = new();
    private readonly BuildSystemDetectionService _buildSystemDetectionService = new();
    private readonly BehaviorDepthEvidenceService _behaviorDepthEvidenceService = new();
    private readonly CSharpPatchFoundationService _cSharpPatchFoundationService = new();
    private readonly CSharpGenerationEscalationService _cSharpGenerationEscalationService = new();
    private readonly CSharpGenerationGuardrailService _cSharpGenerationGuardrailService = new();
    private readonly CSharpTemplateGenerationService _cSharpTemplateGenerationService = new();
    private readonly CommandExecutionService _commandExecutionService = new();
    private readonly AddDotnetProjectReferenceTool _addDotnetProjectReferenceTool;
    private readonly AddProjectToSolutionTool _addProjectToSolutionTool;
    private readonly ExecutionSafetyPolicyService _executionSafetyPolicyService = new();
    private readonly CMakeBuildTool _cmakeBuildTool;
    private readonly CMakeConfigureTool _cmakeConfigureTool;
    private readonly CTestRunTool _ctestRunTool;
    private readonly CreateFileTool _createFileTool = new();
    private readonly CreateDotnetProjectTool _createDotnetProjectTool;
    private readonly CreateDotnetSolutionTool _createDotnetSolutionTool;
    private readonly CreateWorkspaceTextFileTool _createWorkspaceTextFileTool = new();
    private readonly DetectBuildSystemTool _detectBuildSystemTool = new();
    private readonly DotnetBuildParser _dotnetBuildParser = new();
    private readonly DotnetBuildTool _dotnetBuildTool;
    private readonly DotnetTestResultParser _dotnetTestResultParser = new();
    private readonly DotnetTestTool _dotnetTestTool;
    private readonly DotnetProjectReferencePolicyService _dotnetProjectReferencePolicyService = new();
    private readonly ExecutionGateService _executionGateService = new();
    private readonly DotnetScaffoldSurfaceService _dotnetScaffoldSurfaceService = new();
    private readonly FileInfoTool _fileInfoTool = new();
    private readonly GitDiffTool _gitDiffTool;
    private readonly GitStatusTool _gitStatusTool;
    private readonly InspectProjectTool _inspectProjectTool;
    private readonly ListBuildProfilesTool _listBuildProfilesTool = new();
    private readonly ListProjectsTool _listProjectsTool;
    private readonly LatestActionableStateService _latestActionableStateService;
    private readonly LocalRepairPlanningService _localRepairPlanningService = new();
    private readonly MakeBuildTool _makeBuildTool;
    private readonly MakeDirTool _makeDirTool = new();
    private readonly NinjaBuildTool _ninjaBuildTool;
    private readonly ApplyPatchDraftTool _applyPatchDraftTool = new();
    private readonly OpenFailureContextTool _openFailureContextTool = new();
    private readonly PlanRepairTool _planRepairTool = new();
    private readonly PatchVerificationPlanner _patchVerificationPlanner;
    private readonly PreviewPatchDraftTool _previewPatchDraftTool = new();
    private readonly PatchDraftBuilder _patchDraftBuilder = new();
    private readonly RepairContextService _repairContextService;
    private readonly RepairEligibilityService _repairEligibilityService;
    private readonly RepairPlanInputBuilder _repairPlanInputBuilder;
    private readonly ToolErrorTranslator _toolErrorTranslator;
    private readonly ToolRegistryService _toolRegistryService;
    private readonly ReplaceInFileTool _replaceInFileTool = new();
    private readonly ReadFileChunkTool _readFileChunkTool = new();
    private readonly RunBuildScriptTool _runBuildScriptTool;
    private readonly RunCommandTool _runCommandTool;
    private readonly ToolService _toolService;
    private readonly SearchFilesTool _searchFilesTool = new();
    private readonly SearchTextTool _searchTextTool = new();
    private readonly SaveOutputTool _saveOutputTool;
    private readonly VerificationOutcomeComparer _verificationOutcomeComparer = new();
    private readonly VerifyPatchDraftTool _verifyPatchDraftTool = new();
    private readonly WorkspaceBuildIndexService _workspaceBuildIndexService = new();
    private readonly WriteFileTool _writeFileTool = new();
    private readonly WorkspaceService _workspaceService;
    private readonly RamDbService _ramDbService;
    private readonly SettingsService _settingsService;
    private readonly RamRetrievalService _ramRetrievalService;
    private readonly TaskboardMaintenanceBaselineService _taskboardMaintenanceBaselineService = new();

    public ToolExecutionService(
        ToolRegistryService toolRegistryService,
        ToolService toolService,
        SaveOutputTool saveOutputTool,
        WorkspaceService workspaceService,
        RamDbService ramDbService,
        SettingsService? settingsService = null,
        OllamaClient? ollamaClient = null)
    {
        _toolRegistryService = toolRegistryService;
        _toolErrorTranslator = new ToolErrorTranslator(toolRegistryService);
        _toolService = toolService;
        _saveOutputTool = saveOutputTool;
        _workspaceService = workspaceService;
        _ramDbService = ramDbService;
        _settingsService = settingsService ?? new SettingsService();
        _ramRetrievalService = new RamRetrievalService(
            ramDbService,
            _settingsService,
            ollamaClient);
        _runCommandTool = new RunCommandTool(_commandExecutionService);
        _gitStatusTool = new GitStatusTool(_commandExecutionService);
        _gitDiffTool = new GitDiffTool(_commandExecutionService);
        _createDotnetSolutionTool = new CreateDotnetSolutionTool(_commandExecutionService);
        _createDotnetProjectTool = new CreateDotnetProjectTool(_commandExecutionService);
        _addProjectToSolutionTool = new AddProjectToSolutionTool(_commandExecutionService);
        _addDotnetProjectReferenceTool = new AddDotnetProjectReferenceTool(_commandExecutionService);
        _cmakeConfigureTool = new CMakeConfigureTool(_commandExecutionService);
        _cmakeBuildTool = new CMakeBuildTool(_commandExecutionService);
        _ctestRunTool = new CTestRunTool(_commandExecutionService);
        _dotnetBuildTool = new DotnetBuildTool(_commandExecutionService);
        _dotnetTestTool = new DotnetTestTool(_commandExecutionService);
        _listProjectsTool = new ListProjectsTool(_workspaceBuildIndexService);
        _makeBuildTool = new MakeBuildTool(_commandExecutionService);
        _ninjaBuildTool = new NinjaBuildTool(_commandExecutionService);
        _inspectProjectTool = new InspectProjectTool();
        _runBuildScriptTool = new RunBuildScriptTool(_commandExecutionService);
        _repairContextService = new RepairContextService(_workspaceBuildIndexService, _artifactClassificationService);
        _repairEligibilityService = new RepairEligibilityService(_artifactClassificationService);
        _latestActionableStateService = new LatestActionableStateService(_artifactClassificationService);
        _repairPlanInputBuilder = new RepairPlanInputBuilder(_repairContextService, _artifactClassificationService);
        _patchVerificationPlanner = new PatchVerificationPlanner(_buildSystemDetectionService, _artifactClassificationService, _executionSafetyPolicyService, _buildScopeAssessmentService);
    }

    public ToolResult Execute(ToolRequest request)
    {
        if (request is null)
            return Failure("", "Tool request is required.", "validation_failure");

        var toolName = NormalizeToolName(request.ToolName);
        if (string.IsNullOrWhiteSpace(toolName))
            return Failure("", "Tool name is required.", "validation_failure");

        request.ToolName = toolName;
        EnsureExecutionMetadata(request);

        if (!_toolRegistryService.HasTool(toolName))
            return Failure(toolName, $"Unknown tool: {toolName}", "validation_failure");

        var maintenanceGuardFailure = TryBuildMaintenanceBaselineGuardFailure(request, toolName);
        if (maintenanceGuardFailure is not null)
            return maintenanceGuardFailure;

        try
        {
            var result = toolName switch
            {
                "list_folder" => Success(toolName, ExecuteListFolder(request)),
                "read_file" => Success(toolName, ExecuteReadFile(request)),
                "list_projects" => Success(toolName, ExecuteListProjects(request)),
                "detect_build_system" => Success(toolName, ExecuteDetectBuildSystem()),
                "list_build_profiles" => Success(toolName, ExecuteListBuildProfiles()),
                "inspect_project" => Success(toolName, ExecuteInspectProject(request)),
                "search_files" => Success(toolName, ExecuteSearchFiles(request)),
                "search_text" => Success(toolName, ExecuteSearchText(request)),
                "file_info" => Success(toolName, ExecuteFileInfo(request)),
                "read_file_chunk" => Success(toolName, ExecuteReadFileChunk(request)),
                "create_file" => ExecuteCreateFile(request),
                "write_file" => ExecuteWriteFile(request),
                "append_file" => Success(toolName, ExecuteAppendFile(request)),
                "replace_in_file" => Success(toolName, ExecuteReplaceInFile(request)),
                "make_dir" => Success(toolName, ExecuteMakeDir(request)),
                "create_dotnet_solution" => ExecuteCreateDotnetSolution(request),
                "create_dotnet_project" => ExecuteCreateDotnetProject(request),
                "add_project_to_solution" => ExecuteAddProjectToSolution(request),
                "add_dotnet_project_reference" => ExecuteAddDotnetProjectReference(request),
                "create_dotnet_page_view" => ExecuteNamedWorkspaceTextFile(request, "create_dotnet_page_view", "Dotnet page scaffold"),
                "create_dotnet_viewmodel" => ExecuteNamedWorkspaceTextFile(request, "create_dotnet_viewmodel", "Dotnet viewmodel scaffold"),
                "register_navigation" => ExecuteNamedWorkspaceTextFile(request, "register_navigation", "Navigation registration"),
                "register_di_service" => ExecuteNamedWorkspaceTextFile(request, "register_di_service", "Dependency registration"),
                "initialize_sqlite_storage_boundary" => ExecuteNamedWorkspaceTextFile(request, "initialize_sqlite_storage_boundary", "SQLite storage boundary scaffold"),
                "create_cmake_project" => ExecuteNamedWorkspaceTextFile(request, "create_cmake_project", "CMake project scaffold"),
                "create_cpp_source_file" => ExecuteNamedWorkspaceTextFile(request, "create_cpp_source_file", "C++ source scaffold"),
                "create_cpp_header_file" => ExecuteNamedWorkspaceTextFile(request, "create_cpp_header_file", "C++ header scaffold"),
                "create_c_source_file" => ExecuteNamedWorkspaceTextFile(request, "create_c_source_file", "C source scaffold"),
                "create_c_header_file" => ExecuteNamedWorkspaceTextFile(request, "create_c_header_file", "C header scaffold"),
                "run_command" => ExecuteRunCommand(request),
                "git_status" => ExecuteGitStatus(request),
                "git_diff" => ExecuteGitDiff(request),
                "dotnet_build" => ExecuteDotnetBuild(request),
                "dotnet_test" => ExecuteDotnetTest(request),
                "cmake_configure" => ExecuteCMakeConfigure(request),
                "cmake_build" => ExecuteCMakeBuild(request),
                "make_build" => ExecuteMakeBuild(request),
                "ninja_build" => ExecuteNinjaBuild(request),
                "run_build_script" => ExecuteRunBuildScript(request),
                "ctest_run" => ExecuteCTestRun(request),
                "open_failure_context" => ExecuteOpenFailureContext(request),
                "plan_repair" => ExecutePlanRepair(request),
                "preview_patch_draft" => ExecutePreviewPatchDraft(request),
                "apply_patch_draft" => ExecuteApplyPatchDraft(request),
                "verify_patch_draft" => ExecuteVerifyPatchDraft(request),
                "save_output" => Success(toolName, ExecuteSaveOutput(request)),
                "show_artifacts" => Success(toolName, ExecuteShowArtifacts()),
                "show_memory" => Success(toolName, ExecuteShowMemory()),
                _ => Failure(toolName, $"Unknown tool: {toolName}", "validation_failure")
            };

            if (!result.Success)
                result.ErrorMessage = _toolErrorTranslator.Translate(request, result);

            if (string.IsNullOrWhiteSpace(result.Summary))
                result.Summary = BuildDefaultSummary(result);

            if (string.IsNullOrWhiteSpace(result.OutcomeType))
                result.OutcomeType = result.Success ? "success" : "execution_failure";

            return result;
        }
        catch (Exception ex)
        {
            var failure = Failure(toolName, ex.Message, "execution_failure");
            failure.ErrorMessage = _toolErrorTranslator.Translate(request, failure);
            return failure;
        }
    }

    private string ExecuteListFolder(ToolRequest request)
    {
        var path = ResolveWorkspacePath(GetRequiredArgument(request, "path"));
        return _toolService.ListFolder(path);
    }

    private string ExecuteReadFile(ToolRequest request)
    {
        var path = ResolveWorkspacePath(GetRequiredArgument(request, "path"));
        var output = _toolService.ReadTextFile(path);
        RecordFileTouch(RequireWorkspace(), request, path, "read", artifactType: "artifact_reference", isProductiveTouch: true, contentChanged: false);
        return output;
    }

    private string ExecuteDetectBuildSystem()
    {
        var workspaceRoot = RequireWorkspace();
        var detection = _buildSystemDetectionService.Detect(workspaceRoot);
        PersistBuildSystemDetection(workspaceRoot, detection);
        return _detectBuildSystemTool.Format(detection);
    }

    private string ExecuteListBuildProfiles()
    {
        var workspaceRoot = RequireWorkspace();
        var detection = _buildSystemDetectionService.Detect(workspaceRoot);
        PersistBuildSystemDetection(workspaceRoot, detection);
        return _listBuildProfilesTool.Format(detection.Profiles)
            + Environment.NewLine
            + Environment.NewLine
            + _buildScopeAssessmentService.BuildGuidance(workspaceRoot, detection);
    }

    private string ExecuteListProjects(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var kind = GetOptionalArgument(request, "kind");
        return _listProjectsTool.List(workspaceRoot, kind);
    }

    private string ExecuteInspectProject(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var requestedPath = GetOptionalArgument(request, "path");
        var activeTarget = GetOptionalArgument(request, "active_target");
        var resolution = _workspaceBuildIndexService.ResolveForInspection(workspaceRoot, requestedPath, activeTarget);
        if (!resolution.Success || resolution.Item is null)
            throw new InvalidOperationException(resolution.Message);

        request.Arguments["path"] = resolution.Item.RelativePath;
        var output = _inspectProjectTool.Inspect(workspaceRoot, resolution.Item, resolution.Message);
        RecordFileTouch(workspaceRoot, request, resolution.Item.RelativePath, "read", artifactType: "artifact_reference", isProductiveTouch: true, contentChanged: false);
        return output;
    }

    private string ExecuteSearchFiles(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var pattern = GetRequiredArgument(request, "pattern");
        var maxResults = GetIntArgument(request, "max_results", 50, 1, 100);
        return _searchFilesTool.Search(workspaceRoot, pattern, maxResults);
    }

    private string ExecuteSearchText(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var pattern = GetRequiredArgument(request, "pattern");
        var maxResults = GetIntArgument(request, "max_results", 40, 1, 80);
        return _searchTextTool.Search(workspaceRoot, pattern, maxResults);
    }

    private string ExecuteFileInfo(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var path = ResolveWorkspacePath(GetRequiredArgument(request, "path"));
        var output = _fileInfoTool.Describe(workspaceRoot, path);
        RecordFileTouch(workspaceRoot, request, path, "read", artifactType: "artifact_reference", isProductiveTouch: true, contentChanged: false);
        return output;
    }

    private string ExecuteReadFileChunk(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var path = ResolveWorkspacePath(GetRequiredArgument(request, "path"));
        var startLine = GetIntArgument(request, "start_line", 1, 1, int.MaxValue);
        var lineCount = GetIntArgument(request, "line_count", 40, 1, 200);
        var output = _readFileChunkTool.ReadLines(workspaceRoot, path, startLine, lineCount);
        RecordFileTouch(workspaceRoot, request, path, "read", artifactType: "artifact_reference", isProductiveTouch: true, contentChanged: false);
        return output;
    }

    private ToolResult ExecuteCreateFile(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var path = ResolveWorkspacePath(GetRequiredArgument(request, "path"));
        var content = ResolveRequestedContent(request, workspaceRoot, path, out var generationPlan);
        return ExecuteGuardedWorkspaceTextWrite(
            request,
            workspaceRoot,
            path,
            content,
            "create_file",
            "File scaffold",
            (root, targetPath, body) => _createFileTool.Create(root, targetPath, body),
            appendAutoValidationSuffix: false,
            generationPlan);
    }

    private ToolResult ExecuteWriteFile(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var path = ResolveWorkspacePath(GetRequiredArgument(request, "path"));
        var content = ResolveRequestedContent(request, workspaceRoot, path, out var generationPlan);
        return ExecuteGuardedWorkspaceTextWrite(
            request,
            workspaceRoot,
            path,
            content,
            "write_file",
            "Workspace file write",
            (root, targetPath, body) => _writeFileTool.Write(root, targetPath, body),
            appendAutoValidationSuffix: true,
            generationPlan);
    }

    private string ExecuteAppendFile(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var path = ResolveWorkspacePath(GetRequiredArgument(request, "path"));
        var content = GetRequiredArgument(request, "content");
        var message = _appendFileTool.Append(workspaceRoot, path, content);
        var artifact = SyncArtifactFromFile(workspaceRoot, path, request);
        return message + Environment.NewLine + $"Artifact synced: {artifact.RelativePath}";
    }

    private string ExecuteReplaceInFile(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var path = ResolveWorkspacePath(GetRequiredArgument(request, "path"));
        var oldText = GetRequiredArgument(request, "old_text");
        var newText = request.TryGetArgument("new_text", out var replacementText)
            ? replacementText
            : "";
        var message = _replaceInFileTool.Replace(workspaceRoot, path, oldText, newText);
        if (!message.StartsWith("Replaced ", StringComparison.OrdinalIgnoreCase))
            return message;

        var artifact = SyncArtifactFromFile(workspaceRoot, path, request);
        return message
            + Environment.NewLine
            + $"Artifact synced: {artifact.RelativePath}"
            + BuildAutoValidationSuffixForFileChange(workspaceRoot, artifact, "replace_in_file");
    }

    private string ExecuteMakeDir(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var path = ResolveWorkspacePath(GetRequiredArgument(request, "path"));
        var existedBefore = Directory.Exists(path);
        var output = _makeDirTool.Create(workspaceRoot, path);
        RecordFileTouch(
            workspaceRoot,
            request,
            path,
            "create_dir",
            artifactType: "artifact_reference",
            contentChanged: !existedBefore);
        return output;
    }

    private ToolResult ExecuteNamedWorkspaceTextFile(ToolRequest request, string toolName, string label)
    {
        var workspaceRoot = RequireWorkspace();
        var path = ResolveWorkspacePath(GetRequiredArgument(request, "path"));
        var content = ResolveRequestedContent(request, workspaceRoot, path, out var generationPlan);
        return ExecuteGuardedWorkspaceTextWrite(
            request,
            workspaceRoot,
            path,
            content,
            toolName,
            label,
            (root, targetPath, body) => _createWorkspaceTextFileTool.Write(root, targetPath, body, label),
            appendAutoValidationSuffix: true,
            generationPlan);
    }

    private ToolResult ExecuteGuardedWorkspaceTextWrite(
        ToolRequest request,
        string workspaceRoot,
        string fullPath,
        string content,
        string toolName,
        string label,
        Func<string, string, string, string> writer,
        bool appendAutoValidationSuffix,
        CSharpGeneratedOutputPlanRecord? generationPlan = null)
    {
        var relativePath = NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, fullPath));
        var existedBeforeWrite = File.Exists(fullPath);
        var effectiveRequest = request;
        var effectiveContent = content;
        var evaluation = _cSharpGenerationGuardrailService.Evaluate(workspaceRoot, effectiveRequest, fullPath, effectiveContent);
        if (!evaluation.Accepted)
        {
            var escalation = _cSharpGenerationEscalationService.TryResolve(effectiveRequest, fullPath, evaluation);
            if (escalation.ShouldRetry)
            {
                var retryRequest = effectiveRequest.Clone();
                retryRequest.Arguments["generation_profile_override"] = escalation.ProfileOverride;
                var retriedEvaluation = _cSharpGenerationGuardrailService.Evaluate(workspaceRoot, retryRequest, fullPath, escalation.Content);
                retriedEvaluation.EscalationStatus = escalation.EscalationStatus;
                retriedEvaluation.EscalationSummary = escalation.EscalationSummary;
                retriedEvaluation.RetryStatus = retriedEvaluation.Accepted ? "retried_accepted" : "retried_rejected";
                evaluation = retriedEvaluation;
                if (retriedEvaluation.Accepted)
                {
                    effectiveRequest = retryRequest;
                    effectiveContent = escalation.Content;
                }
            }
            else
            {
                evaluation.EscalationStatus = escalation.EscalationStatus;
                evaluation.EscalationSummary = escalation.EscalationSummary;
                evaluation.RetryStatus = FirstNonEmpty(escalation.RetryStatus, evaluation.RetryStatus, "not_attempted");
            }
        }

        if (!evaluation.Accepted)
        {
            var rejectionSummary = $"{evaluation.Summary} target={relativePath}";
            var rejectionDataJson = BuildGenerationGuardrailStructuredDataJson(
                toolName,
                "generation_guardrail_rejected",
                relativePath,
                label,
                evaluation,
                fileArtifact: null,
                mutationKind: existedBeforeWrite ? "modify" : "create");
            return Failure(toolName, rejectionSummary, "generation_guardrail_rejected", rejectionSummary, rejectionDataJson);
        }

        var modificationContract = _cSharpPatchFoundationService.BuildModificationContractForWrite(
            workspaceRoot,
            effectiveRequest,
            relativePath,
            existedBeforeWrite,
            generationPlan);
        ArtifactRecord? modificationContractArtifact = null;
        ArtifactRecord? modificationPlanArtifact = null;
        CSharpPatchPlanRecord? modificationPlan = null;
        if (modificationContract is not null)
        {
            modificationContractArtifact = SaveCSharpPatchContractArtifact(workspaceRoot, modificationContract, effectiveRequest);
            modificationPlan = _cSharpPatchFoundationService.BuildWritePlan(
                modificationContract,
                relativePath,
                existedBeforeWrite,
                generationPlan,
                evaluation.Summary);
            if (modificationContractArtifact is not null)
            {
                modificationPlan.RelatedArtifactIds.Add(modificationContractArtifact.Id);
                AddUniquePath(modificationPlan.RelatedArtifactPaths, modificationContractArtifact.RelativePath);
            }

            modificationPlanArtifact = SaveCSharpPatchPlanArtifact(workspaceRoot, modificationPlan, effectiveRequest);
        }

        var message = writer(workspaceRoot, fullPath, effectiveContent);
        var artifact = SyncArtifactFromFile(workspaceRoot, fullPath, effectiveRequest);
        var companionArtifacts = WriteCompanionArtifacts(workspaceRoot, effectiveRequest, generationPlan);
        var behaviorDepthEvidence = _behaviorDepthEvidenceService.Build(workspaceRoot, fullPath, toolName, evaluation);
        var behaviorDepthArtifact = SaveBehaviorDepthArtifact(workspaceRoot, behaviorDepthEvidence);
        var output = new StringBuilder()
            .AppendLine(message)
            .Append($"Artifact synced: {artifact.RelativePath}");
        if (companionArtifacts.Count > 0)
        {
            output.AppendLine();
            output.Append($"Companion artifacts synced: {string.Join(", ", companionArtifacts.Select(item => item.RelativePath))}");
        }
        if (modificationContractArtifact is not null)
            output.AppendLine().Append($"Modification contract synced: {modificationContractArtifact.RelativePath}");
        if (modificationPlanArtifact is not null)
            output.AppendLine().Append($"Modification plan synced: {modificationPlanArtifact.RelativePath}");
        output.AppendLine()
            .Append($"Behavior depth artifact synced: {behaviorDepthArtifact.RelativePath}");
        if (appendAutoValidationSuffix)
            output.Append(BuildAutoValidationSuffixForFileChange(workspaceRoot, artifact, toolName));

        var summary = $"{evaluation.Summary} target={relativePath}";
        var structuredDataJson = BuildGenerationGuardrailStructuredDataJson(
            toolName,
            "success",
            relativePath,
            label,
            evaluation,
            artifact,
            existedBeforeWrite ? "modify" : "create",
            behaviorDepthEvidence,
            behaviorDepthArtifact,
            companionArtifacts,
            modificationContract,
            modificationContractArtifact,
            modificationPlan,
            modificationPlanArtifact);
        return Success(toolName, output.ToString(), summary, "success", structuredDataJson);
    }

    private List<ArtifactRecord> WriteCompanionArtifacts(
        string workspaceRoot,
        ToolRequest request,
        CSharpGeneratedOutputPlanRecord? generationPlan)
    {
        var artifacts = new List<ArtifactRecord>();
        if (generationPlan is null || generationPlan.CompanionArtifacts.Count == 0)
            return artifacts;

        foreach (var companion in generationPlan.CompanionArtifacts)
        {
            if (string.IsNullOrWhiteSpace(companion.RelativePath) || string.IsNullOrWhiteSpace(companion.Content))
                continue;

            var fullPath = ResolveWorkspacePath(companion.RelativePath);
            var normalizedRelativePath = NormalizeRelativePath(companion.RelativePath);
            var existingContent = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";
            if (!string.Equals(existingContent, companion.Content, StringComparison.Ordinal))
                _writeFileTool.Write(workspaceRoot, fullPath, companion.Content);

            var companionRequest = request.Clone();
            companionRequest.Arguments["path"] = normalizedRelativePath;
            companionRequest.Arguments["content"] = companion.Content;
            if (!string.IsNullOrWhiteSpace(companion.Pattern))
                companionRequest.Arguments["pattern"] = companion.Pattern;
            if (!string.IsNullOrWhiteSpace(companion.FileRole))
                companionRequest.Arguments["role"] = companion.FileRole;

            artifacts.Add(SyncArtifactFromFile(workspaceRoot, fullPath, companionRequest));
        }

        return artifacts;
    }

    private string ResolveRequestedContent(ToolRequest request, string workspaceRoot, string fullPath, out CSharpGeneratedOutputPlanRecord? generationPlan)
    {
        generationPlan = null;
        var relativePath = NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, fullPath));
        if (_cSharpTemplateGenerationService.TryGeneratePlan(request, relativePath, out var generatedPlan))
        {
            var explicitContent = request.TryGetArgument("content", out var requestedContent)
                ? requestedContent
                : "";
            generationPlan = generatedPlan;
            request.Arguments["content"] = generatedPlan.PrimaryArtifact.Content;
            request.Arguments["template_generation_summary"] = generatedPlan.TemplateGenerationSummary;
            request.Arguments["template_generation_override"] = (!string.IsNullOrWhiteSpace(explicitContent)
                && !string.Equals(explicitContent, generatedPlan.PrimaryArtifact.Content, StringComparison.Ordinal))
                ? "true"
                : "false";
            return generatedPlan.PrimaryArtifact.Content;
        }

        if (request.TryGetArgument("content", out var fallbackContent)
            && !string.IsNullOrWhiteSpace(fallbackContent))
        {
            return fallbackContent;
        }

        return GetRequiredArgument(request, "content");
    }

    private ToolResult ExecuteCreateDotnetSolution(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var solutionName = GetRequiredArgument(request, "solution_name");
        var workingDirectory = GetOptionalArgument(request, "working_directory");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 60, 1, 300);
        var targetPath = NormalizeRelativePath($"{solutionName}.sln");
        var fullTargetPath = ResolveWorkspacePath(targetPath);

        if (File.Exists(fullTargetPath))
        {
            var summary = $"Solution already exists: {targetPath}.";
            RecordExecutionSuccess(workspaceRoot, "create_dotnet_solution", targetPath, summary, BuildSimpleDataJson("create_dotnet_solution", "success", targetPath, summary));
            return Success("create_dotnet_solution", summary, summary, "success", BuildSimpleDataJson("create_dotnet_solution", "success", targetPath, summary));
        }

        var gateDecision = BuildExecutionGateDecision(request, "create_dotnet_solution", "dotnet", true, "safe_narrow", targetPath);
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("create_dotnet_solution", gateDecision, targetPath);

        var result = _createDotnetSolutionTool.Run(workspaceRoot, solutionName, workingDirectory, timeoutSeconds, gateDecision);
        var (validationPassed, validationSummary) = ValidateCreatedFile(targetPath, fullTargetPath, "Solution");
        var details = string.Join(
            Environment.NewLine,
            $"Created .NET solution target: {targetPath}",
            _commandExecutionService.FormatCompactResult("dotnet new sln", result));
        return BuildBuilderCommandResult("create_dotnet_solution", workspaceRoot, targetPath, validationPassed, validationSummary, details, result);
    }

    private ToolResult ExecuteCreateDotnetProject(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var requestedTemplate = GetRequiredArgument(request, "template");
        if (!_dotnetScaffoldSurfaceService.TryResolve(requestedTemplate, out var templateRecord))
            return Failure("create_dotnet_project", $"Unsupported .NET scaffold template `{requestedTemplate}`.", "validation_failure");
        if (!string.Equals(templateRecord.SupportStatus, "supported_complete", StringComparison.OrdinalIgnoreCase))
            return Failure("create_dotnet_project", $"Deferred .NET scaffold template `{templateRecord.TemplateId}` is not enabled in Stage 1.0. {templateRecord.Summary}", "validation_failure");

        var template = templateRecord.DotnetTemplateName;
        var projectName = GetRequiredArgument(request, "project_name");
        var outputPath = NormalizeRelativePath(GetRequiredArgument(request, "output_path"));
        var targetFramework = _dotnetScaffoldSurfaceService.ResolveTargetFramework(template, GetOptionalArgument(request, "target_framework"));
        var templateSwitches = _dotnetScaffoldSurfaceService.ResolveDefaultSwitches(template, GetOptionalArgument(request, "template_switches"));
        var workingDirectory = GetOptionalArgument(request, "working_directory");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 90, 1, 300);
        var targetPath = NormalizeRelativePath(Path.Combine(outputPath, $"{projectName}.csproj"));
        var fullTargetPath = ResolveWorkspacePath(targetPath);

        if (File.Exists(fullTargetPath))
        {
            var summary = $"Project already exists: {targetPath}.";
            var structuredDataJson = BuildProjectAttachSimpleDataJson(request, "create_dotnet_project", "success", targetPath, summary);
            RecordExecutionSuccess(workspaceRoot, "create_dotnet_project", targetPath, summary, structuredDataJson);
            return Success("create_dotnet_project", summary, summary, "success", structuredDataJson);
        }

        var gateDecision = BuildExecutionGateDecision(request, "create_dotnet_project", "dotnet", true, "safe_narrow", targetPath);
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("create_dotnet_project", gateDecision, targetPath);

        var result = _createDotnetProjectTool.Run(workspaceRoot, template, projectName, outputPath, targetFramework, templateSwitches, workingDirectory, timeoutSeconds, gateDecision);
        var (validationPassed, validationSummary) = ValidateCreatedFile(targetPath, fullTargetPath, "Project");
        var details = string.Join(
            Environment.NewLine,
            $"Created .NET project target: {targetPath}",
            $"Scaffold contract: matrix={DotnetScaffoldSurfaceService.MatrixVersion} template={templateRecord.TemplateId} status={templateRecord.SupportStatus} role={GetOptionalArgument(request, "role")} framework={FirstNonEmpty(targetFramework, "(default)")} switches={FirstNonEmpty(templateSwitches, "(none)")}",
            _commandExecutionService.FormatCompactResult($"dotnet new {template}", result));
        return BuildBuilderCommandResult(
            "create_dotnet_project",
            workspaceRoot,
            targetPath,
            validationPassed,
            validationSummary,
            details,
            result,
            BuildProjectAttachBuilderDataJson(request, "create_dotnet_project", targetPath, validationPassed, validationSummary, result));
    }

    private ToolResult ExecuteAddProjectToSolution(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var solutionPath = NormalizeRelativePath(GetRequiredArgument(request, "solution_path"));
        var projectPath = NormalizeRelativePath(GetRequiredArgument(request, "project_path"));
        var workingDirectory = GetOptionalArgument(request, "working_directory");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 60, 1, 300);
        var fullSolutionPath = ResolveWorkspacePath(solutionPath);
        var fullProjectPath = ResolveWorkspacePath(projectPath);
        var expectedReference = BuildSolutionProjectReference(fullSolutionPath, fullProjectPath, workspaceRoot);

        if (SolutionContainsReference(fullSolutionPath, expectedReference))
        {
            var summary = $"Solution already references {projectPath}.";
            var structuredDataJson = BuildProjectAttachSimpleDataJson(request, "add_project_to_solution", "success", solutionPath, summary);
            RecordExecutionSuccess(workspaceRoot, "add_project_to_solution", solutionPath, summary, structuredDataJson);
            return Success("add_project_to_solution", summary, summary, "success", structuredDataJson);
        }

        var gateDecision = BuildExecutionGateDecision(request, "add_project_to_solution", "dotnet", true, "safe_narrow", solutionPath);
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("add_project_to_solution", gateDecision, solutionPath);

        var result = _addProjectToSolutionTool.Run(workspaceRoot, solutionPath, projectPath, workingDirectory, timeoutSeconds, gateDecision);
        var (validationPassed, validationSummary) = ValidateSolutionReference(fullSolutionPath, expectedReference, projectPath);
        var details = string.Join(
            Environment.NewLine,
            $"Added project `{projectPath}` to solution `{solutionPath}`.",
            _commandExecutionService.FormatCompactResult("dotnet sln add", result));
        return BuildBuilderCommandResult(
            "add_project_to_solution",
            workspaceRoot,
            solutionPath,
            validationPassed,
            validationSummary,
            details,
            result,
            BuildProjectAttachBuilderDataJson(request, "add_project_to_solution", solutionPath, validationPassed, validationSummary, result));
    }

    private ToolResult ExecuteAddDotnetProjectReference(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var projectPath = NormalizeRelativePath(GetRequiredArgument(request, "project_path"));
        var referencePath = NormalizeRelativePath(GetRequiredArgument(request, "reference_path"));
        var decisionSummaryOverride = GetOptionalArgument(request, "reference_decision_summary");
        var workingDirectory = GetOptionalArgument(request, "working_directory");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 60, 1, 300);
        var referenceDecision = _dotnetProjectReferencePolicyService.Evaluate(workspaceRoot, projectPath, referencePath);
        var decisionSummary = string.IsNullOrWhiteSpace(decisionSummaryOverride)
            ? referenceDecision.DecisionSummary
            : decisionSummaryOverride;
        if (referenceDecision.DecisionKind == DotnetProjectReferenceDecisionKind.Blocked)
        {
            var summary = decisionSummary;
            var structuredDataJson = BuildProjectReferenceDecisionJson(referenceDecision, "add_dotnet_project_reference", referenceDecision.DecisionCode, summary);
            RecordExecutionFailure(workspaceRoot, "add_dotnet_project_reference", referenceDecision.DecisionCode, projectPath, summary, structuredDataJson);
            return Failure("add_dotnet_project_reference", summary, referenceDecision.DecisionCode, summary, structuredDataJson);
        }

        projectPath = NormalizeRelativePath(referenceDecision.EffectiveProjectPath);
        referencePath = NormalizeRelativePath(referenceDecision.EffectiveReferencePath);
        request.Arguments["project_path"] = projectPath;
        request.Arguments["reference_path"] = referencePath;

        var fullProjectPath = ResolveWorkspacePath(projectPath);
        var fullReferencePath = ResolveWorkspacePath(referencePath);
        var expectedReference = BuildProjectReferenceInclude(fullProjectPath, fullReferencePath, workspaceRoot);

        if (ProjectContainsReference(fullProjectPath, expectedReference))
        {
            var summary = $"{decisionSummary} result=already_referenced";
            var structuredDataJson = BuildProjectReferenceDecisionJson(referenceDecision, "add_dotnet_project_reference", "success", summary);
            RecordExecutionSuccess(workspaceRoot, "add_dotnet_project_reference", projectPath, summary, structuredDataJson);
            return Success("add_dotnet_project_reference", summary, summary, "success", structuredDataJson);
        }

        var gateDecision = BuildExecutionGateDecision(request, "add_dotnet_project_reference", "dotnet", true, "safe_narrow", projectPath);
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("add_dotnet_project_reference", gateDecision, projectPath);

        var result = _addDotnetProjectReferenceTool.Run(workspaceRoot, projectPath, referencePath, workingDirectory, timeoutSeconds, gateDecision);
        var (validationPassed, validationSummary) = ValidateProjectReference(fullProjectPath, fullReferencePath, expectedReference, referencePath);
        validationSummary = $"{decisionSummary} result={validationSummary}";
        var details = string.Join(
            Environment.NewLine,
            $"Added project reference `{referencePath}` to `{projectPath}`.",
            _commandExecutionService.FormatCompactResult("dotnet add reference", result));
        return BuildBuilderCommandResult(
            "add_dotnet_project_reference",
            workspaceRoot,
            projectPath,
            validationPassed,
            validationSummary,
            details,
            result,
            BuildProjectReferenceDecisionJson(referenceDecision, "add_dotnet_project_reference", validationPassed ? "success" : "validation_failed", validationSummary));
    }

    private ToolResult ExecuteRunCommand(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var command = GetRequiredArgument(request, "command");
        var arguments = GetOptionalArgument(request, "arguments");
        var workingDirectory = GetOptionalArgument(request, "working_directory");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 30, 1, 600);
        var normalizedCommand = NormalizeCommandFamily(command);
        var gateDecision = BuildExecutionGateDecision(
            request,
            normalizedCommand,
            normalizedCommand,
            true,
            "",
            "");
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("run_command", gateDecision, "");

        var result = _runCommandTool.Run(workspaceRoot, command, arguments, workingDirectory, timeoutSeconds, gateDecision);
        return FromCommandResult("run_command", _commandExecutionService.FormatDetailedResult(result), result);
    }

    private ToolResult ExecuteGitStatus(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var workingDirectory = GetOptionalArgument(request, "working_directory");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 30, 1, 300);
        var gateDecision = BuildExecutionGateDecision(request, "git", "git", true, "", "");
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("git_status", gateDecision, "");

        var result = _gitStatusTool.Run(workspaceRoot, workingDirectory, timeoutSeconds, gateDecision);
        return FromCommandResult("git_status", _commandExecutionService.FormatCompactResult("git status", result), result);
    }

    private ToolResult ExecuteGitDiff(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var path = GetOptionalArgument(request, "path");
        var workingDirectory = GetOptionalArgument(request, "working_directory");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 30, 1, 300);
        var normalizedPath = NormalizeWorkspaceRelativePath(workspaceRoot, path);
        var gateDecision = BuildExecutionGateDecision(request, "git", "git", true, "", normalizedPath);
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("git_diff", gateDecision, normalizedPath);

        var repoProbe = _commandExecutionService.Execute(
            workspaceRoot,
            "git",
            "rev-parse --show-toplevel",
            workingDirectory,
            timeoutSeconds,
            gateDecision);

        if (repoProbe.ExitCode != 0 || repoProbe.TimedOut)
        {
            return Failure(
                "git_diff",
                "git_diff failed: current workspace is not a git repository (.git folder not found).");
        }

        var repoRoot = TrimCommandOutput(repoProbe.StandardOutput);
        var statusResult = _commandExecutionService.Execute(
            workspaceRoot,
            "git",
            "status --short --branch --untracked-files=all --",
            workingDirectory,
            timeoutSeconds,
            gateDecision);

        if (statusResult.ExitCode != 0 || statusResult.TimedOut)
        {
            return Failure(
                "git_diff",
                "git_diff failed: could not read git status." + Environment.NewLine + _commandExecutionService.FormatCompactResult("git status", statusResult));
        }

        var diffResult = _gitDiffTool.Run(workspaceRoot, workingDirectory, normalizedPath, timeoutSeconds, gateDecision);
        var details = BuildGitDiffOutput(repoRoot, normalizedPath, statusResult, diffResult);
        return FromCommandResult("git_diff", details, diffResult);
    }

    private ToolResult ExecuteDotnetBuild(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var project = GetOptionalArgument(request, "project");
        var activeTarget = GetOptionalArgument(request, "active_target");
        var configuration = GetOptionalArgument(request, "configuration");
        var workingDirectory = GetOptionalArgument(request, "working_directory");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 120, 1, 900);
        var resolution = _workspaceBuildIndexService.ResolveForBuild(workspaceRoot, project, activeTarget);
        if (!resolution.Success || resolution.Item is null)
        {
            var failure = Failure("dotnet_build", resolution.Message, "resolution_failure");
            RecordExecutionFailure(
                workspaceRoot,
                "dotnet_build",
                "resolution_failure",
                project,
                failure.Summary,
                BuildSimpleDataJson("dotnet_build", "resolution_failure", project, resolution.Message));
            return failure;
        }

        request.Arguments["project"] = resolution.Item.RelativePath;
        var gateDecision = BuildExecutionGateDecision(
            request,
            "dotnet_build",
            "dotnet",
            true,
            "safe_narrow",
            resolution.Item.RelativePath);
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("dotnet_build", gateDecision, resolution.Item.RelativePath);

        var result = _dotnetBuildTool.Run(workspaceRoot, workingDirectory, resolution.Item.RelativePath, configuration, timeoutSeconds, gateDecision);
        var parsed = _dotnetBuildParser.Parse(workspaceRoot, result.StandardOutput, result.StandardError);
        var repairContext = _repairContextService.BuildForBuildFailure(workspaceRoot, resolution.Item.RelativePath, parsed, "dotnet_build");
        PersistBuildProfileSelection(
            workspaceRoot,
            new WorkspaceBuildProfileRecord
            {
                WorkspaceRoot = workspaceRoot,
                BuildSystemType = BuildSystemType.Dotnet,
                PrimaryTargetPath = resolution.Item.RelativePath,
                BuildToolFamily = "dotnet_build",
                TestToolFamily = "dotnet_test",
                BuildTargetPath = resolution.Item.RelativePath,
                TestTargetPath = resolution.Item.RelativePath,
                Confidence = "high"
            },
            "dotnet_build",
            "",
            "");
        var outcomeType = DetermineCommandOutcome("dotnet_build", result, parsed.ErrorCount > 0);
        var summary = outcomeType == "build_failure"
            ? parsed.Summary
            : outcomeType == "success"
                ? parsed.Summary
                : BuildExecutionFailureSummary("dotnet_build", result);
        var structuredDataJson = BuildFailureDataJson(
            "dotnet_build",
            outcomeType,
            resolution.Item.RelativePath,
            summary,
            parsed,
            result);

        if (outcomeType == "success")
        {
            var output = BuildDotnetBuildSuccessOutput(resolution.Message, resolution.Item.RelativePath, parsed, result);
            RecordExecutionSuccess(workspaceRoot, "dotnet_build", resolution.Item.RelativePath, summary, structuredDataJson);
            RecordFileTouch(workspaceRoot, request, resolution.Item.RelativePath, "verify", artifactType: "build_result", isProductiveTouch: true, contentChanged: false);
            return Success("dotnet_build", output, summary, "success", structuredDataJson);
        }

        var outputFailure = BuildDotnetBuildFailureOutput(resolution.Message, resolution.Item.RelativePath, parsed, result, summary);
        RecordExecutionFailure(workspaceRoot, "dotnet_build", outcomeType, resolution.Item.RelativePath, summary, structuredDataJson);
        RecordFileTouch(workspaceRoot, request, resolution.Item.RelativePath, "verify", artifactType: "build_result", isProductiveTouch: true, contentChanged: false);

        if (outcomeType == "build_failure")
        {
            SaveFailureArtifact(
                workspaceRoot,
                "build_failure_summary",
                resolution.Item.RelativePath,
                "dotnet_build",
                summary,
                structuredDataJson);
            var repairArtifact = _repairContextService.SaveRepairContextArtifact(workspaceRoot, _ramDbService, repairContext);
            outputFailure += Environment.NewLine + $"Artifact synced: {repairArtifact.RelativePath}";
        }

        return Failure("dotnet_build", outputFailure, outcomeType, summary, structuredDataJson);
    }

    private ToolResult ExecuteDotnetTest(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var project = GetOptionalArgument(request, "project");
        var activeTarget = GetOptionalArgument(request, "active_target");
        var filter = GetOptionalArgument(request, "filter");
        var workingDirectory = GetOptionalArgument(request, "working_directory");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 120, 1, 900);
        var resolution = _workspaceBuildIndexService.ResolveForTesting(workspaceRoot, project, activeTarget);
        if (!resolution.Success || resolution.Item is null)
        {
            var failure = Failure("dotnet_test", resolution.Message, "resolution_failure");
            RecordExecutionFailure(
                workspaceRoot,
                "dotnet_test",
                "resolution_failure",
                project,
                failure.Summary,
                BuildWorkspaceResolutionDataJson("dotnet_test", "resolution_failure", project, resolution));
            return failure;
        }

        request.Arguments["project"] = resolution.Item.RelativePath;
        var gateDecision = BuildExecutionGateDecision(
            request,
            "dotnet_test",
            "dotnet",
            true,
            "safe_narrow",
            resolution.Item.RelativePath);
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("dotnet_test", gateDecision, resolution.Item.RelativePath);

        var result = _dotnetTestTool.Run(workspaceRoot, workingDirectory, resolution.Item.RelativePath, filter, timeoutSeconds, gateDecision);
        var parsed = _dotnetTestResultParser.Parse(result.StandardOutput, result.StandardError);
        var repairContext = _repairContextService.BuildForTestFailure(workspaceRoot, resolution.Item.RelativePath, parsed);
        PersistBuildProfileSelection(
            workspaceRoot,
            new WorkspaceBuildProfileRecord
            {
                WorkspaceRoot = workspaceRoot,
                BuildSystemType = BuildSystemType.Dotnet,
                PrimaryTargetPath = resolution.Item.RelativePath,
                BuildToolFamily = "dotnet_build",
                TestToolFamily = "dotnet_test",
                BuildTargetPath = resolution.Item.RelativePath,
                TestTargetPath = resolution.Item.RelativePath,
                Confidence = "high"
            },
            "dotnet_test",
            "",
            "");
        var outcomeType = DetermineCommandOutcome("dotnet_test", result, parsed.FailedCount > 0);
        var summary = outcomeType == "test_failure"
            ? parsed.Summary
            : outcomeType == "success"
                ? parsed.Summary
                : BuildExecutionFailureSummary("dotnet_test", result);
        var structuredDataJson = BuildFailureDataJson(
            "dotnet_test",
            outcomeType,
            resolution.Item.RelativePath,
            summary,
            parsed,
            result);

        if (outcomeType == "success")
        {
            var output = BuildDotnetTestSuccessOutput(resolution.Message, resolution.Item.RelativePath, parsed, result);
            RecordExecutionSuccess(workspaceRoot, "dotnet_test", resolution.Item.RelativePath, summary, structuredDataJson);
            RecordFileTouch(workspaceRoot, request, resolution.Item.RelativePath, "verify", artifactType: "build_result", isProductiveTouch: true, contentChanged: false);
            return Success("dotnet_test", output, summary, "success", structuredDataJson);
        }

        var outputFailure = BuildDotnetTestFailureOutput(resolution.Message, resolution.Item.RelativePath, parsed, result, summary);
        RecordExecutionFailure(workspaceRoot, "dotnet_test", outcomeType, resolution.Item.RelativePath, summary, structuredDataJson);
        RecordFileTouch(workspaceRoot, request, resolution.Item.RelativePath, "verify", artifactType: "build_result", isProductiveTouch: true, contentChanged: false);

        if (outcomeType == "test_failure")
        {
            SaveFailureArtifact(
                workspaceRoot,
                "test_failure_summary",
                resolution.Item.RelativePath,
                "dotnet_test",
                summary,
                structuredDataJson);
            var repairArtifact = _repairContextService.SaveRepairContextArtifact(workspaceRoot, _ramDbService, repairContext);
            outputFailure += Environment.NewLine + $"Artifact synced: {repairArtifact.RelativePath}";
        }

        return Failure("dotnet_test", outputFailure, outcomeType, summary, structuredDataJson);
    }

    private ToolResult ExecuteCMakeConfigure(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var sourceDirectory = GetOptionalArgument(request, "source_dir");
        var buildDirectory = GetOptionalArgument(request, "build_dir");
        var generator = GetOptionalArgument(request, "generator");
        var configuration = GetOptionalArgument(request, "configuration");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 180, 1, 900);
        var profile = ResolveBuildProfileOrFailure(workspaceRoot, BuildSystemType.CMake, sourceDirectory, out var profileMessage);
        if (profile is null)
            return Failure("cmake_configure", profileMessage, "resolution_failure", profileMessage);

        var resolvedSourceDirectory = NormalizeWorkspaceRelativeDirectory(workspaceRoot, FirstNonEmpty(sourceDirectory, profile.ConfigureTargetPath, "."));
        var resolvedBuildDirectory = NormalizeWorkspaceRelativeDirectory(workspaceRoot, FirstNonEmpty(buildDirectory, profile.BuildDirectoryPath, "build"));
        request.Arguments["source_dir"] = resolvedSourceDirectory;
        request.Arguments["build_dir"] = resolvedBuildDirectory;
        var gateDecision = BuildExecutionGateDecision(
            request,
            "cmake_configure",
            NormalizeBuildFamily(BuildSystemType.CMake),
            true,
            "safe_narrow",
            resolvedBuildDirectory);
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("cmake_configure", gateDecision, resolvedBuildDirectory);

        var result = _cmakeConfigureTool.Run(
            workspaceRoot,
            resolvedSourceDirectory,
            resolvedBuildDirectory,
            generator,
            configuration,
            timeoutSeconds,
            gateDecision);
        var parsed = _buildOutputParsingService.ParseBuildOutput("cmake_configure", workspaceRoot, result.StandardOutput, result.StandardError);
        return FinalizeGenericBuildExecution(
            workspaceRoot,
            request,
            "cmake_configure",
            profile,
            resolvedBuildDirectory,
            $"Resolved CMake configure source={resolvedSourceDirectory} build={resolvedBuildDirectory}.",
            result,
            parsed,
            "configure_result");
    }

    private ToolResult ExecuteCMakeBuild(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var buildDirectory = GetOptionalArgument(request, "build_dir");
        var target = GetOptionalArgument(request, "target");
        var configuration = GetOptionalArgument(request, "configuration");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 180, 1, 900);
        var profile = ResolveBuildProfileOrFailure(workspaceRoot, BuildSystemType.CMake, buildDirectory, out var profileMessage);
        if (profile is null)
            return Failure("cmake_build", profileMessage, "resolution_failure", profileMessage);

        var resolvedBuildDirectory = NormalizeWorkspaceRelativeDirectory(workspaceRoot, FirstNonEmpty(buildDirectory, profile.BuildDirectoryPath, "build"));
        request.Arguments["build_dir"] = resolvedBuildDirectory;
        var blockedResult = TryBlockBroadNativeBuildExecution(
            workspaceRoot,
            "cmake_build",
            profile,
            resolvedBuildDirectory,
            buildDirectory,
            target,
            $"Resolved CMake build directory: {resolvedBuildDirectory}.");
        if (blockedResult is not null)
            return blockedResult;

        var gateDecision = BuildExecutionGateDecision(
            request,
            "cmake_build",
            NormalizeBuildFamily(profile.BuildSystemType),
            true,
            "safe_narrow",
            resolvedBuildDirectory);
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("cmake_build", gateDecision, resolvedBuildDirectory);

        var result = _cmakeBuildTool.Run(
            workspaceRoot,
            resolvedBuildDirectory,
            target,
            configuration,
            timeoutSeconds,
            gateDecision);
        var parsed = _buildOutputParsingService.ParseBuildOutput("cmake_build", workspaceRoot, result.StandardOutput, result.StandardError);
        return FinalizeGenericBuildExecution(
            workspaceRoot,
            request,
            "cmake_build",
            profile,
            resolvedBuildDirectory,
            $"Resolved CMake build directory: {resolvedBuildDirectory}.",
            result,
            parsed,
            "build_result");
    }

    private ToolResult ExecuteMakeBuild(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var directory = GetOptionalArgument(request, "directory");
        var target = GetOptionalArgument(request, "target");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 180, 1, 900);
        var profile = ResolveBuildProfileOrFailure(workspaceRoot, BuildSystemType.Make, directory, out var profileMessage);
        if (profile is null)
            return Failure("make_build", profileMessage, "resolution_failure", profileMessage);

        var resolvedDirectory = NormalizeWorkspaceRelativeDirectory(workspaceRoot, FirstNonEmpty(directory, profile.BuildTargetPath, "."));
        request.Arguments["directory"] = resolvedDirectory;
        var blockedResult = TryBlockBroadNativeBuildExecution(
            workspaceRoot,
            "make_build",
            profile,
            resolvedDirectory,
            directory,
            target,
            $"Resolved make directory: {resolvedDirectory}.");
        if (blockedResult is not null)
            return blockedResult;

        var gateDecision = BuildExecutionGateDecision(
            request,
            "make_build",
            NormalizeBuildFamily(profile.BuildSystemType),
            true,
            "safe_narrow",
            resolvedDirectory);
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("make_build", gateDecision, resolvedDirectory);

        var result = _makeBuildTool.Run(workspaceRoot, resolvedDirectory, target, timeoutSeconds, gateDecision);
        var parsed = _buildOutputParsingService.ParseBuildOutput("make_build", workspaceRoot, result.StandardOutput, result.StandardError);
        return FinalizeGenericBuildExecution(
            workspaceRoot,
            request,
            "make_build",
            profile,
            resolvedDirectory,
            $"Resolved make directory: {resolvedDirectory}.",
            result,
            parsed,
            "build_result");
    }

    private ToolResult ExecuteNinjaBuild(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var directory = GetOptionalArgument(request, "directory");
        var target = GetOptionalArgument(request, "target");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 180, 1, 900);
        var profile = ResolveBuildProfileOrFailure(workspaceRoot, BuildSystemType.Ninja, directory, out var profileMessage);
        if (profile is null)
            return Failure("ninja_build", profileMessage, "resolution_failure", profileMessage);

        var resolvedDirectory = NormalizeWorkspaceRelativeDirectory(workspaceRoot, FirstNonEmpty(directory, profile.BuildTargetPath, "."));
        request.Arguments["directory"] = resolvedDirectory;
        var blockedResult = TryBlockBroadNativeBuildExecution(
            workspaceRoot,
            "ninja_build",
            profile,
            resolvedDirectory,
            directory,
            target,
            $"Resolved ninja directory: {resolvedDirectory}.");
        if (blockedResult is not null)
            return blockedResult;

        var gateDecision = BuildExecutionGateDecision(
            request,
            "ninja_build",
            NormalizeBuildFamily(profile.BuildSystemType),
            true,
            "safe_narrow",
            resolvedDirectory);
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("ninja_build", gateDecision, resolvedDirectory);

        var result = _ninjaBuildTool.Run(workspaceRoot, resolvedDirectory, target, timeoutSeconds, gateDecision);
        var parsed = _buildOutputParsingService.ParseBuildOutput("ninja_build", workspaceRoot, result.StandardOutput, result.StandardError);
        return FinalizeGenericBuildExecution(
            workspaceRoot,
            request,
            "ninja_build",
            profile,
            resolvedDirectory,
            $"Resolved ninja directory: {resolvedDirectory}.",
            result,
            parsed,
            "build_result");
    }

    private ToolResult ExecuteRunBuildScript(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var scriptPath = GetOptionalArgument(request, "path");
        var scriptArguments = GetOptionalArgument(request, "script_arguments");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 180, 1, 900);
        var profile = ResolveBuildProfileOrFailure(workspaceRoot, BuildSystemType.Script, scriptPath, out var profileMessage);
        if (profile is null)
            return Failure("run_build_script", profileMessage, "resolution_failure", profileMessage);

        var resolvedScriptPath = NormalizeWorkspaceRelativePath(workspaceRoot, FirstNonEmpty(scriptPath, profile.BuildTargetPath));
        if (string.IsNullOrWhiteSpace(resolvedScriptPath))
        {
            var message = "run_build_script failed: no repo-local build script was resolved for this workspace.";
            return Failure("run_build_script", message, "resolution_failure", message);
        }

        if (!string.IsNullOrWhiteSpace(scriptArguments))
        {
            var message =
                "run_build_script failed: additional script arguments are blocked by execution safety policy."
                + Environment.NewLine
                + "Run the detected repo-local script without extra arguments, or add a narrower dedicated tool first.";
            return Failure("run_build_script", message, "safety_blocked", message);
        }

        request.Arguments["path"] = resolvedScriptPath;
        var blockedResult = TryBlockBroadNativeBuildExecution(
            workspaceRoot,
            "run_build_script",
            profile,
            resolvedScriptPath,
            scriptPath,
            "",
            $"Resolved build script: {resolvedScriptPath}.");
        if (blockedResult is not null)
            return blockedResult;

        var gateDecision = BuildExecutionGateDecision(
            request,
            "run_build_script",
            NormalizeBuildFamily(profile.BuildSystemType),
            true,
            "safe_narrow",
            resolvedScriptPath);
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("run_build_script", gateDecision, resolvedScriptPath);

        var result = _runBuildScriptTool.Run(workspaceRoot, resolvedScriptPath, scriptArguments, timeoutSeconds, gateDecision);
        var parsed = _buildOutputParsingService.ParseBuildOutput("run_build_script", workspaceRoot, result.StandardOutput, result.StandardError);
        return FinalizeGenericBuildExecution(
            workspaceRoot,
            request,
            "run_build_script",
            profile,
            resolvedScriptPath,
            $"Resolved build script: {resolvedScriptPath}.",
            result,
            parsed,
            "build_result");
    }

    private ToolResult ExecuteCTestRun(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var directory = GetRequiredArgument(request, "directory");
        var configuration = GetOptionalArgument(request, "configuration");
        var timeoutSeconds = GetIntArgument(request, "timeout_seconds", 60, 1, 300);
        var gateDecision = BuildExecutionGateDecision(request, "ctest_run", "ctest", true, "safe_narrow", directory);
        if (!gateDecision.IsAllowed)
            return BuildExecutionGateDeniedToolResult("ctest_run", gateDecision, directory);

        var result = _ctestRunTool.Run(workspaceRoot, directory, configuration, timeoutSeconds, gateDecision);
        var parsed = _buildOutputParsingService.ParseBuildOutput("ctest_run", workspaceRoot, result.StandardOutput, result.StandardError);
        return FinalizeGenericBuildExecution(
            workspaceRoot,
            request,
            "ctest_run",
            new WorkspaceBuildProfileRecord
            {
                BuildSystemType = BuildSystemType.CMake,
                BuildToolFamily = "ctest_run",
                BuildTargetPath = directory
            },
            directory,
            $"Resolved ctest directory: {directory}.",
            result,
            parsed,
            "test_result");
    }

    private ToolResult ExecuteOpenFailureContext(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var eligibility = _repairEligibilityService.EvaluateFailureNavigation(workspaceRoot, _ramDbService);
        if (!eligibility.IsEligible)
        {
            var message = BuildRepairIneligibleMessage("open_failure_context", eligibility, workspaceRoot);
            return Failure("open_failure_context", message, "resolution_failure", message);
        }

        var scope = GetOptionalArgument(request, "scope");
        var resolution = _repairContextService.ResolveContext(
            workspaceRoot,
            _ramDbService,
            scope);

        if (!resolution.Success)
            return Failure("open_failure_context", resolution.Message, "resolution_failure");

        if (resolution.HasOpenablePath && resolution.Item is not null && !string.IsNullOrWhiteSpace(resolution.Item.RelativePath))
        {
            request.Arguments["path"] = resolution.Item.RelativePath;
        }

        var output = _openFailureContextTool.Open(workspaceRoot, resolution, _readFileChunkTool);
        var structuredDataJson = SerializeJson(new
        {
            source = resolution.Source,
            has_openable_path = resolution.HasOpenablePath,
            item = resolution.Item,
            repair_context = resolution.RepairContext
        });

        return Success("open_failure_context", output, resolution.Message, "success", structuredDataJson);
    }

    private ToolResult ExecutePlanRepair(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var eligibility = _repairEligibilityService.EvaluatePlanRepair(workspaceRoot, _ramDbService);
        if (!eligibility.IsEligible)
        {
            var message = BuildRepairIneligibleMessage("plan_repair", eligibility, workspaceRoot);
            return Failure("plan_repair", message, "resolution_failure", message);
        }

        var proposalContext = BuildRepairProposalContext(workspaceRoot, request);
        if (!proposalContext.Success || proposalContext.Input is null || proposalContext.Proposal is null || proposalContext.Artifact is null)
        {
            return Failure(
                "plan_repair",
                proposalContext.Message,
                "resolution_failure",
                proposalContext.Message);
        }

        var patchContract = _cSharpPatchFoundationService.TryBuildRepairContract(workspaceRoot, proposalContext.Proposal);
        ArtifactRecord? patchContractArtifact = null;
        if (patchContract is not null)
            patchContractArtifact = SaveCSharpPatchContractArtifact(workspaceRoot, patchContract, request);

        var output = _planRepairTool.Format(
            proposalContext.Input,
            proposalContext.Proposal,
            proposalContext.Artifact,
            patchContract,
            patchContractArtifact);
        var structuredDataJson = SerializeJson(new
        {
            input = proposalContext.Input,
            proposal = proposalContext.Proposal,
            artifact = new
            {
                proposalContext.Artifact.Id,
                proposalContext.Artifact.ArtifactType,
                proposalContext.Artifact.RelativePath,
                proposalContext.Artifact.UpdatedUtc
            },
            csharp_patch_contract = patchContract,
            csharp_patch_contract_artifact = patchContractArtifact is null
                ? null
                : new
                {
                    patchContractArtifact.Id,
                    patchContractArtifact.ArtifactType,
                    patchContractArtifact.RelativePath,
                    patchContractArtifact.UpdatedUtc
                }
        });

        return Success(
            "plan_repair",
            output,
            proposalContext.Proposal.Title,
            proposalContext.Proposal.RequiresModel ? "model_brief_ready" : "local_repair_plan",
            structuredDataJson);
    }

    private ToolResult ExecutePreviewPatchDraft(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var eligibility = _repairEligibilityService.EvaluatePreviewPatchDraft(workspaceRoot, _ramDbService);
        if (!eligibility.IsEligible)
        {
            var message = BuildRepairIneligibleMessage("preview_patch_draft", eligibility, workspaceRoot);
            return Failure("preview_patch_draft", message, "resolution_failure", message);
        }

        var preferredPath = ResolvePreferredPatchPath(workspaceRoot, request);
        var recentArtifacts = eligibility.RecentArtifacts;

        var selectedProposal = TrySelectRepairProposalArtifact(recentArtifacts, preferredPath, out var proposalSelectionMessage);
        if (selectedProposal is null)
        {
            if (ShouldBuildFreshRepairProposal(preferredPath, proposalSelectionMessage))
            {
                var proposalContext = BuildRepairProposalContext(workspaceRoot, request);
                if (proposalContext.Success && proposalContext.Input is not null && proposalContext.Proposal is not null && proposalContext.Artifact is not null)
                {
                    selectedProposal = new ArtifactPayload<RepairProposalRecord>(proposalContext.Artifact, proposalContext.Proposal, NormalizeRelativePath(proposalContext.Proposal.TargetFilePath));
                    proposalSelectionMessage = proposalContext.Message;
                }
            }
        }

        if (selectedProposal is null)
        {
            if (!ShouldFallbackToExistingPatchDraft(preferredPath, proposalSelectionMessage))
                return Failure("preview_patch_draft", proposalSelectionMessage, "resolution_failure", proposalSelectionMessage);

            var selectedDraft = TrySelectPatchDraftArtifact(recentArtifacts, preferredPath, out var draftSelectionMessage);
            if (selectedDraft is null)
            {
                var message = BuildPatchDraftResolutionMessage(proposalSelectionMessage, draftSelectionMessage);
                return Failure("preview_patch_draft", message, "resolution_failure", message);
            }

            if (!string.IsNullOrWhiteSpace(selectedDraft.Payload.TargetFilePath))
                request.Arguments["path"] = selectedDraft.Payload.TargetFilePath;

            var existingContractArtifact = LoadArtifactByRelativePath(workspaceRoot, selectedDraft.Payload.PatchContractArtifactRelativePath);
            var existingContract = TryDeserializeArtifact<CSharpPatchWorkContractRecord>(existingContractArtifact);
            var existingPlanArtifact = LoadArtifactByRelativePath(workspaceRoot, selectedDraft.Payload.PatchPlanArtifactRelativePath);
            var existingPlan = TryDeserializeArtifact<CSharpPatchPlanRecord>(existingPlanArtifact);
            var output = _previewPatchDraftTool.Format(
                selectedDraft.Payload,
                selectedDraft.Artifact,
                existingContract,
                existingContractArtifact,
                existingPlan,
                existingPlanArtifact);
            var structuredDataJson = SerializeJson(new
            {
                draft = selectedDraft.Payload,
                csharp_patch_contract = existingContract,
                csharp_patch_plan = existingPlan,
                artifact = new
                {
                    selectedDraft.Artifact.Id,
                    selectedDraft.Artifact.ArtifactType,
                    selectedDraft.Artifact.RelativePath,
                    selectedDraft.Artifact.UpdatedUtc
                }
            });

            return Success(
                "preview_patch_draft",
                output,
                selectedDraft.Payload.ProposalSummary,
                selectedDraft.Payload.CanApplyLocally ? "patch_draft_ready" : "patch_draft_brief",
                structuredDataJson);
        }

        var patchContract = _cSharpPatchFoundationService.TryBuildRepairContract(workspaceRoot, selectedProposal.Payload);
        ArtifactRecord? patchContractArtifact = null;
        if (patchContract is not null)
            patchContractArtifact = SaveCSharpPatchContractArtifact(workspaceRoot, patchContract, request);

        var draft = _patchDraftBuilder.Build(workspaceRoot, selectedProposal.Payload);
        if (!string.IsNullOrWhiteSpace(draft.TargetFilePath))
            request.Arguments["path"] = draft.TargetFilePath;

        CSharpPatchPlanRecord? patchPlan = null;
        ArtifactRecord? patchPlanArtifact = null;
        if (patchContract is not null)
        {
            draft.PatchContractId = patchContract.ContractId;
            draft.PatchContractArtifactRelativePath = patchContractArtifact?.RelativePath ?? "";
            draft.ModificationIntent = patchContract.ModificationIntent;
            draft.TargetSurfaceType = patchContract.TargetSurfaceType;
            draft.MutationFamily = patchContract.MutationFamily;
            draft.AllowedEditScope = patchContract.AllowedEditScope;
            draft.SupportingFiles = [.. patchContract.SupportingFiles];
            draft.WarningPolicyMode = patchContract.WarningPolicyMode;
            draft.TargetProjectPath = FirstNonEmpty(draft.TargetProjectPath, patchContract.TargetProjectPath);
            draft.RetrievalBackend = FirstNonEmpty(draft.RetrievalBackend, patchContract.RetrievalBackend);
            draft.RetrievalEmbedderModel = FirstNonEmpty(draft.RetrievalEmbedderModel, patchContract.RetrievalEmbedderModel);
            draft.RetrievalQueryKind = FirstNonEmpty(draft.RetrievalQueryKind, patchContract.RetrievalQueryKind);
            draft.RetrievalHitCount = Math.Max(draft.RetrievalHitCount, patchContract.RetrievalHitCount);
            if (draft.RetrievalSourceKinds.Count == 0 && patchContract.RetrievalSourceKinds.Count > 0)
                draft.RetrievalSourceKinds = [.. patchContract.RetrievalSourceKinds];
            draft.RetrievalContextPacketArtifactRelativePath = FirstNonEmpty(draft.RetrievalContextPacketArtifactRelativePath, patchContract.RetrievalContextPacketArtifactRelativePath);
            patchPlan = _cSharpPatchFoundationService.BuildPlan(patchContract, selectedProposal.Payload, draft);
            draft.PatchPlanId = patchPlan.PlanId;
        }

        var draftArtifact = SavePatchDraftArtifact(workspaceRoot, draft);
        if (patchPlan is not null)
        {
            patchPlan.RelatedArtifactIds.Add(draftArtifact.Id);
            AddUniquePath(patchPlan.RelatedArtifactPaths, draftArtifact.RelativePath);
            patchPlanArtifact = SaveCSharpPatchPlanArtifact(workspaceRoot, patchPlan, request);
            draft.PatchPlanArtifactRelativePath = patchPlanArtifact.RelativePath;
            draftArtifact = SavePatchDraftArtifact(workspaceRoot, draft);
        }

        var previewOutput = _previewPatchDraftTool.Format(
            draft,
            draftArtifact,
            patchContract,
            patchContractArtifact,
            patchPlan,
            patchPlanArtifact);
        var previewStructuredDataJson = SerializeJson(new
        {
            proposal = selectedProposal.Payload,
            proposal_artifact = new
            {
                selectedProposal.Artifact.Id,
                selectedProposal.Artifact.ArtifactType,
                selectedProposal.Artifact.RelativePath,
                selectedProposal.Artifact.UpdatedUtc
            },
            draft,
            csharp_patch_contract = patchContract,
            csharp_patch_plan = patchPlan,
            draft_artifact = new
            {
                draftArtifact.Id,
                draftArtifact.ArtifactType,
                draftArtifact.RelativePath,
                draftArtifact.UpdatedUtc
            },
            csharp_patch_contract_artifact = patchContractArtifact is null
                ? null
                : new
                {
                    patchContractArtifact.Id,
                    patchContractArtifact.ArtifactType,
                    patchContractArtifact.RelativePath,
                    patchContractArtifact.UpdatedUtc
                },
            csharp_patch_plan_artifact = patchPlanArtifact is null
                ? null
                : new
                {
                    patchPlanArtifact.Id,
                    patchPlanArtifact.ArtifactType,
                    patchPlanArtifact.RelativePath,
                    patchPlanArtifact.UpdatedUtc
                }
        });

        return Success(
            "preview_patch_draft",
            previewOutput,
            draft.ProposalSummary,
            draft.CanApplyLocally ? "patch_draft_ready" : "patch_draft_brief",
            previewStructuredDataJson);
    }

    private ToolResult ExecuteApplyPatchDraft(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var eligibility = _repairEligibilityService.EvaluateApplyPatchDraft(workspaceRoot, _ramDbService);
        if (!eligibility.IsEligible)
        {
            var message = BuildRepairIneligibleMessage("apply_patch_draft", eligibility, workspaceRoot);
            return Failure("apply_patch_draft", message, "resolution_failure", message);
        }

        var preferredPath = ResolvePreferredPatchPath(workspaceRoot, request);
        var recentArtifacts = eligibility.RecentArtifacts;
        var selectedDraft = TrySelectPatchDraftArtifact(recentArtifacts, preferredPath, out var selectionMessage);
        if (selectedDraft is null)
            return Failure("apply_patch_draft", selectionMessage, "resolution_failure", selectionMessage);

        var draft = selectedDraft.Payload;
        if (!_repairEligibilityService.HasValidRepairChain(draft))
        {
            var invalidMessage =
                "apply_patch_draft failed: the selected patch draft is not tied to a recorded repair chain."
                + Environment.NewLine
                + "Run dotnet build or dotnet test first, or preview a patch draft from a real repair proposal.";
            return Failure("apply_patch_draft", invalidMessage, "resolution_failure", invalidMessage);
        }

        if (!draft.CanApplyLocally)
        {
            var message =
                "apply_patch_draft failed: the selected patch draft cannot be applied locally." + Environment.NewLine
                + "Preview the patch draft first and review the model brief instead.";
            RecordExecutionFailure(
                workspaceRoot,
                "apply_patch_draft",
                "validation_failure",
                draft.TargetFilePath,
                message,
                SerializeJson(new { draft }));
            return Failure("apply_patch_draft", message, "validation_failure", message, SerializeJson(new { draft }));
        }

        request.Arguments["path"] = draft.TargetFilePath;
        var stateBeforeApply = _ramDbService.LoadExecutionState(workspaceRoot);
        var proposal = TryLoadRepairProposalById(workspaceRoot, draft.SourceProposalArtifactId);
        var patchContractArtifact = LoadArtifactByRelativePath(
            workspaceRoot,
            draft.PatchContractArtifactRelativePath);
        var patchContract = TryDeserializeArtifact<CSharpPatchWorkContractRecord>(patchContractArtifact);
        if (patchContract is null && proposal is not null)
        {
            patchContract = _cSharpPatchFoundationService.TryBuildRepairContract(workspaceRoot, proposal);
            if (patchContract is not null)
                patchContractArtifact = SaveCSharpPatchContractArtifact(workspaceRoot, patchContract, request);
        }

        if (patchContract is not null)
        {
            var scopeDecision = _cSharpPatchFoundationService.ValidateDraft(workspaceRoot, patchContract, draft);
            if (!scopeDecision.ScopeApproved)
            {
                var scopeMessage =
                    "apply_patch_draft failed: the requested mutation falls outside the approved C# patch scope."
                    + Environment.NewLine
                    + scopeDecision.Summary;
                var scopeDataJson = SerializeJson(new
                {
                    draft,
                    csharp_patch_contract = patchContract,
                    csharp_scope_decision = scopeDecision
                });
                RecordExecutionFailure(workspaceRoot, "apply_patch_draft", "scope_blocked", draft.TargetFilePath, scopeMessage, scopeDataJson);
                return Failure("apply_patch_draft", scopeMessage, "scope_blocked", scopeMessage, scopeDataJson);
            }
        }

        var patchPlanArtifact = LoadArtifactByRelativePath(
            workspaceRoot,
            draft.PatchPlanArtifactRelativePath);
        var patchPlan = TryDeserializeArtifact<CSharpPatchPlanRecord>(patchPlanArtifact);
        if (patchPlan is null && patchContract is not null && proposal is not null)
        {
            patchPlan = _cSharpPatchFoundationService.BuildPlan(patchContract, proposal, draft);
            patchPlanArtifact = SaveCSharpPatchPlanArtifact(workspaceRoot, patchPlan, request);
            draft.PatchPlanId = patchPlan.PlanId;
            draft.PatchPlanArtifactRelativePath = patchPlanArtifact.RelativePath;
        }

        try
        {
            if (string.Equals(draft.DraftKind, "rebuild_symbol_recovery", StringComparison.OrdinalIgnoreCase))
            {
                var reconciliationApplyRecord = new PatchApplyResultRecord
                {
                    AppliedUtc = DateTime.UtcNow.ToString("O"),
                    Draft = draft,
                    ApplyOutput = "No local file mutation was required. The generated symbol recovery path will close through bounded rebuild verification.",
                    PatchContractId = FirstNonEmpty(patchContract?.ContractId ?? "", draft.PatchContractId),
                    PatchContractArtifactRelativePath = FirstNonEmpty(patchContractArtifact?.RelativePath ?? "", draft.PatchContractArtifactRelativePath),
                    PatchPlanId = FirstNonEmpty(patchPlan?.PlanId ?? "", draft.PatchPlanId),
                    PatchPlanArtifactRelativePath = FirstNonEmpty(patchPlanArtifact?.RelativePath ?? "", draft.PatchPlanArtifactRelativePath)
                };
                var reconciliationApplyArtifact = SavePatchApplyArtifact(workspaceRoot, reconciliationApplyRecord);
                var reconciliationOutput = reconciliationApplyRecord.ApplyOutput
                    + Environment.NewLine
                    + $"Apply artifact synced: {reconciliationApplyArtifact.RelativePath}";
                if (patchContractArtifact is not null)
                    reconciliationOutput += Environment.NewLine + $"Contract artifact synced: {patchContractArtifact.RelativePath}";
                if (patchPlanArtifact is not null)
                    reconciliationOutput += Environment.NewLine + $"Plan artifact synced: {patchPlanArtifact.RelativePath}";
                var reconciliationSummary = $"Accepted rebuild-first symbol reconciliation for {draft.TargetFilePath}.";
                var reconciliationDataJson = SerializeJson(new
                {
                    draft,
                    reconciliation_only = true,
                    mutation_observed = false,
                    csharp_patch_contract = patchContract,
                    csharp_patch_plan = patchPlan,
                    source_patch_draft_artifact = new
                    {
                        selectedDraft.Artifact.Id,
                        selectedDraft.Artifact.ArtifactType,
                        selectedDraft.Artifact.RelativePath,
                        selectedDraft.Artifact.UpdatedUtc
                    },
                    apply_artifact = new
                    {
                        reconciliationApplyArtifact.Id,
                        reconciliationApplyArtifact.ArtifactType,
                        reconciliationApplyArtifact.RelativePath,
                        reconciliationApplyArtifact.UpdatedUtc
                    },
                    csharp_patch_contract_artifact = patchContractArtifact is null
                        ? null
                        : new
                        {
                            patchContractArtifact.Id,
                            patchContractArtifact.ArtifactType,
                            patchContractArtifact.RelativePath,
                            patchContractArtifact.UpdatedUtc
                        },
                    csharp_patch_plan_artifact = patchPlanArtifact is null
                        ? null
                        : new
                        {
                            patchPlanArtifact.Id,
                            patchPlanArtifact.ArtifactType,
                            patchPlanArtifact.RelativePath,
                            patchPlanArtifact.UpdatedUtc
                        }
                });
                RecordExecutionSuccess(workspaceRoot, "apply_patch_draft", draft.TargetFilePath, reconciliationSummary, reconciliationDataJson);
                return Success("apply_patch_draft", reconciliationOutput, reconciliationSummary, "success", reconciliationDataJson);
            }

            var fullPath = ResolveWorkspacePath(draft.TargetFilePath);
            var applyOutput = _applyPatchDraftTool.Apply(workspaceRoot, fullPath, draft, _writeFileTool);
            var fileArtifact = SyncArtifactFromFile(workspaceRoot, fullPath, request);
            var applyRecord = new PatchApplyResultRecord
            {
                AppliedUtc = DateTime.UtcNow.ToString("O"),
                Draft = draft,
                ApplyOutput = applyOutput,
                PatchContractId = FirstNonEmpty(patchContract?.ContractId ?? "", draft.PatchContractId),
                PatchContractArtifactRelativePath = FirstNonEmpty(patchContractArtifact?.RelativePath ?? "", draft.PatchContractArtifactRelativePath),
                PatchPlanId = FirstNonEmpty(patchPlan?.PlanId ?? "", draft.PatchPlanId),
                PatchPlanArtifactRelativePath = FirstNonEmpty(patchPlanArtifact?.RelativePath ?? "", draft.PatchPlanArtifactRelativePath)
            };
            var applyArtifact = SavePatchApplyArtifact(workspaceRoot, applyRecord);
            var output = applyOutput
                + Environment.NewLine
                + $"Artifact synced: {fileArtifact.RelativePath}"
                + Environment.NewLine
                + $"Apply artifact synced: {applyArtifact.RelativePath}";
            if (patchContractArtifact is not null)
                output += Environment.NewLine + $"Contract artifact synced: {patchContractArtifact.RelativePath}";
            if (patchPlanArtifact is not null)
                output += Environment.NewLine + $"Plan artifact synced: {patchPlanArtifact.RelativePath}";
            output += BuildAutoValidationSuffixForPatchApply(workspaceRoot, applyArtifact, applyRecord, proposal, stateBeforeApply);
            var summary = $"Applied patch draft to {draft.TargetFilePath}.";
            var structuredDataJson = SerializeJson(new
            {
                draft,
                csharp_patch_contract = patchContract,
                csharp_patch_plan = patchPlan,
                auto_validation_triggered = true,
                source_patch_draft_artifact = new
                {
                    selectedDraft.Artifact.Id,
                    selectedDraft.Artifact.ArtifactType,
                    selectedDraft.Artifact.RelativePath,
                    selectedDraft.Artifact.UpdatedUtc
                },
                file_artifact = new
                {
                    fileArtifact.Id,
                    fileArtifact.ArtifactType,
                    fileArtifact.RelativePath,
                    fileArtifact.UpdatedUtc
                },
                apply_artifact = new
                {
                    applyArtifact.Id,
                    applyArtifact.ArtifactType,
                    applyArtifact.RelativePath,
                    applyArtifact.UpdatedUtc
                },
                csharp_patch_contract_artifact = patchContractArtifact is null
                    ? null
                    : new
                    {
                        patchContractArtifact.Id,
                        patchContractArtifact.ArtifactType,
                        patchContractArtifact.RelativePath,
                        patchContractArtifact.UpdatedUtc
                    },
                csharp_patch_plan_artifact = patchPlanArtifact is null
                    ? null
                    : new
                    {
                        patchPlanArtifact.Id,
                        patchPlanArtifact.ArtifactType,
                        patchPlanArtifact.RelativePath,
                        patchPlanArtifact.UpdatedUtc
                    }
            });

            RecordExecutionSuccess(workspaceRoot, "apply_patch_draft", draft.TargetFilePath, summary, structuredDataJson);
            RecordFileTouch(workspaceRoot, request, draft.TargetFilePath, "patch_apply", artifactType: "patch_apply_result");
            return Success("apply_patch_draft", output, summary, "patch_applied", structuredDataJson);
        }
        catch (Exception ex)
        {
            var outcomeType = ex.Message.StartsWith("apply_patch_draft failed: target file no longer matches", StringComparison.OrdinalIgnoreCase)
                ? "mismatch_failure"
                : "execution_failure";
            var structuredDataJson = SerializeJson(new { draft });
            RecordExecutionFailure(workspaceRoot, "apply_patch_draft", outcomeType, draft.TargetFilePath, ex.Message, structuredDataJson);
            return Failure("apply_patch_draft", ex.Message, outcomeType, ex.Message, structuredDataJson);
        }
    }

    private ToolResult ExecuteVerifyPatchDraft(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var eligibility = _repairEligibilityService.EvaluateVerifyPatchDraft(workspaceRoot, _ramDbService);
        if (!eligibility.IsEligible)
        {
            var message = BuildRepairIneligibleMessage("verify_patch_draft", eligibility, workspaceRoot);
            return Failure("verify_patch_draft", message, "resolution_failure", message);
        }

        var preferredPath = ResolvePreferredPatchPath(workspaceRoot, request);
        var recentArtifacts = eligibility.RecentArtifacts;
        var selectedApply = TrySelectPatchApplyArtifact(recentArtifacts, preferredPath, out var selectionMessage);
        if (selectedApply is null)
            return Failure("verify_patch_draft", selectionMessage, "resolution_failure", selectionMessage);

        if (!_repairEligibilityService.HasValidRepairChain(selectedApply.Payload))
        {
            var invalidMessage =
                "verify_patch_draft failed: the selected patch apply result is not tied to a recorded repair chain."
                + Environment.NewLine
                + "Apply a patch draft created from a recorded failure before running verification.";
            return Failure("verify_patch_draft", invalidMessage, "resolution_failure", invalidMessage);
        }

        var proposal = TryLoadRepairProposalById(workspaceRoot, selectedApply.Payload.Draft.SourceProposalArtifactId);
        var patchContractArtifact = LoadArtifactByRelativePath(
            workspaceRoot,
            FirstNonEmpty(selectedApply.Payload.PatchContractArtifactRelativePath, selectedApply.Payload.Draft.PatchContractArtifactRelativePath));
        var patchContract = TryDeserializeArtifact<CSharpPatchWorkContractRecord>(patchContractArtifact);
        if (patchContract is null && proposal is not null)
        {
            patchContract = _cSharpPatchFoundationService.TryBuildRepairContract(workspaceRoot, proposal);
            if (patchContract is not null)
                patchContractArtifact = SaveCSharpPatchContractArtifact(workspaceRoot, patchContract, request);
        }

        var patchPlanArtifact = LoadArtifactByRelativePath(
            workspaceRoot,
            FirstNonEmpty(selectedApply.Payload.PatchPlanArtifactRelativePath, selectedApply.Payload.Draft.PatchPlanArtifactRelativePath));
        var patchPlan = TryDeserializeArtifact<CSharpPatchPlanRecord>(patchPlanArtifact);
        if (patchPlan is null && patchContract is not null && proposal is not null)
        {
            patchPlan = _cSharpPatchFoundationService.BuildPlan(patchContract, proposal, selectedApply.Payload.Draft);
            patchPlanArtifact = SaveCSharpPatchPlanArtifact(workspaceRoot, patchPlan, request);
        }

        var stateBefore = _ramDbService.LoadExecutionState(workspaceRoot);
        var activeTarget = GetOptionalArgument(request, "active_target");
        var plan = _patchVerificationPlanner.Build(workspaceRoot, selectedApply.Payload, proposal, stateBefore, activeTarget);
        plan.SourcePatchContractId = FirstNonEmpty(
            patchContract?.ContractId ?? "",
            selectedApply.Payload.PatchContractId,
            selectedApply.Payload.Draft.PatchContractId);
        plan.SourcePatchPlanId = FirstNonEmpty(
            patchPlan?.PlanId ?? "",
            selectedApply.Payload.PatchPlanId,
            selectedApply.Payload.Draft.PatchPlanId);
        plan.ModificationIntent = FirstNonEmpty(
            patchContract?.ModificationIntent ?? "",
            selectedApply.Payload.Draft.ModificationIntent,
            plan.ModificationIntent);
        plan.TargetSurfaceType = FirstNonEmpty(
            patchContract?.TargetSurfaceType ?? "",
            selectedApply.Payload.Draft.TargetSurfaceType,
            plan.TargetSurfaceType);
        plan.TargetFiles = patchContract is not null && patchContract.TargetFiles.Count > 0
            ? [.. patchContract.TargetFiles]
            : plan.TargetFiles.Count > 0
                ? [.. plan.TargetFiles]
                : BuildPatchVerificationTargetFiles(selectedApply.Payload.Draft);
        plan.WarningPolicyMode = FirstNonEmpty(
            patchContract?.WarningPolicyMode ?? "",
            selectedApply.Payload.Draft.WarningPolicyMode,
            "track_only");
        if (patchContract is not null)
        {
            plan.ValidationRequirements = [.. patchContract.ValidationRequirements];
            plan.RerunRequirements = [.. patchContract.RerunRequirements];
        }

        var planArtifact = SaveVerificationPlanArtifact(workspaceRoot, plan);

        ToolResult? verificationResult = null;
        if (!string.Equals(plan.VerificationTool, "read_only_check", StringComparison.OrdinalIgnoreCase))
        {
            var verificationRequest = new ToolRequest
            {
                ToolName = plan.VerificationTool,
                Reason = $"Verification for patch draft {selectedApply.Payload.Draft.DraftId}",
                ExecutionSourceType = ExecutionSourceType.Verification,
                ExecutionSourceName = "verify_patch_draft",
                IsAutomaticTrigger = false,
                ExecutionAllowed = true,
                ExecutionPolicyMode = "explicit_manual",
                ExecutionScopeRiskClassification = "safe_narrow",
                ExecutionBuildFamily = plan.BuildSystemType,
                TaskboardRunStateId = request.TaskboardRunStateId,
                TaskboardPlanImportId = request.TaskboardPlanImportId,
                TaskboardPlanTitle = request.TaskboardPlanTitle,
                TaskboardBatchId = request.TaskboardBatchId,
                TaskboardBatchTitle = request.TaskboardBatchTitle,
                TaskboardWorkItemId = request.TaskboardWorkItemId,
                TaskboardWorkItemTitle = request.TaskboardWorkItemTitle
            };

            AppendVerificationTargetArguments(plan, verificationRequest);
            if (!string.IsNullOrWhiteSpace(plan.Filter))
                verificationRequest.Arguments["filter"] = plan.Filter;

            var gateDecision = BuildExecutionGateDecision(
                verificationRequest,
                plan.VerificationTool,
                plan.BuildSystemType,
                true,
                verificationRequest.ExecutionScopeRiskClassification,
                plan.TargetPath);

            verificationResult = gateDecision.IsAllowed
                ? Execute(verificationRequest)
                : BuildExecutionGateDeniedToolResult(plan.VerificationTool, gateDecision, plan.TargetPath);
        }

        var outcome = _verificationOutcomeComparer.Compare(
            workspaceRoot,
            plan,
            selectedApply.Payload,
            stateBefore,
            verificationResult);
        var resultArtifact = SaveVerificationResultArtifact(workspaceRoot, outcome);
        ArtifactRecord? closureArtifact = null;
        if (string.Equals(outcome.OutcomeClassification, "verified_fixed", StringComparison.OrdinalIgnoreCase))
            closureArtifact = SaveRepairLoopClosureArtifact(workspaceRoot, selectedApply.Payload, plan, outcome);

        var output = _verifyPatchDraftTool.Format(plan, outcome, planArtifact, resultArtifact, closureArtifact);
        var structuredDataJson = SerializeJson(new
        {
            source_patch_apply_artifact = new
            {
                selectedApply.Artifact.Id,
                selectedApply.Artifact.ArtifactType,
                selectedApply.Artifact.RelativePath,
                selectedApply.Artifact.UpdatedUtc
            },
            csharp_patch_contract = patchContract,
            csharp_patch_plan = patchPlan,
            plan,
            verification_result = verificationResult,
            outcome,
            plan_artifact = new
            {
                planArtifact.Id,
                planArtifact.ArtifactType,
                planArtifact.RelativePath,
                planArtifact.UpdatedUtc
            },
            result_artifact = new
            {
                resultArtifact.Id,
                resultArtifact.ArtifactType,
                resultArtifact.RelativePath,
                resultArtifact.UpdatedUtc
            },
            closure_artifact = closureArtifact is null
                ? null
                : new
                {
                    closureArtifact.Id,
                    closureArtifact.ArtifactType,
                    closureArtifact.RelativePath,
                    closureArtifact.UpdatedUtc
                },
            csharp_patch_contract_artifact = patchContractArtifact is null
                ? null
                : new
                {
                    patchContractArtifact.Id,
                    patchContractArtifact.ArtifactType,
                    patchContractArtifact.RelativePath,
                    patchContractArtifact.UpdatedUtc
                },
            csharp_patch_plan_artifact = patchPlanArtifact is null
                ? null
                : new
                {
                    patchPlanArtifact.Id,
                    patchPlanArtifact.ArtifactType,
                    patchPlanArtifact.RelativePath,
                    patchPlanArtifact.UpdatedUtc
                }
        });

        _ramDbService.SaveVerificationOutcome(
            workspaceRoot,
            plan.VerificationPlanId,
            selectedApply.Payload.Draft.DraftId,
            plan.VerificationTool,
            outcome.OutcomeClassification,
            FirstNonEmpty(outcome.ResolvedTarget, plan.TargetPath),
            outcome.Explanation,
            structuredDataJson);
        _ramDbService.SaveBuildProfileState(
            workspaceRoot,
            plan.BuildSystemType,
            plan.BuildSystemType,
            plan.TargetPath,
            "",
            "",
            "",
            plan.BuildSystemType);

        RecordFileTouch(
            workspaceRoot,
            request,
            FirstNonEmpty(outcome.ResolvedTarget, plan.TargetPath, selectedApply.Payload.Draft.TargetFilePath),
            "verify",
            artifactType: "verification_result",
            isProductiveTouch: true,
            contentChanged: false);

        return Success(
            "verify_patch_draft",
            output,
            outcome.Explanation,
            outcome.OutcomeClassification,
            structuredDataJson);
    }

    private string ExecuteSaveOutput(ToolRequest request)
    {
        var workspaceRoot = RequireWorkspace();
        var path = ResolveWorkspacePath(GetRequiredArgument(request, "path"));
        var content = request.TryGetArgument("content", out var requestedContent)
            ? requestedContent
            : "";

        var saveMessage = _saveOutputTool.SaveText(workspaceRoot, path, content);
        var artifact = SaveArtifactRecord(workspaceRoot, path, content, request);

        return saveMessage
            + Environment.NewLine
            + $"Artifact stored in SQLite: #{artifact.Id}"
            + Environment.NewLine
            + $"Artifact synced: {artifact.RelativePath}"
            + BuildAutoValidationSuffixForFileChange(workspaceRoot, artifact, "builder_output");
    }

    private string ExecuteShowArtifacts()
    {
        var workspaceRoot = RequireWorkspace();
        var rows = _ramDbService.LoadLatestArtifacts(workspaceRoot, 10);

        if (rows.Count == 0)
            return "No artifacts found.";

        var lines = new List<string>
        {
            "Recent artifacts:"
        };

        foreach (var row in rows)
        {
            lines.Add($"- {row.Title} [{row.ArtifactType}] {row.RelativePath} ({row.UpdatedUtc})");
        }

        lines.Add($"Active artifact: {rows[0].RelativePath}");
        return string.Join(Environment.NewLine, lines);
    }

    private string ExecuteShowMemory()
    {
        var workspaceRoot = RequireWorkspace();
        var rows = _ramDbService.LoadRecentMemorySummaries(workspaceRoot, 10);

        if (rows.Count == 0)
            return "No memory summaries found.";

        var lines = new List<string>
        {
            "Recent memory summaries:"
        };

        foreach (var row in rows)
        {
            lines.Add($"- [{row.CreatedUtc}] {row.SourceType}/{row.SourceId}: {row.SummaryText}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private WorkspaceBuildProfileRecord? ResolveBuildProfileOrFailure(
        string workspaceRoot,
        BuildSystemType requestedType,
        string requestedPath,
        out string message)
    {
        var detection = _buildSystemDetectionService.Detect(workspaceRoot);
        PersistBuildSystemDetection(workspaceRoot, detection);

        var normalizedRequestedPath = NormalizeRequestedBuildPath(workspaceRoot, requestedPath);
        return _buildSystemDetectionService.ResolveProfile(
            workspaceRoot,
            requestedType,
            normalizedRequestedPath,
            out message);
    }

    private ToolResult? TryBlockBroadNativeBuildExecution(
        string workspaceRoot,
        string toolName,
        WorkspaceBuildProfileRecord profile,
        string resolvedTargetPath,
        string explicitPath,
        string explicitTarget,
        string resolutionMessage)
    {
        var assessment = _buildScopeAssessmentService.Assess(
            workspaceRoot,
            profile,
            toolName,
            resolvedTargetPath,
            explicitPath,
            explicitTarget);
        if (assessment.LiveExecutionAllowed)
            return null;

        PersistBuildProfileSelection(workspaceRoot, profile, toolName, "", "");

        var summary = assessment.Reason;
        var structuredDataJson = BuildScopeBlockedDataJson(toolName, resolvedTargetPath, summary, profile, assessment);
        var blockArtifact = SaveBuildExecutionArtifact(workspaceRoot, "build_scope_block", toolName, resolvedTargetPath, summary, structuredDataJson);
        RecordExecutionFailure(workspaceRoot, toolName, "safety_blocked_scope", resolvedTargetPath, summary, structuredDataJson);

        var output = string.Join(
            Environment.NewLine,
            _buildScopeAssessmentService.BuildBlockedMessage(assessment),
            resolutionMessage,
            $"Artifact synced: {blockArtifact.RelativePath}");

        return Failure(toolName, output, "safety_blocked_scope", summary, structuredDataJson);
    }

    private ToolResult FinalizeGenericBuildExecution(
        string workspaceRoot,
        ToolRequest request,
        string toolName,
        WorkspaceBuildProfileRecord profile,
        string targetPath,
        string resolutionMessage,
        CommandExecutionResult result,
        DotnetBuildParseResult parsed,
        string resultArtifactType)
    {
        PersistBuildProfileSelection(workspaceRoot, profile, toolName, resultArtifactType == "configure_result" ? toolName : "", "");

        var outcomeType = DetermineGenericBuildOutcome(result, parsed);
        var summary = outcomeType == "success"
            ? parsed.Summary
            : outcomeType == "build_failure"
                ? parsed.Summary
                : BuildExecutionFailureSummary(toolName, result);
        var structuredDataJson = BuildFailureDataJson(toolName, outcomeType, targetPath, summary, parsed, result);
        var resultArtifact = SaveBuildExecutionArtifact(workspaceRoot, resultArtifactType, toolName, targetPath, summary, structuredDataJson);
        ArtifactRecord? safetyArtifact = null;
        if (_executionSafetyPolicyService.IsSafetyOutcome(outcomeType))
            safetyArtifact = SaveExecutionSafetyArtifact(workspaceRoot, toolName, targetPath, summary, structuredDataJson);

        if (outcomeType == "success")
        {
            var output = BuildGenericBuildSuccessOutput(resolutionMessage, targetPath, parsed, result, resultArtifact);
            RecordExecutionSuccess(workspaceRoot, toolName, targetPath, summary, structuredDataJson);
            return Success(toolName, output, summary, "success", structuredDataJson);
        }

        var outputFailure = BuildGenericBuildFailureOutput(toolName, resolutionMessage, targetPath, parsed, result, summary, resultArtifact);
        if (safetyArtifact is not null)
            outputFailure += Environment.NewLine + $"Artifact synced: {safetyArtifact.RelativePath}";
        RecordExecutionFailure(workspaceRoot, toolName, outcomeType, targetPath, summary, structuredDataJson);

        if (outcomeType == "build_failure")
        {
            SaveFailureArtifact(
                workspaceRoot,
                "build_failure_summary",
                targetPath,
                toolName,
                summary,
                structuredDataJson);
            var repairContext = _repairContextService.BuildForBuildFailure(workspaceRoot, targetPath, parsed, toolName);
            var repairArtifact = _repairContextService.SaveRepairContextArtifact(workspaceRoot, _ramDbService, repairContext);
            outputFailure += Environment.NewLine + $"Artifact synced: {repairArtifact.RelativePath}";
        }

        return Failure(toolName, outputFailure, outcomeType, summary, structuredDataJson);
    }

    private void PersistBuildSystemDetection(string workspaceRoot, BuildSystemDetectionResult detection)
    {
        SaveCustomArtifact(
            workspaceRoot,
            "build_system_detection",
            "Build system detection",
            ".ram/build-system/detection.json",
            SerializeJson(detection),
            detection.Summary);

        if (detection.PreferredProfile is not null)
            SaveBuildProfileArtifact(workspaceRoot, detection.PreferredProfile);

        _ramDbService.SaveBuildProfileState(
            workspaceRoot,
            NormalizeBuildSystemType(detection.DetectedType),
            detection.PreferredProfile is null ? "" : NormalizeBuildSystemType(detection.PreferredProfile.BuildSystemType),
            detection.PreferredProfile?.PrimaryTargetPath ?? "",
            detection.PreferredProfile is null ? "" : SerializeJson(detection.PreferredProfile),
            "",
            "",
            "");
    }

    private void PersistBuildProfileSelection(
        string workspaceRoot,
        WorkspaceBuildProfileRecord profile,
        string buildToolFamily,
        string configureToolName,
        string verificationFamily)
    {
        SaveBuildProfileArtifact(workspaceRoot, profile);
        _ramDbService.SaveBuildProfileState(
            workspaceRoot,
            NormalizeBuildSystemType(profile.BuildSystemType),
            NormalizeBuildSystemType(profile.BuildSystemType),
            profile.PrimaryTargetPath,
            SerializeJson(profile),
            configureToolName,
            buildToolFamily,
            verificationFamily);
    }

    private ArtifactRecord SaveBuildProfileArtifact(string workspaceRoot, WorkspaceBuildProfileRecord profile)
    {
        var relativePath = $".ram/build-system/profiles/{NormalizeBuildSystemType(profile.BuildSystemType)}.json";
        return SaveCustomArtifact(
            workspaceRoot,
            "build_profile",
            $"Build profile: {profile.BuildSystemType}",
            relativePath,
            SerializeJson(profile),
            $"Preferred {profile.BuildSystemType} profile target: {DisplayValue(profile.PrimaryTargetPath)}.");
    }

    private ArtifactRecord SaveBuildExecutionArtifact(
        string workspaceRoot,
        string artifactType,
        string toolName,
        string targetPath,
        string summary,
        string content)
    {
        var artifactFolder = artifactType switch
        {
            "configure_result" => "configure-results",
            "build_scope_block" => "build-scope-blocks",
            _ => "build-results"
        };
        var relativePath = $".ram/{artifactFolder}/{toolName}-{BuildArtifactSlug(targetPath)}.json";
        return SaveCustomArtifact(
            workspaceRoot,
            artifactType,
            $"{toolName}: {DisplayValue(targetPath)}",
            relativePath,
            content,
            summary);
    }

    private string BuildScopeBlockedDataJson(
        string toolName,
        string targetPath,
        string summary,
        WorkspaceBuildProfileRecord profile,
        BuildScopeAssessmentRecord assessment)
    {
        return SerializeJson(new
        {
            tool_name = toolName,
            outcome_type = "safety_blocked_scope",
            target_path = NormalizeRelativePath(targetPath),
            summary,
            captured_utc = DateTime.UtcNow.ToString("O"),
            profile = new
            {
                build_system_type = NormalizeBuildSystemType(profile.BuildSystemType),
                primary_target_path = profile.PrimaryTargetPath,
                build_target_path = profile.BuildTargetPath,
                build_directory_path = profile.BuildDirectoryPath,
                configure_target_path = profile.ConfigureTargetPath
            },
            assessment
        });
    }

    private ArtifactRecord SaveExecutionSafetyArtifact(
        string workspaceRoot,
        string toolName,
        string targetPath,
        string summary,
        string content)
    {
        var relativePath = $".ram/execution-safety/{toolName}-{BuildArtifactSlug(targetPath)}.json";
        return SaveCustomArtifact(
            workspaceRoot,
            "execution_safety_result",
            $"Execution safety: {toolName} {DisplayValue(targetPath)}",
            relativePath,
            content,
            summary);
    }

    private ArtifactRecord SaveCustomArtifact(
        string workspaceRoot,
        string artifactType,
        string title,
        string relativePath,
        string content,
        string summary)
    {
        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();
        artifact.IntentTitle = "";
        artifact.ArtifactType = artifactType;
        artifact.Title = title;
        artifact.RelativePath = relativePath;
        artifact.Content = content;
        artifact.Summary = summary;

        if (existingArtifact is null)
            return _ramDbService.SaveArtifact(workspaceRoot, artifact);

        _ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private void RecordFileTouch(
        string workspaceRoot,
        ToolRequest request,
        string? path,
        string operationType,
        string artifactType = "",
        bool isProductiveTouch = true,
        bool contentChanged = true)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(path))
            return;

        var normalizedPath = NormalizeTrackedFilePath(workspaceRoot, path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return;

        _ramDbService.AddFileTouchRecord(workspaceRoot, new RamFileTouchRecord
        {
            RunStateId = request.TaskboardRunStateId ?? "",
            PlanImportId = request.TaskboardPlanImportId ?? "",
            BatchId = request.TaskboardBatchId ?? "",
            WorkItemId = request.TaskboardWorkItemId ?? "",
            WorkItemTitle = request.TaskboardWorkItemTitle ?? "",
            FilePath = normalizedPath,
            OperationType = operationType ?? "",
            Reason = FirstNonEmpty(request.Reason ?? "", request.TaskboardWorkItemTitle ?? "", request.ExecutionSourceName ?? ""),
            SourceActionName = FirstNonEmpty(request.ExecutionSourceName ?? "", request.ToolName ?? ""),
            ArtifactType = artifactType ?? "",
            IsProductiveTouch = isProductiveTouch,
            ContentChanged = contentChanged
        });
    }

    private string NormalizeTrackedFilePath(string workspaceRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        try
        {
            return NormalizeWorkspaceRelativePath(workspaceRoot, path);
        }
        catch
        {
            return NormalizeRelativePath(path);
        }
    }

    private static string DetermineFileTouchOperation(string toolName)
    {
        return NormalizeToolName(toolName) switch
        {
            "create_file" => "create",
            "append_file" => "append",
            "replace_in_file" or "apply_patch_draft" => "patch",
            "make_dir" => "create_dir",
            "read_file" or "read_file_chunk" or "inspect_project" or "file_info" => "read",
            "dotnet_build" or "dotnet_test" or "verify_patch_draft" => "verify",
            _ => "write"
        };
    }

    private string BuildGenericBuildSuccessOutput(
        string resolutionMessage,
        string targetPath,
        DotnetBuildParseResult parsed,
        CommandExecutionResult result,
        ArtifactRecord resultArtifact)
    {
        return string.Join(
            Environment.NewLine,
            resolutionMessage,
            $"Resolved target: {DisplayValue(targetPath)}",
            $"Summary: {parsed.Summary}",
            $"Artifact synced: {resultArtifact.RelativePath}",
            _commandExecutionService.FormatCompactResult(result.DisplayCommand, result));
    }

    private string BuildGenericBuildFailureOutput(
        string toolName,
        string resolutionMessage,
        string targetPath,
        DotnetBuildParseResult parsed,
        CommandExecutionResult result,
        string summary,
        ArtifactRecord resultArtifact)
    {
        var lines = new List<string>
        {
            $"{toolName} failed:",
            resolutionMessage,
            $"Resolved target: {DisplayValue(targetPath)}",
            $"Summary: {summary}",
            $"Artifact synced: {resultArtifact.RelativePath}"
        };

        if (parsed.TopErrors.Count > 0)
        {
            lines.Add("Top errors:");
            foreach (var error in parsed.TopErrors)
            {
                var location = string.IsNullOrWhiteSpace(error.FilePath) ? DisplayValue(error.RawPath) : error.FilePath;
                var lineSuffix = error.LineNumber > 0 ? $":{error.LineNumber}" : "";
                var columnSuffix = error.ColumnNumber > 0 ? $":{error.ColumnNumber}" : "";
                var codeSuffix = string.IsNullOrWhiteSpace(error.Code) ? "" : $" {error.Code}";
                lines.Add($"- {location}{lineSuffix}{columnSuffix}{codeSuffix} {error.Message}".Trim());
            }
        }

        lines.Add(_commandExecutionService.FormatCompactResult(result.DisplayCommand, result));
        return string.Join(Environment.NewLine, lines);
    }

    private static string DetermineGenericBuildOutcome(CommandExecutionResult result, DotnetBuildParseResult parsed)
    {
        if (!string.IsNullOrWhiteSpace(result.SafetyOutcomeType))
            return result.SafetyOutcomeType;

        if (result.ExitCode == 0 && !result.TimedOut && parsed.Success)
            return "success";

        if (result.TimedOut)
            return "timed_out";

        if (!parsed.Success || parsed.ErrorCount > 0 || parsed.TopErrors.Count > 0)
            return "build_failure";

        return "execution_failure";
    }

    private string NormalizeWorkspaceRelativeDirectory(string workspaceRoot, string path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? "." : path;
        return NormalizeWorkspaceRelativePath(workspaceRoot, normalized);
    }

    private string NormalizeRequestedBuildPath(string workspaceRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        try
        {
            return NormalizeWorkspaceRelativePath(workspaceRoot, path);
        }
        catch
        {
            return NormalizeRelativePath(path);
        }
    }

    private static string BuildArtifactSlug(string targetPath)
    {
        var slugSource = string.IsNullOrWhiteSpace(targetPath) ? "workspace" : NormalizeRelativePath(targetPath);
        var slug = new string(slugSource
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');

        return string.IsNullOrWhiteSpace(slug) ? "workspace" : slug;
    }

    private static string NormalizeBuildSystemType(BuildSystemType buildSystemType)
    {
        return buildSystemType.ToString().ToLowerInvariant();
    }

    private static void AppendVerificationTargetArguments(VerificationPlanRecord plan, ToolRequest request)
    {
        if (string.IsNullOrWhiteSpace(plan.TargetPath))
            return;

        switch (plan.VerificationTool)
        {
            case "dotnet_build":
            case "dotnet_test":
                request.Arguments["project"] = plan.TargetPath;
                break;
            case "cmake_build":
                request.Arguments["build_dir"] = plan.TargetPath;
                break;
            case "make_build":
            case "ninja_build":
                request.Arguments["directory"] = plan.TargetPath;
                break;
            case "run_build_script":
                request.Arguments["path"] = plan.TargetPath;
                break;
        }
    }

    private ArtifactRecord SaveArtifactRecord(string workspaceRoot, string fullPath, string content, ToolRequest request)
    {
        var relativePath = NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, fullPath));
        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();
        var contentChanged = existingArtifact is null || !string.Equals(existingArtifact.Content, content, StringComparison.Ordinal);

        artifact.IntentTitle = request.TryGetArgument("intent_title", out var intentTitle)
            ? intentTitle
            : "";
        artifact.ArtifactType = request.TryGetArgument("artifact_type", out var artifactType)
            ? artifactType
            : DetectArtifactType(fullPath);
        artifact.Title = request.TryGetArgument("title", out var title)
            ? title
            : Path.GetFileName(relativePath);
        artifact.RelativePath = relativePath;
        artifact.Content = content;
        artifact.Summary = BuildArtifactSummary(content);
        artifact.SourceRunStateId = request.TaskboardRunStateId ?? "";
        artifact.SourceBatchId = request.TaskboardBatchId ?? "";
        artifact.SourceWorkItemId = request.TaskboardWorkItemId ?? "";

        ArtifactRecord savedArtifact;
        if (existingArtifact is null)
            savedArtifact = _ramDbService.SaveArtifact(workspaceRoot, artifact);
        else
        {
            _ramDbService.UpdateArtifact(workspaceRoot, artifact);
            savedArtifact = artifact;
        }

        RecordFileTouch(
            workspaceRoot,
            request,
            relativePath,
            DetermineFileTouchOperation(request.ToolName),
            artifact.ArtifactType,
            isProductiveTouch: true,
            contentChanged: contentChanged);

        return savedArtifact;
    }

    private ArtifactRecord SyncArtifactFromFile(string workspaceRoot, string fullPath, ToolRequest request)
    {
        var content = File.ReadAllText(fullPath);
        return SaveArtifactRecord(workspaceRoot, fullPath, content, request);
    }

    private ArtifactRecord SaveFailureArtifact(
        string workspaceRoot,
        string artifactType,
        string targetPath,
        string toolName,
        string summary,
        string content)
    {
        var relativePath = BuildFailureArtifactPath(artifactType, targetPath);
        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();

        artifact.IntentTitle = "";
        artifact.ArtifactType = artifactType;
        artifact.Title = $"{toolName} failure: {DisplayValue(targetPath)}";
        artifact.RelativePath = relativePath;
        artifact.Content = content;
        artifact.Summary = summary;

        if (existingArtifact is null)
            return _ramDbService.SaveArtifact(workspaceRoot, artifact);

        _ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private ArtifactRecord SaveRepairProposalArtifact(string workspaceRoot, RepairProposalRecord proposal)
    {
        var relativePath = BuildRepairProposalArtifactPath(proposal.TargetFilePath, proposal.ProposalId);
        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();

        artifact.IntentTitle = "";
        artifact.ArtifactType = "repair_proposal";
        artifact.Title = proposal.Title;
        artifact.RelativePath = relativePath;
        artifact.Content = SerializeJson(proposal);
        artifact.Summary = BuildRepairProposalSummary(proposal);

        if (existingArtifact is null)
            return _ramDbService.SaveArtifact(workspaceRoot, artifact);

        _ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private ArtifactRecord SaveCSharpPatchContractArtifact(string workspaceRoot, CSharpPatchWorkContractRecord contract, ToolRequest request)
    {
        var relativePath = BuildCSharpPatchContractArtifactPath(contract.TargetFiles.FirstOrDefault() ?? contract.TargetProjectPath, contract.ContractId);
        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();

        artifact.IntentTitle = "";
        artifact.ArtifactType = "csharp_patch_contract";
        artifact.Title = $"C# patch contract: {DisplayValue(contract.TargetFiles.FirstOrDefault() ?? contract.TargetProjectPath)}";
        artifact.RelativePath = relativePath;
        artifact.Content = SerializeJson(contract);
        artifact.Summary = BuildCSharpPatchContractSummary(contract);
        artifact.SourceRunStateId = request.TaskboardRunStateId ?? "";
        artifact.SourceBatchId = request.TaskboardBatchId ?? "";
        artifact.SourceWorkItemId = request.TaskboardWorkItemId ?? "";

        if (existingArtifact is null)
            return _ramDbService.SaveArtifact(workspaceRoot, artifact);

        _ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private ArtifactRecord SaveCSharpPatchPlanArtifact(string workspaceRoot, CSharpPatchPlanRecord plan, ToolRequest request)
    {
        var relativePath = BuildCSharpPatchPlanArtifactPath(plan.TargetFiles.FirstOrDefault() ?? plan.TargetProjectPath, plan.PlanId);
        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();

        artifact.IntentTitle = "";
        artifact.ArtifactType = "csharp_patch_plan";
        artifact.Title = $"C# patch plan: {DisplayValue(plan.TargetFiles.FirstOrDefault() ?? plan.TargetProjectPath)}";
        artifact.RelativePath = relativePath;
        artifact.Content = SerializeJson(plan);
        artifact.Summary = BuildCSharpPatchPlanSummary(plan);
        artifact.SourceRunStateId = request.TaskboardRunStateId ?? "";
        artifact.SourceBatchId = request.TaskboardBatchId ?? "";
        artifact.SourceWorkItemId = request.TaskboardWorkItemId ?? "";

        if (existingArtifact is null)
            return _ramDbService.SaveArtifact(workspaceRoot, artifact);

        _ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private ArtifactRecord SavePatchDraftArtifact(string workspaceRoot, PatchDraftRecord draft)
    {
        var relativePath = BuildPatchDraftArtifactPath(draft.TargetFilePath, draft.DraftId);
        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();

        artifact.IntentTitle = "";
        artifact.ArtifactType = "patch_draft";
        artifact.Title = $"Patch draft: {DisplayValue(draft.TargetFilePath)}";
        artifact.RelativePath = relativePath;
        artifact.Content = SerializeJson(draft);
        artifact.Summary = BuildPatchDraftSummary(draft);

        if (existingArtifact is null)
            return _ramDbService.SaveArtifact(workspaceRoot, artifact);

        _ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private ArtifactRecord SavePatchApplyArtifact(string workspaceRoot, PatchApplyResultRecord applyRecord)
    {
        var draft = applyRecord.Draft;
        var relativePath = BuildPatchApplyArtifactPath(draft.TargetFilePath, draft.DraftId);
        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();

        artifact.IntentTitle = "";
        artifact.ArtifactType = "patch_apply_result";
        artifact.Title = $"Applied patch: {DisplayValue(draft.TargetFilePath)}";
        artifact.RelativePath = relativePath;
        artifact.Content = SerializeJson(applyRecord);
        artifact.Summary = $"Applied patch draft to {DisplayValue(draft.TargetFilePath)}.";

        if (existingArtifact is null)
            return _ramDbService.SaveArtifact(workspaceRoot, artifact);

        _ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private ArtifactRecord SaveVerificationPlanArtifact(string workspaceRoot, VerificationPlanRecord plan)
    {
        var relativePath = BuildVerificationPlanArtifactPath(plan.TargetPath, plan.VerificationPlanId);
        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();

        artifact.IntentTitle = "";
        artifact.ArtifactType = "verification_plan";
        artifact.Title = $"Verification plan: {DisplayValue(plan.TargetPath)}";
        artifact.RelativePath = relativePath;
        artifact.Content = SerializeJson(plan);
        artifact.Summary = BuildVerificationPlanSummary(plan);

        if (existingArtifact is null)
            return _ramDbService.SaveArtifact(workspaceRoot, artifact);

        _ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private ArtifactRecord SaveVerificationResultArtifact(string workspaceRoot, VerificationOutcomeRecord outcome)
    {
        var relativePath = BuildVerificationResultArtifactPath(outcome.ResolvedTarget, outcome.VerificationPlanId);
        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();

        artifact.IntentTitle = "";
        artifact.ArtifactType = "verification_result";
        artifact.Title = $"Verification result: {DisplayValue(outcome.ResolvedTarget)}";
        artifact.RelativePath = relativePath;
        artifact.Content = SerializeJson(outcome);
        artifact.Summary = BuildVerificationResultSummary(outcome);

        if (existingArtifact is null)
            return _ramDbService.SaveArtifact(workspaceRoot, artifact);

        _ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private string BuildAutoValidationSuffixForFileChange(string workspaceRoot, ArtifactRecord sourceArtifact, string sourceActionType)
    {
        var plan = _autoValidationPlanner.BuildForFileChange(
            workspaceRoot,
            sourceArtifact.Id,
            sourceArtifact.ArtifactType,
            sourceActionType,
            [NormalizeRelativePath(sourceArtifact.RelativePath)]);
        return RunAutoValidationPlan(workspaceRoot, plan);
    }

    private string BuildAutoValidationSuffixForPatchApply(
        string workspaceRoot,
        ArtifactRecord sourceArtifact,
        PatchApplyResultRecord applyRecord,
        RepairProposalRecord? proposal,
        WorkspaceExecutionStateRecord stateBeforeApply)
    {
        var plan = _autoValidationPlanner.BuildForPatchApply(
            workspaceRoot,
            sourceArtifact.Id,
            sourceArtifact.ArtifactType,
            applyRecord,
            proposal,
            stateBeforeApply,
            applyRecord.Draft.TargetFilePath);
        return RunAutoValidationPlan(workspaceRoot, plan);
    }

    private string RunAutoValidationPlan(string workspaceRoot, AutoValidationPlanRecord plan)
    {
        var planArtifact = SaveAutoValidationPlanArtifact(workspaceRoot, plan);

        if (ShouldSkipManualOnlyCooldown(workspaceRoot, plan, out var manualOnlySkipReason))
        {
            var skippedResult = BuildAutoValidationSkippedResult(workspaceRoot, plan, manualOnlySkipReason);
            var skippedArtifact = SaveAutoValidationResultArtifact(workspaceRoot, skippedResult);
            RememberManualOnlyCooldown(plan);
            RecordNoExecutionPathTaken(workspaceRoot, plan, skippedResult.Summary);
            return BuildAutoValidationOutput(plan, skippedResult, planArtifact, skippedArtifact);
        }

        if (_autoValidationPlanner.ShouldSkipDuplicate(workspaceRoot, plan, _ramDbService, out var skipReason))
        {
            var skippedResult = BuildAutoValidationSkippedResult(workspaceRoot, plan, skipReason);
            var skippedArtifact = SaveAutoValidationResultArtifact(workspaceRoot, skippedResult);
            RecordNoExecutionPathTaken(workspaceRoot, plan, skippedResult.Summary);
            return BuildAutoValidationOutput(plan, skippedResult, planArtifact, skippedArtifact);
        }

        if (!plan.ExecutionAllowed || string.IsNullOrWhiteSpace(plan.SelectedValidationTool))
        {
            var blockedResult = BuildAutoValidationBlockedResult(workspaceRoot, plan);
            var blockedArtifact = SaveAutoValidationResultArtifact(workspaceRoot, blockedResult);
            RememberManualOnlyCooldown(plan);
            RecordNoExecutionPathTaken(workspaceRoot, plan, blockedResult.Summary);
            return BuildAutoValidationOutput(plan, blockedResult, planArtifact, blockedArtifact);
        }

        var validationRequest = new ToolRequest
        {
            ToolName = plan.SelectedValidationTool,
            Reason = $"Auto-validation for {plan.SourceActionType}",
            ExecutionSourceType = ExecutionSourceType.AutoValidation,
            ExecutionSourceName = plan.SourceActionType,
            IsAutomaticTrigger = true,
            ExecutionAllowed = plan.ExecutionAllowed,
            ExecutionPolicyMode = plan.PolicyMode,
            ExecutionScopeRiskClassification = plan.ScopeRiskClassification,
            ExecutionBuildFamily = plan.BuildFamily
        };
        AppendAutoValidationTargetArguments(plan, validationRequest);

        var gateDecision = BuildExecutionGateDecision(
            validationRequest,
            plan.SelectedValidationTool,
            plan.BuildFamily,
            plan.ExecutionAllowed,
            plan.ScopeRiskClassification,
            plan.SelectedTargetPath);
        if (!gateDecision.IsAllowed)
        {
            var blockedResult = BuildAutoValidationGateDeniedResult(workspaceRoot, plan, gateDecision);
            var blockedArtifact = SaveAutoValidationResultArtifact(workspaceRoot, blockedResult);
            RememberManualOnlyCooldown(plan);
            RecordExecutionBlocked(workspaceRoot, gateDecision, plan.SelectedValidationTool, blockedResult.Summary);
            return BuildAutoValidationOutput(plan, blockedResult, planArtifact, blockedArtifact);
        }

        var toolResult = Execute(validationRequest);
        var result = BuildAutoValidationExecutedResult(workspaceRoot, plan, toolResult);
        var resultArtifact = SaveAutoValidationResultArtifact(workspaceRoot, result);
        if (!result.ExecutionAttempted)
            RecordNoExecutionPathTaken(workspaceRoot, plan, result.Summary);
        return BuildAutoValidationOutput(plan, result, planArtifact, resultArtifact);
    }

    private bool ShouldSkipManualOnlyCooldown(string workspaceRoot, AutoValidationPlanRecord plan, out string reason)
    {
        reason = "";
        if (!string.Equals(plan.PolicyMode, "manual_only", StringComparison.OrdinalIgnoreCase))
            return false;

        var cooldownKey = BuildManualOnlyCooldownKey(workspaceRoot, plan);
        lock (ManualOnlyCooldowns)
        {
            if (ManualOnlyCooldowns.TryGetValue(cooldownKey, out var lastSeenUtc)
                && (DateTime.UtcNow - lastSeenUtc).TotalSeconds <= 20)
            {
                reason = "Skipped duplicate native auto-validation because the same native change family was already marked manual-only very recently.";
                return true;
            }
        }

        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(
            workspaceRoot,
            BuildAutoValidationResultArtifactPath(plan));
        if (existingArtifact is null || string.IsNullOrWhiteSpace(existingArtifact.Content))
            return false;

        AutoValidationResultRecord? result;
        try
        {
            result = JsonSerializer.Deserialize<AutoValidationResultRecord>(existingArtifact.Content);
        }
        catch
        {
            return false;
        }

        if (result is null
            || !string.Equals(result.OutcomeClassification, "manual_only", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(result.BuildFamily, plan.BuildFamily, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(result.SourceActionType, plan.SourceActionType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if ((DateTime.UtcNow - ParseUtc(result.CreatedUtc)).TotalSeconds > 20)
            return false;

        reason = "Skipped duplicate native auto-validation because the same native change family was already marked manual-only very recently.";
        return true;
    }

    private static void RememberManualOnlyCooldown(AutoValidationPlanRecord plan)
    {
        if (!string.Equals(plan.PolicyMode, "manual_only", StringComparison.OrdinalIgnoreCase))
            return;

        lock (ManualOnlyCooldowns)
        {
            ManualOnlyCooldowns[BuildManualOnlyCooldownKey(plan.WorkspaceRoot, plan)] = DateTime.UtcNow;
        }
    }

    private static void AppendAutoValidationTargetArguments(AutoValidationPlanRecord plan, ToolRequest request)
    {
        if (string.IsNullOrWhiteSpace(plan.SelectedValidationTool))
            return;

        switch (plan.SelectedValidationTool)
        {
            case "dotnet_build":
            case "dotnet_test":
                if (!string.IsNullOrWhiteSpace(plan.SelectedTargetPath))
                    request.Arguments["project"] = plan.SelectedTargetPath;
                break;
            case "cmake_configure":
                if (!string.IsNullOrWhiteSpace(plan.SelectedTargetPath))
                    request.Arguments["source_dir"] = plan.SelectedTargetPath;
                break;
            case "cmake_build":
                if (!string.IsNullOrWhiteSpace(plan.SelectedTargetPath))
                    request.Arguments["build_dir"] = plan.SelectedTargetPath;
                break;
            case "make_build":
            case "ninja_build":
                if (!string.IsNullOrWhiteSpace(plan.SelectedTargetPath))
                    request.Arguments["directory"] = plan.SelectedTargetPath;
                break;
            case "run_build_script":
                if (!string.IsNullOrWhiteSpace(plan.SelectedTargetPath))
                    request.Arguments["path"] = plan.SelectedTargetPath;
                break;
        }
    }

    private ArtifactRecord SaveAutoValidationPlanArtifact(string workspaceRoot, AutoValidationPlanRecord plan)
    {
        var relativePath = BuildAutoValidationPlanArtifactPath(plan);
        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();

        artifact.IntentTitle = "";
        artifact.ArtifactType = "auto_validation_plan";
        artifact.Title = $"Auto-validation plan: {DisplayValue(FirstNonEmpty(plan.SelectedTargetPath, plan.ChangedFilePaths.FirstOrDefault() ?? ""))}";
        artifact.RelativePath = relativePath;
        artifact.Content = SerializeJson(plan);
        artifact.Summary = BuildAutoValidationPlanSummary(plan);

        if (existingArtifact is null)
            return _ramDbService.SaveArtifact(workspaceRoot, artifact);

        _ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private ArtifactRecord SaveAutoValidationResultArtifact(string workspaceRoot, AutoValidationResultRecord result)
    {
        var relativePath = BuildAutoValidationResultArtifactPath(result);
        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();

        artifact.IntentTitle = "";
        artifact.ArtifactType = "auto_validation_result";
        artifact.Title = $"Auto-validation result: {DisplayValue(FirstNonEmpty(result.ResolvedTarget, result.ExecutedTool))}";
        artifact.RelativePath = relativePath;
        artifact.Content = SerializeJson(result);
        artifact.Summary = BuildAutoValidationResultSummary(result);

        if (existingArtifact is null)
            return _ramDbService.SaveArtifact(workspaceRoot, artifact);

        _ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private AutoValidationResultRecord BuildAutoValidationSkippedResult(string workspaceRoot, AutoValidationPlanRecord plan, string reason)
    {
        var outcome = string.Equals(plan.PolicyMode, "manual_only", StringComparison.OrdinalIgnoreCase)
            ? "manual_only"
            : "not_applicable";
        return new AutoValidationResultRecord
        {
            ResultId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            PlanId = plan.PlanId,
            SourceArtifactId = plan.SourceArtifactId,
            SourceArtifactType = plan.SourceArtifactType,
            SourceActionType = plan.SourceActionType,
            BuildFamily = plan.BuildFamily,
            ChangedFilePaths = [.. plan.ChangedFilePaths],
            ExecutedTool = plan.SelectedValidationTool,
            ResolvedTarget = plan.SelectedTargetPath,
            OutcomeClassification = outcome,
            Summary = reason,
            ExecutionAttempted = false,
            Explanation = reason,
            SuggestedNextStep = plan.RecommendedNextStep
        };
    }

    private AutoValidationResultRecord BuildAutoValidationBlockedResult(string workspaceRoot, AutoValidationPlanRecord plan)
    {
        var outcome = DetermineAutoValidationBlockedOutcome(plan);
        return new AutoValidationResultRecord
        {
            ResultId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            PlanId = plan.PlanId,
            SourceArtifactId = plan.SourceArtifactId,
            SourceArtifactType = plan.SourceArtifactType,
            SourceActionType = plan.SourceActionType,
            BuildFamily = plan.BuildFamily,
            ChangedFilePaths = [.. plan.ChangedFilePaths],
            ExecutedTool = plan.SelectedValidationTool,
            ResolvedTarget = plan.SelectedTargetPath,
            OutcomeClassification = outcome,
            Summary = FirstNonEmpty(plan.BlockedReason, plan.ValidationReason),
            ExecutionAttempted = false,
            SafetyTrigger = outcome == "scope_blocked" ? "safety_blocked_scope" : "",
            Explanation = FirstNonEmpty(plan.BlockedReason, plan.ValidationReason),
            SuggestedNextStep = plan.RecommendedNextStep
        };
    }

    private AutoValidationResultRecord BuildAutoValidationGateDeniedResult(
        string workspaceRoot,
        AutoValidationPlanRecord plan,
        ExecutionGateDecision gateDecision)
    {
        return new AutoValidationResultRecord
        {
            ResultId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            PlanId = plan.PlanId,
            SourceArtifactId = plan.SourceArtifactId,
            SourceArtifactType = plan.SourceArtifactType,
            SourceActionType = plan.SourceActionType,
            BuildFamily = plan.BuildFamily,
            ChangedFilePaths = [.. plan.ChangedFilePaths],
            ExecutedTool = plan.SelectedValidationTool,
            ResolvedTarget = plan.SelectedTargetPath,
            OutcomeClassification = DetermineAutoValidationBlockedOutcome(plan),
            Summary = gateDecision.BlockedReason,
            ExecutionAttempted = false,
            LinkedOutcomeType = "execution_gate_blocked",
            SafetyTrigger = "execution_gate_blocked",
            Explanation = gateDecision.BlockedReason,
            SuggestedNextStep = plan.RecommendedNextStep
        };
    }

    private AutoValidationResultRecord BuildAutoValidationExecutedResult(string workspaceRoot, AutoValidationPlanRecord plan, ToolResult toolResult)
    {
        var outcome = toolResult.Success
            ? "validated_success"
            : string.Equals(toolResult.OutcomeType, "safety_blocked_scope", StringComparison.OrdinalIgnoreCase)
                ? "scope_blocked"
                : _executionSafetyPolicyService.IsSafetyOutcome(toolResult.OutcomeType)
                    ? "safety_blocked"
                    : toolResult.OutcomeType is "build_failure" or "test_failure"
                        ? "validated_failure"
                        : toolResult.OutcomeType is "not_verifiable"
                            ? "not_verifiable"
                            : "execution_failed";

        return new AutoValidationResultRecord
        {
            ResultId = Guid.NewGuid().ToString("N"),
            WorkspaceRoot = workspaceRoot,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            PlanId = plan.PlanId,
            SourceArtifactId = plan.SourceArtifactId,
            SourceArtifactType = plan.SourceArtifactType,
            SourceActionType = plan.SourceActionType,
            BuildFamily = plan.BuildFamily,
            ChangedFilePaths = [.. plan.ChangedFilePaths],
            ExecutedTool = toolResult.ToolName,
            ResolvedTarget = FirstNonEmpty(plan.SelectedTargetPath, ExtractTargetPath(toolResult.StructuredDataJson)),
            OutcomeClassification = outcome,
            Summary = FirstNonEmpty(toolResult.Summary, toolResult.ErrorMessage, toolResult.Output),
            ExecutionAttempted = DidToolExecutionAttemptExternalCommand(toolResult),
            LinkedOutcomeType = toolResult.OutcomeType,
            TopFailures = ExtractAutoValidationFailures(toolResult),
            SafetyTrigger = _executionSafetyPolicyService.IsSafetyOutcome(toolResult.OutcomeType)
                || string.Equals(toolResult.OutcomeType, "safety_blocked_scope", StringComparison.OrdinalIgnoreCase)
                    ? toolResult.OutcomeType
                    : "",
            Explanation = toolResult.Success
                ? "Automatic validation completed successfully."
                : FirstNonEmpty(toolResult.Summary, toolResult.ErrorMessage),
            SuggestedNextStep = plan.RecommendedNextStep
        };
    }

    private static string DetermineAutoValidationBlockedOutcome(AutoValidationPlanRecord plan)
    {
        if (string.Equals(plan.PolicyMode, "manual_only", StringComparison.OrdinalIgnoreCase))
            return "manual_only";

        if (string.Equals(plan.PolicyMode, "scope_blocked", StringComparison.OrdinalIgnoreCase))
            return "scope_blocked";

        if (string.Equals(plan.PolicyMode, "safety_blocked", StringComparison.OrdinalIgnoreCase))
            return "safety_blocked";

        if (plan.ScopeRiskClassification is "high_broad" or "medium_narrowable")
            return "scope_blocked";

        if (plan.SourceActionType == "patch_apply")
            return "not_verifiable";

        return plan.ValidationReason.Contains("not code or build-system", StringComparison.OrdinalIgnoreCase)
            || plan.ValidationReason.Contains("no changed workspace files", StringComparison.OrdinalIgnoreCase)
            || plan.ValidationReason.Contains("duplicate auto-validation", StringComparison.OrdinalIgnoreCase)
                ? "not_applicable"
                : "not_verifiable";
    }

    private string BuildAutoValidationOutput(
        AutoValidationPlanRecord plan,
        AutoValidationResultRecord result,
        ArtifactRecord planArtifact,
        ArtifactRecord resultArtifact)
    {
        var lines = new List<string>
        {
            "",
            "Auto-validation:",
            $"Source: {plan.SourceActionType}",
            $"Build family: {DisplayValue(FirstNonEmpty(result.BuildFamily, plan.BuildFamily))}",
            $"Outcome: {result.OutcomeClassification}",
            $"Tool: {DisplayValue(FirstNonEmpty(result.ExecutedTool, plan.SelectedValidationTool))}",
            $"Target: {DisplayValue(FirstNonEmpty(result.ResolvedTarget, plan.SelectedTargetPath))}",
            $"Summary: {DisplayValue(result.Summary)}",
            $"Execution attempted: {(result.ExecutionAttempted ? "yes" : "no")}",
            $"Artifact synced: {planArtifact.RelativePath}",
            $"Artifact synced: {resultArtifact.RelativePath}"
        };

        if (!result.ExecutionAttempted)
            lines.Add("Execution: no external command launched.");

        if (result.ChangedFilePaths.Count > 0)
            lines.Add($"Changed files: {string.Join(", ", result.ChangedFilePaths.Take(3))}");

        if (!string.IsNullOrWhiteSpace(plan.SafetySummary))
            lines.Add($"Execution safety: {plan.SafetySummary}");

        if (!string.IsNullOrWhiteSpace(result.SuggestedNextStep))
            lines.Add($"Next: {result.SuggestedNextStep}");

        if (result.TopFailures.Count > 0)
        {
            lines.Add("Top failures:");
            foreach (var failure in result.TopFailures.Take(3))
                lines.Add($"- {failure}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private List<string> ExtractAutoValidationFailures(ToolResult toolResult)
    {
        if (string.Equals(toolResult.ToolName, "dotnet_test", StringComparison.OrdinalIgnoreCase))
        {
            var parsedTests = DeserializeParsedSection<DotnetTestParseResult>(toolResult.StructuredDataJson);
            return parsedTests?.FailingTests
                .Take(5)
                .Select(failure => string.IsNullOrWhiteSpace(failure.ResolvedSourcePath)
                    ? failure.TestName
                    : $"{failure.TestName} [{failure.ResolvedSourcePath}:{failure.SourceLine}]")
                .ToList() ?? [];
        }

        var parsedBuild = DeserializeParsedSection<DotnetBuildParseResult>(toolResult.StructuredDataJson);
        return parsedBuild?.TopErrors
            .Where(error => string.Equals(error.Severity, "error", StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .Select(error =>
            {
                var location = string.IsNullOrWhiteSpace(error.FilePath) ? DisplayValue(error.RawPath) : error.FilePath;
                var lineSuffix = error.LineNumber > 0 ? $":{error.LineNumber}" : "";
                var codeSuffix = string.IsNullOrWhiteSpace(error.Code) ? "" : $" {error.Code}";
                return $"{location}{lineSuffix}{codeSuffix} {error.Message}".Trim();
            })
            .ToList() ?? [];
    }

    private ArtifactRecord SaveRepairLoopClosureArtifact(
        string workspaceRoot,
        PatchApplyResultRecord applyRecord,
        VerificationPlanRecord plan,
        VerificationOutcomeRecord outcome)
    {
        var relativePath = BuildRepairLoopClosureArtifactPath(applyRecord.Draft.TargetFilePath, applyRecord.Draft.DraftId);
        var existingArtifact = _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
        var artifact = existingArtifact ?? new ArtifactRecord();

        artifact.IntentTitle = "";
        artifact.ArtifactType = "repair_loop_closure";
        artifact.Title = $"Verified repair: {DisplayValue(applyRecord.Draft.TargetFilePath)}";
        artifact.RelativePath = relativePath;
        artifact.Content = SerializeJson(new
        {
            source_failure = applyRecord.Draft.FailureSummary,
            patch_summary = applyRecord.Draft.ProposalSummary,
            verification_tool = plan.VerificationTool,
            verification_target = plan.TargetPath,
            verification_outcome = outcome
        });
        artifact.Summary = $"Verified fix for {DisplayValue(applyRecord.Draft.TargetFilePath)} via {DisplayValue(plan.VerificationTool)}.";

        if (existingArtifact is null)
            return _ramDbService.SaveArtifact(workspaceRoot, artifact);

        _ramDbService.UpdateArtifact(workspaceRoot, artifact);
        return artifact;
    }

    private (bool Success, string Message, RepairPlanInput? Input, RepairProposalRecord? Proposal, ArtifactRecord? Artifact)
        BuildRepairProposalContext(string workspaceRoot, ToolRequest request)
    {
        var scope = GetOptionalArgument(request, "scope");
        var explicitPath = GetOptionalArgument(request, "path");
        var input = _repairPlanInputBuilder.Build(
            workspaceRoot,
            _ramDbService,
            scope,
            explicitPath,
            request.TaskboardRunStateId ?? "");

        if (!input.Success)
            return (false, input.Message, input, null, null);

        input.BaselineSolutionPath = GetOptionalArgument(request, "baseline_solution_path");
        input.BaselineAllowedRoots = SplitPipeList(GetOptionalArgument(request, "baseline_allowed_roots"));
        input.BaselineExcludedRoots = SplitPipeList(GetOptionalArgument(request, "baseline_excluded_roots"));
        input.MaintenanceBaselineSummary = BuildMaintenanceBaselineSummary(input);

        if (!_artifactClassificationService.IsRepairEligibleFailureKind(input.FailureKind)
            && string.IsNullOrWhiteSpace(input.SourceArtifactType))
        {
            var message =
                "plan_repair failed: no recorded build or test failure is available for this workspace."
                + Environment.NewLine
                + "Run dotnet build or dotnet test first, or select a failure-related artifact.";
            return (false, message, input, null, null);
        }

        var retrievalPreparation = _ramRetrievalService
            .PrepareRepairContextAsync(workspaceRoot, input, request, _settingsService.Load())
            .GetAwaiter()
            .GetResult();
        if (retrievalPreparation.Success && retrievalPreparation.ContextPacket is not null)
        {
            input.RetrievalBackend = retrievalPreparation.ContextPacket.BackendType;
            input.RetrievalEmbedderModel = retrievalPreparation.ContextPacket.EmbedderModel;
            input.RetrievalQueryKind = retrievalPreparation.ContextPacket.QueryKind;
            input.RetrievalHitCount = retrievalPreparation.ContextPacket.HitCount;
            input.RetrievalSourceKinds = [.. retrievalPreparation.ContextPacket.SourceKinds];
            input.RetrievalSourcePaths = [.. retrievalPreparation.ContextPacket.SourcePaths];
            input.RetrievalQueryArtifactRelativePath = retrievalPreparation.QueryArtifact?.RelativePath ?? "";
            input.RetrievalResultArtifactRelativePath = retrievalPreparation.RetrievalResultArtifact?.RelativePath ?? "";
            input.RetrievalContextPacketArtifactRelativePath = retrievalPreparation.ContextPacketArtifact?.RelativePath ?? "";
            input.RetrievalIndexBatchArtifactRelativePath = retrievalPreparation.IndexBatchArtifact?.RelativePath ?? "";
            input.RetrievalContextText = retrievalPreparation.ContextPacket.ContextText;
        }

        var proposal = _localRepairPlanningService.Plan(workspaceRoot, input);
        if (!_artifactClassificationService.IsRepairEligibleFailureKind(proposal.FailureKind)
            && !_artifactClassificationService.IsRepairLoopArtifactType(proposal.SourceArtifactType))
        {
            var message =
                "plan_repair failed: RAM could not trace this request to a recorded build or test failure."
                + Environment.NewLine
                + "Run dotnet build or dotnet test first, or select a failure-related artifact.";
            return (false, message, input, null, null);
        }

        if (!string.IsNullOrWhiteSpace(proposal.TargetFilePath))
            request.Arguments["path"] = proposal.TargetFilePath;

        var artifact = SaveRepairProposalArtifact(workspaceRoot, proposal);
        var messageSuffix = retrievalPreparation.Success && retrievalPreparation.ContextPacket is not null
            ? $"{Environment.NewLine}Retrieval: {retrievalPreparation.ContextPacket.RetrievalSummary}"
            : !string.IsNullOrWhiteSpace(retrievalPreparation.Message)
                ? $"{Environment.NewLine}Retrieval: {retrievalPreparation.Message}"
                : "";
        return (true, input.Message + messageSuffix, input, proposal, artifact);
    }

    private string ResolvePreferredPatchPath(string workspaceRoot, ToolRequest request)
    {
        if (request.TryGetArgument("path", out var explicitPath))
            return NormalizeWorkspaceRelativePath(workspaceRoot, explicitPath);

        return "";
    }

    private ArtifactPayload<RepairProposalRecord>? TrySelectRepairProposalArtifact(
        IReadOnlyList<ArtifactRecord> recentArtifacts,
        string preferredPath,
        out string message)
    {
        var proposals = LoadRecentArtifactPayloads<RepairProposalRecord>(
            recentArtifacts.Where(_repairEligibilityService.IsValidRepairProposalArtifact).ToList(),
            "repair_proposal",
            static proposal => proposal.TargetFilePath);
        return TrySelectArtifactPayload(
            proposals,
            preferredPath,
            "repair proposals",
            out message);
    }

    private ArtifactPayload<PatchDraftRecord>? TrySelectPatchDraftArtifact(
        IReadOnlyList<ArtifactRecord> recentArtifacts,
        string preferredPath,
        out string message)
    {
        var drafts = LoadRecentArtifactPayloads<PatchDraftRecord>(
            recentArtifacts.Where(_repairEligibilityService.IsValidPatchDraftArtifact).ToList(),
            "patch_draft",
            static draft => draft.TargetFilePath);
        return TrySelectArtifactPayload(
            drafts,
            preferredPath,
            "patch drafts",
            out message);
    }

    private ArtifactPayload<PatchApplyResultRecord>? TrySelectPatchApplyArtifact(
        IReadOnlyList<ArtifactRecord> recentArtifacts,
        string preferredPath,
        out string message)
    {
        var applies = LoadRecentArtifactPayloads<PatchApplyResultRecord>(
            recentArtifacts.Where(_repairEligibilityService.IsValidPatchApplyArtifact).ToList(),
            "patch_apply_result",
            static apply => apply.Draft.TargetFilePath);
        return TrySelectArtifactPayload(
            applies,
            preferredPath,
            "patch apply results",
            out message);
    }

    private static List<ArtifactPayload<T>> LoadRecentArtifactPayloads<T>(
        IReadOnlyList<ArtifactRecord> recentArtifacts,
        string artifactType,
        Func<T, string> targetSelector)
    {
        var results = new List<ArtifactPayload<T>>();
        foreach (var artifact in recentArtifacts)
        {
            if (!string.Equals(artifact.ArtifactType, artifactType, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(artifact.Content))
            {
                continue;
            }

            try
            {
                var payload = JsonSerializer.Deserialize<T>(artifact.Content);
                if (payload is null)
                    continue;

                results.Add(new ArtifactPayload<T>(
                    artifact,
                    payload,
                    NormalizeRelativePath(targetSelector(payload) ?? "")));
            }
            catch
            {
                // Ignore malformed artifact rows and continue with the next candidate.
            }
        }

        return results;
    }

    private static ArtifactPayload<T>? TrySelectArtifactPayload<T>(
        IReadOnlyList<ArtifactPayload<T>> payloads,
        string preferredPath,
        string label,
        out string message)
    {
        if (payloads.Count == 0)
        {
            message = $"No {label} are stored for this workspace.";
            return null;
        }

        var normalizedPreferredPath = NormalizeRelativePath(preferredPath);
        if (!string.IsNullOrWhiteSpace(normalizedPreferredPath))
        {
            var matching = payloads
                .Where(payload => string.Equals(payload.TargetPath, normalizedPreferredPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matching.Count == 0)
            {
                message = BuildArtifactSelectionMessage(
                    label,
                    $"No {label[..^1]} matched {normalizedPreferredPath}.",
                    payloads);
                return null;
            }

            message = "";
            return matching[0];
        }

        var distinctTargets = payloads
            .Select(payload => payload.TargetPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctTargets.Count > 1)
        {
            message = BuildArtifactSelectionMessage(
                label,
                $"Multiple {label} are available and RAM does not have a single current target.",
                payloads);
            return null;
        }

        message = "";
        return payloads[0];
    }

    private static string BuildArtifactSelectionMessage<T>(
        string label,
        string summary,
        IReadOnlyList<ArtifactPayload<T>> payloads)
    {
        var lines = new List<string>
        {
            summary,
            $"Available {label}:"
        };

        foreach (var payload in payloads.Take(5))
        {
            lines.Add($"- {DisplayValue(payload.TargetPath)} [{payload.Artifact.Title}] ({payload.Artifact.UpdatedUtc})");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildPatchDraftResolutionMessage(string proposalMessage, string draftMessage)
    {
        if (!string.IsNullOrWhiteSpace(proposalMessage) && !string.IsNullOrWhiteSpace(draftMessage))
            return proposalMessage + Environment.NewLine + draftMessage;

        return FirstNonEmpty(proposalMessage, draftMessage, "No repair proposal or patch draft is available for this workspace.");
    }

    private static bool ShouldBuildFreshRepairProposal(string preferredPath, string proposalSelectionMessage)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
            return true;

        return proposalSelectionMessage.StartsWith("No repair proposal", StringComparison.OrdinalIgnoreCase)
            || proposalSelectionMessage.StartsWith("No repair proposals are stored", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldFallbackToExistingPatchDraft(string preferredPath, string proposalSelectionMessage)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
            return true;

        return proposalSelectionMessage.StartsWith("No repair proposal", StringComparison.OrdinalIgnoreCase)
            || proposalSelectionMessage.StartsWith("No repair proposals are stored", StringComparison.OrdinalIgnoreCase);
    }

    private static ArtifactRecord? BuildActiveArtifactFromRequest(ToolRequest request)
    {
        if (!request.TryGetArgument("active_artifact_path", out var relativePath)
            || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        return new ArtifactRecord
        {
            RelativePath = relativePath,
            ArtifactType = request.TryGetArgument("active_artifact_type", out var artifactType)
                ? artifactType
                : ""
        };
    }

    private void RecordExecutionFailure(
        string workspaceRoot,
        string toolName,
        string outcomeType,
        string targetPath,
        string summary,
        string dataJson)
    {
        _ramDbService.SaveExecutionFailure(
            workspaceRoot,
            toolName,
            outcomeType,
            NormalizeRelativePath(targetPath ?? ""),
            summary,
            dataJson);
    }

    private void RecordExecutionSuccess(
        string workspaceRoot,
        string toolName,
        string targetPath,
        string summary,
        string dataJson)
    {
        _ramDbService.SaveExecutionSuccess(
            workspaceRoot,
            toolName,
            "success",
            NormalizeRelativePath(targetPath ?? ""),
            summary,
            dataJson);
    }

    private string RequireWorkspace()
    {
        if (!_workspaceService.HasWorkspace())
            throw new InvalidOperationException("No workspace set. Tool execution requires an active workspace.");

        return _workspaceService.WorkspaceRoot;
    }

    private ToolResult? TryBuildMaintenanceBaselineGuardFailure(ToolRequest request, string toolName)
    {
        if (!_workspaceService.HasWorkspace())
            return null;

        if (!string.Equals(request.ExecutionPolicyMode, "taskboard_auto_run", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.TaskboardPlanTitle))
        {
            return null;
        }

        var workspaceRoot = RequireWorkspace();
        var requestBaseline = _taskboardMaintenanceBaselineService.ResolveFromRequestContext(workspaceRoot, request);
        if (requestBaseline.IsMaintenanceMode)
        {
            var requestGuard = _taskboardMaintenanceBaselineService.EvaluateMutationGuard(workspaceRoot, requestBaseline, request);
            _taskboardMaintenanceBaselineService.StampBaselineContext(request, requestBaseline);
            if (!requestGuard.Applies || requestGuard.Allowed)
                return null;

            return Failure(
                toolName,
                requestGuard.Summary,
                "maintenance_baseline_guard",
                requestGuard.Summary,
                SerializeJson(new
                {
                    reason_code = requestGuard.ReasonCode,
                    target_path = requestGuard.TargetPath,
                    baseline_solution_path = requestGuard.BaselineSolutionPath,
                    declared_roots = requestGuard.DeclaredRoots,
                    allowed_roots = requestGuard.AllowedRoots,
                    discovered_roots = requestGuard.DiscoveredRoots,
                    compatible_storage_roots = requestGuard.CompatibleStorageRoots,
                    excluded_roots = requestGuard.ExcludedRoots,
                    storage_resolution_kind = requestGuard.StorageResolutionKind,
                    storage_resolution_summary = requestGuard.StorageResolutionSummary
                }));
        }

        var rawArtifact = string.IsNullOrWhiteSpace(request.TaskboardPlanTitle)
            ? null
            : _ramDbService.LoadLatestArtifactByTypeAndIntentTitle(workspaceRoot, "taskboard_raw", request.TaskboardPlanTitle);
        rawArtifact ??= _ramDbService.LoadLatestArtifactByType(workspaceRoot, "taskboard_raw");
        if (rawArtifact is null || string.IsNullOrWhiteSpace(rawArtifact.Content))
            return null;

        var executionState = _ramDbService.LoadExecutionState(workspaceRoot);
        var baseline = _taskboardMaintenanceBaselineService.ResolveFromRawText(
            workspaceRoot,
            FirstNonEmpty(request.TaskboardPlanTitle, rawArtifact.IntentTitle, rawArtifact.Title),
            rawArtifact.Content,
            executionState);
        if (!baseline.IsMaintenanceMode)
            return null;

        var guard = _taskboardMaintenanceBaselineService.EvaluateMutationGuard(workspaceRoot, baseline, request);
        _taskboardMaintenanceBaselineService.StampBaselineContext(request, baseline);
        if (!guard.Applies || guard.Allowed)
            return null;

        return Failure(
            toolName,
            guard.Summary,
            "maintenance_baseline_guard",
            guard.Summary,
            SerializeJson(new
            {
                reason_code = guard.ReasonCode,
                target_path = guard.TargetPath,
                baseline_solution_path = guard.BaselineSolutionPath,
                declared_roots = guard.DeclaredRoots,
                allowed_roots = guard.AllowedRoots,
                discovered_roots = guard.DiscoveredRoots,
                compatible_storage_roots = guard.CompatibleStorageRoots,
                excluded_roots = guard.ExcludedRoots,
                storage_resolution_kind = guard.StorageResolutionKind,
                storage_resolution_summary = guard.StorageResolutionSummary
            }));
    }

    private string ResolveWorkspacePath(string path)
    {
        var workspaceRoot = RequireWorkspace();
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(workspaceRoot, path));

        if (!_workspaceService.IsInsideWorkspace(fullPath))
            throw new InvalidOperationException("Tool path must stay inside the active workspace.");

        return fullPath;
    }

    private static string GetRequiredArgument(ToolRequest request, string key)
    {
        if (request.TryGetArgument(key, out var value))
            return value;

        throw new ArgumentException($"Tool argument is required: {key}", nameof(request));
    }

    private static string GetOptionalArgument(ToolRequest request, string key)
    {
        return request.TryGetArgument(key, out var value) ? value : "";
    }

    private static int GetIntArgument(ToolRequest request, string key, int defaultValue, int minValue, int maxValue)
    {
        if (!request.TryGetArgument(key, out var value))
            return defaultValue;

        if (!int.TryParse(value, out var parsed))
            throw new ArgumentException($"Tool argument must be an integer: {key}", nameof(request));

        return Math.Clamp(parsed, minValue, maxValue);
    }

    private static string NormalizeToolName(string toolName)
    {
        return (toolName ?? "").Trim().ToLowerInvariant();
    }

    private static ToolResult Success(
        string toolName,
        string output,
        string summary = "",
        string outcomeType = "success",
        string structuredDataJson = "")
    {
        return new ToolResult
        {
            ToolName = toolName,
            Success = true,
            OutcomeType = outcomeType,
            Summary = string.IsNullOrWhiteSpace(summary) ? BuildDefaultSummary(output) : summary,
            StructuredDataJson = structuredDataJson ?? "",
            Output = output ?? "",
            ErrorMessage = ""
        };
    }

    private static ToolResult Failure(
        string toolName,
        string message,
        string outcomeType = "execution_failure",
        string summary = "",
        string structuredDataJson = "")
    {
        return new ToolResult
        {
            ToolName = toolName,
            Success = false,
            OutcomeType = outcomeType,
            Summary = string.IsNullOrWhiteSpace(summary) ? BuildDefaultSummary(message) : summary,
            StructuredDataJson = structuredDataJson ?? "",
            Output = "",
            ErrorMessage = message ?? "Tool execution failed."
        };
    }

    private static ToolResult FromCommandResult(string toolName, string details, CommandExecutionResult result)
    {
        var outcomeType = string.IsNullOrWhiteSpace(result.SafetyOutcomeType)
            ? "execution_failure"
            : result.SafetyOutcomeType;

        return result.ExitCode == 0 && !result.TimedOut && string.IsNullOrWhiteSpace(result.SafetyOutcomeType)
            ? Success(toolName, details, $"{toolName} completed successfully.", "success")
            : Failure(toolName, details, outcomeType, BuildExecutionFailureSummary(toolName, result));
    }

    private static string BuildArtifactSummary(string content)
    {
        const int maxLength = 160;

        var normalized = (content ?? "")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        if (string.IsNullOrWhiteSpace(normalized))
            return "(empty)";

        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..maxLength].TrimEnd() + "...";
    }

    private static string DetectArtifactType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        return extension switch
        {
            ".cs" or ".xaml" or ".xml" or ".json" or ".js" or ".ts" or ".css" or ".html" => "code",
            ".txt" or ".md" or ".log" => "text",
            _ => "output"
        };
    }

    private static string NormalizeRelativePath(string path)
    {
        return (path ?? "").Replace('\\', '/');
    }

    private string NormalizeWorkspaceRelativePath(string workspaceRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        return NormalizeRelativePath(Path.GetRelativePath(workspaceRoot, ResolveWorkspacePath(path)));
    }

    private string BuildDotnetBuildSuccessOutput(
        string discoveryMessage,
        string targetPath,
        DotnetBuildParseResult parsed,
        CommandExecutionResult result)
    {
        return string.Join(
            Environment.NewLine,
            discoveryMessage,
            $"Resolved build target: {targetPath}",
            $"Summary: {parsed.Summary}",
            _commandExecutionService.FormatCompactResult("dotnet build", result));
    }

    private string BuildDotnetBuildFailureOutput(
        string discoveryMessage,
        string targetPath,
        DotnetBuildParseResult parsed,
        CommandExecutionResult result,
        string summary)
    {
        var lines = new List<string>
        {
            "dotnet_build failed:",
            discoveryMessage,
            $"Resolved build target: {targetPath}",
            $"Summary: {summary}"
        };

        if (parsed.TopErrors.Count > 0)
        {
            lines.Add("Top errors:");
            foreach (var error in parsed.TopErrors)
            {
                var location = string.IsNullOrWhiteSpace(error.FilePath)
                    ? DisplayValue(error.RawPath)
                    : error.FilePath;
                var columnSuffix = error.ColumnNumber > 0 ? $",{error.ColumnNumber}" : "";
                lines.Add($"- {location}({error.LineNumber}{columnSuffix}) {error.Code}: {error.Message}");
            }
        }

        lines.Add(_commandExecutionService.FormatCompactResult("dotnet build", result));
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildDotnetTestSuccessOutput(
        string resolutionMessage,
        string targetPath,
        DotnetTestParseResult parsed,
        CommandExecutionResult result)
    {
        return string.Join(
            Environment.NewLine,
            resolutionMessage,
            $"Resolved test target: {targetPath}",
            $"Summary: {parsed.Summary}",
            _commandExecutionService.FormatCompactResult("dotnet test", result));
    }

    private string BuildDotnetTestFailureOutput(
        string resolutionMessage,
        string targetPath,
        DotnetTestParseResult parsed,
        CommandExecutionResult result,
        string summary)
    {
        var lines = new List<string>
        {
            "dotnet_test failed:",
            resolutionMessage,
            $"Resolved test target: {targetPath}",
            $"Summary: {summary}"
        };

        if (parsed.FailingTests.Count > 0)
        {
            lines.Add("Failing tests:");
            foreach (var failure in parsed.FailingTests.Take(5))
            {
                lines.Add($"- {failure.TestName}");
                if (!string.IsNullOrWhiteSpace(failure.ResolvedSourcePath))
                    lines.Add($"  File: {failure.ResolvedSourcePath}:{failure.SourceLine}");
                else if (failure.CandidatePaths.Count > 1)
                    lines.Add($"  File candidates: {string.Join(", ", failure.CandidatePaths.Take(3))}");
                if (!string.IsNullOrWhiteSpace(failure.Message))
                    lines.Add($"  Message: {failure.Message}");
                if (!string.IsNullOrWhiteSpace(failure.StackTraceExcerpt))
                    lines.Add($"  Stack: {failure.StackTraceExcerpt}");
            }
        }

        lines.Add(_commandExecutionService.FormatCompactResult("dotnet test", result));
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildFailureDataJson(
        string toolName,
        string outcomeType,
        string targetPath,
        string summary,
        object parsedResult,
        CommandExecutionResult commandResult)
    {
        return SerializeJson(new
        {
            tool_name = toolName,
            outcome_type = outcomeType,
            target_path = NormalizeRelativePath(targetPath),
            summary,
            captured_utc = DateTime.UtcNow.ToString("O"),
            parsed = parsedResult,
            command = new
            {
                display_command = commandResult.DisplayCommand,
                working_directory = commandResult.WorkingDirectory,
                exit_code = commandResult.ExitCode,
                execution_attempted = commandResult.ExecutionAttempted,
                execution_source = commandResult.ExecutionSourceSummary,
                gate_decision_id = commandResult.GateDecisionId,
                gate_decision_summary = commandResult.GateDecisionSummary,
                timeout_seconds = commandResult.TimeoutSeconds,
                timed_out = commandResult.TimedOut,
                killed_process_tree = commandResult.KilledProcessTree,
                safety_outcome_type = commandResult.SafetyOutcomeType,
                safety_message = commandResult.SafetyMessage,
                safety_profile = commandResult.SafetyProfileSummary,
                stdout_excerpt = BoundText(commandResult.StandardOutput, 3000),
                stderr_excerpt = BoundText(commandResult.StandardError, 2000),
                truncated = commandResult.OutputWasTruncated
            }
        });
    }

    private static string BuildSimpleDataJson(string toolName, string outcomeType, string targetPath, string summary)
    {
        return SerializeJson(new
        {
            tool_name = toolName,
            outcome_type = outcomeType,
            target_path = NormalizeRelativePath(targetPath),
            summary,
            captured_utc = DateTime.UtcNow.ToString("O")
        });
    }

    private static string BuildWorkspaceResolutionDataJson(
        string toolName,
        string outcomeType,
        string targetPath,
        WorkspaceBuildResolution resolution)
    {
        return SerializeJson(new
        {
            tool_name = toolName,
            outcome_type = outcomeType,
            target_path = NormalizeRelativePath(targetPath),
            summary = resolution.Message,
            captured_utc = DateTime.UtcNow.ToString("O"),
            resolution = new
            {
                requested_target = resolution.RequestedTarget,
                normalized_target = resolution.NormalizedTarget,
                failure_kind = resolution.FailureKind,
                reason_code = resolution.ReasonCode,
                prerequisite_required = resolution.PrerequisiteRequired,
                discovered_alternatives = resolution.Candidates
                    .Select(candidate => candidate.RelativePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList()
            }
        });
    }

    private string BuildProjectAttachBuilderDataJson(
        ToolRequest request,
        string toolName,
        string targetPath,
        bool validationPassed,
        string summary,
        CommandExecutionResult commandResult)
    {
        var outcomeType = string.IsNullOrWhiteSpace(commandResult.SafetyOutcomeType)
            ? commandResult.ExitCode == 0 && !commandResult.TimedOut
                ? validationPassed ? "success" : "validation_failed"
                : commandResult.TimedOut
                    ? "timed_out"
                    : "execution_failure"
            : commandResult.SafetyOutcomeType;
        return BuildFailureDataJson(
            toolName,
            outcomeType,
            targetPath,
            summary,
            new
            {
                target_path = NormalizeRelativePath(targetPath),
                validation_passed = validationPassed,
                project_attach = BuildProjectAttachPayload(request)
            },
            commandResult);
    }

    private static string BuildProjectAttachSimpleDataJson(
        ToolRequest request,
        string toolName,
        string outcomeType,
        string targetPath,
        string summary)
    {
        return SerializeJson(new
        {
            tool_name = toolName,
            outcome_type = outcomeType,
            target_path = NormalizeRelativePath(targetPath),
            summary,
            captured_utc = DateTime.UtcNow.ToString("O"),
            project_attach = BuildProjectAttachPayload(request)
        });
    }

    private static object? BuildProjectAttachPayload(ToolRequest request)
    {
        if (request is null)
            return null;

        var targetProjectPath = GetOptionalArgument(request, "project_attach_target_project");
        var solutionPath = GetOptionalArgument(request, "project_attach_solution_path");
        var continuationStatus = GetOptionalArgument(request, "project_attach_continuation_status");
        var insertedStep = GetOptionalArgument(request, "project_attach_inserted_step");
        var continuationSummary = GetOptionalArgument(request, "project_attach_continuation_summary");
        var projectExistedRaw = GetOptionalArgument(request, "project_attach_project_existed_at_decision");
        if (string.IsNullOrWhiteSpace(targetProjectPath)
            && string.IsNullOrWhiteSpace(solutionPath)
            && string.IsNullOrWhiteSpace(continuationStatus)
            && string.IsNullOrWhiteSpace(insertedStep)
            && string.IsNullOrWhiteSpace(continuationSummary)
            && string.IsNullOrWhiteSpace(projectExistedRaw))
        {
            return null;
        }

        return new
        {
            solution_path = NormalizeRelativePath(solutionPath),
            target_project_path = NormalizeRelativePath(targetProjectPath),
            scaffold_surface_version = DotnetScaffoldSurfaceService.MatrixVersion,
            scaffold_surface_status = new DotnetScaffoldSurfaceService().ResolveSupportStatus(GetOptionalArgument(request, "template")),
            template = GetOptionalArgument(request, "template"),
            declared_name = FirstNonEmpty(GetOptionalArgument(request, "name"), GetOptionalArgument(request, "project_name")),
            declared_path = FirstNonEmpty(GetOptionalArgument(request, "declared_path"), GetOptionalArgument(request, "path"), NormalizeRelativePath(targetProjectPath)),
            declared_solution = FirstNonEmpty(GetOptionalArgument(request, "solution"), NormalizeRelativePath(solutionPath)),
            declared_role = GetOptionalArgument(request, "role"),
            target_framework = GetOptionalArgument(request, "target_framework"),
            template_switches = GetOptionalArgument(request, "template_switches"),
            attach = GetOptionalArgument(request, "attach"),
            reference_from = GetOptionalArgument(request, "reference_from"),
            reference_to = GetOptionalArgument(request, "reference_to"),
            continuation_status = continuationStatus,
            inserted_step = insertedStep,
            continuation_summary = continuationSummary,
            project_existed_at_decision = string.Equals(projectExistedRaw, "true", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string BuildGenerationGuardrailStructuredDataJson(
        string toolName,
        string outcomeType,
        string targetPath,
        string label,
        CSharpGenerationGuardrailEvaluationRecord evaluation,
        ArtifactRecord? fileArtifact,
        string mutationKind,
        BehaviorDepthEvidenceRecord? behaviorDepthEvidence = null,
        ArtifactRecord? behaviorDepthArtifact = null,
        IReadOnlyList<ArtifactRecord>? companionArtifacts = null,
        CSharpPatchWorkContractRecord? modificationContract = null,
        ArtifactRecord? modificationContractArtifact = null,
        CSharpPatchPlanRecord? modificationPlan = null,
        ArtifactRecord? modificationPlanArtifact = null)
    {
        return SerializeJson(new
        {
            tool_name = toolName,
            outcome_type = outcomeType,
            target_path = NormalizeRelativePath(targetPath),
            label,
            mutation_kind = mutationKind,
            captured_utc = DateTime.UtcNow.ToString("O"),
            generation_guardrail = evaluation,
            behavior_depth = behaviorDepthEvidence,
            behavior_depth_artifact = behaviorDepthArtifact is null
                ? null
                : new
                {
                    behaviorDepthArtifact.RelativePath,
                    behaviorDepthArtifact.ArtifactType,
                    behaviorDepthArtifact.Title,
                    behaviorDepthArtifact.Summary
                },
            file_artifact = fileArtifact is null
                ? null
                : new
                {
                    fileArtifact.RelativePath,
                    fileArtifact.ArtifactType,
                    fileArtifact.Title,
                    fileArtifact.Summary
                },
            companion_artifacts = companionArtifacts is null || companionArtifacts.Count == 0
                ? null
                : companionArtifacts.Select(artifact => new
                {
                    artifact.RelativePath,
                    artifact.ArtifactType,
                    artifact.Title,
                    artifact.Summary
                }).ToList(),
            csharp_patch_contract = modificationContract,
            csharp_patch_contract_artifact = modificationContractArtifact is null
                ? null
                : new
                {
                    modificationContractArtifact.RelativePath,
                    modificationContractArtifact.ArtifactType,
                    modificationContractArtifact.Title,
                    modificationContractArtifact.Summary
                },
            csharp_patch_plan = modificationPlan,
            csharp_patch_plan_artifact = modificationPlanArtifact is null
                ? null
                : new
                {
                    modificationPlanArtifact.RelativePath,
                    modificationPlanArtifact.ArtifactType,
                    modificationPlanArtifact.Title,
                    modificationPlanArtifact.Summary
                }
        });
    }

    private ArtifactRecord SaveBehaviorDepthArtifact(string workspaceRoot, BehaviorDepthEvidenceRecord evidence)
    {
        var relativePath = $".ram/behavior-depth/{BuildArtifactSlug(evidence.TargetPath)}.json";
        var summary = $"profile={DisplayValue(evidence.Profile)} tier={DisplayValue(evidence.BehaviorDepthTier)} recommendation={DisplayValue(evidence.CompletionRecommendation)} follow_up={DisplayValue(evidence.FollowUpRecommendation)}";
        return SaveCustomArtifact(
            workspaceRoot,
            "behavior_depth_evidence",
            $"Behavior depth: {DisplayValue(evidence.TargetPath)}",
            relativePath,
            SerializeJson(evidence),
            summary);
    }

    private static string DetermineCommandOutcome(string toolName, CommandExecutionResult result, bool hasParsedFailure)
    {
        if (!string.IsNullOrWhiteSpace(result.SafetyOutcomeType))
            return result.SafetyOutcomeType;

        if (result.ExitCode == 0 && !result.TimedOut)
            return "success";

        if (hasParsedFailure)
            return toolName == "dotnet_test" ? "test_failure" : "build_failure";

        if (result.TimedOut)
            return "timed_out";

        return "execution_failure";
    }

    private static string BuildExecutionFailureSummary(string toolName, CommandExecutionResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.SafetyMessage))
            return result.SafetyMessage;

        return result.TimedOut
            ? $"{toolName} timed out after {result.TimeoutSeconds} second(s)."
            : $"{toolName} failed with exit code {result.ExitCode}.";
    }

    private ToolResult BuildBuilderCommandResult(
        string toolName,
        string workspaceRoot,
        string targetPath,
        bool validationPassed,
        string validationSummary,
        string details,
        CommandExecutionResult result,
        string? structuredDataJsonOverride = null)
    {
        var outcomeType = string.IsNullOrWhiteSpace(result.SafetyOutcomeType)
            ? result.ExitCode == 0 && !result.TimedOut
                ? validationPassed ? "success" : "validation_failed"
                : result.TimedOut
                    ? "timed_out"
                    : "execution_failure"
            : result.SafetyOutcomeType;
        var structuredDataJson = string.IsNullOrWhiteSpace(structuredDataJsonOverride)
            ? BuildFailureDataJson(
                toolName,
                outcomeType,
                targetPath,
                validationSummary,
                new
                {
                    target_path = NormalizeRelativePath(targetPath),
                    validation_passed = validationPassed
                },
                result)
            : structuredDataJsonOverride;

        if (outcomeType == "success")
        {
            RecordExecutionSuccess(workspaceRoot, toolName, targetPath, validationSummary, structuredDataJson);
            return Success(toolName, details, validationSummary, "success", structuredDataJson);
        }

        var failureOutput = string.Join(
            Environment.NewLine,
            $"{toolName} failed:",
            validationSummary,
            details);
        RecordExecutionFailure(workspaceRoot, toolName, outcomeType, targetPath, validationSummary, structuredDataJson);
        return Failure(toolName, failureOutput, outcomeType, validationSummary, structuredDataJson);
    }

    private static string BuildProjectReferenceDecisionJson(
        DotnetProjectReferenceDecision decision,
        string toolName,
        string outcomeType,
        string summary)
    {
        return SerializeJson(new
        {
            tool_name = toolName,
            outcome_type = outcomeType,
            summary,
            attempted_project_path = NormalizeRelativePath(decision.AttemptedProjectPath),
            attempted_reference_path = NormalizeRelativePath(decision.AttemptedReferencePath),
            effective_project_path = NormalizeRelativePath(decision.EffectiveProjectPath),
            effective_reference_path = NormalizeRelativePath(decision.EffectiveReferencePath),
            direction_rule = decision.DirectionRuleId,
            direction_rule_summary = decision.DirectionRuleSummary,
            attempted_frameworks = decision.AttemptedFrameworkSummary,
            effective_frameworks = decision.EffectiveFrameworkSummary,
            compatibility = decision.CompatibilityKind.ToString().ToLowerInvariant(),
            compatibility_summary = decision.CompatibilitySummary,
            decision_code = decision.DecisionCode,
            decision_kind = decision.DecisionKind.ToString().ToLowerInvariant(),
            captured_utc = DateTime.UtcNow.ToString("O")
        });
    }

    private static (bool Success, string Summary) ValidateCreatedFile(string relativeTargetPath, string fullTargetPath, string kind)
    {
        return File.Exists(fullTargetPath)
            ? (true, $"{kind} created: {NormalizeRelativePath(relativeTargetPath)}.")
            : (false, $"{kind} creation validation failed: expected {NormalizeRelativePath(relativeTargetPath)} was not created.");
    }

    private static string BuildSolutionProjectReference(string fullSolutionPath, string fullProjectPath, string workspaceRoot)
    {
        var solutionDirectory = Path.GetDirectoryName(fullSolutionPath) ?? workspaceRoot;
        var relativePath = Path.GetRelativePath(solutionDirectory, fullProjectPath)
            .Replace('/', '\\');
        return relativePath;
    }

    private static bool SolutionContainsReference(string fullSolutionPath, string expectedReference)
    {
        if (!File.Exists(fullSolutionPath))
            return false;

        var content = File.ReadAllText(fullSolutionPath);
        return content.Contains(expectedReference, StringComparison.OrdinalIgnoreCase);
    }

    private static (bool Success, string Summary) ValidateSolutionReference(string fullSolutionPath, string expectedReference, string projectPath)
    {
        return SolutionContainsReference(fullSolutionPath, expectedReference)
            ? (true, $"Solution updated: added {NormalizeRelativePath(projectPath)}.")
            : (false, $"Solution update validation failed: expected project reference `{NormalizeRelativePath(projectPath)}` was not found in the solution file.");
    }

    private static string BuildProjectReferenceInclude(string fullProjectPath, string fullReferencePath, string workspaceRoot)
    {
        var projectDirectory = Path.GetDirectoryName(fullProjectPath) ?? workspaceRoot;
        return NormalizeRelativePath(Path.GetRelativePath(projectDirectory, fullReferencePath));
    }

    private static bool ProjectContainsReference(string fullProjectPath, string expectedReference)
    {
        if (!File.Exists(fullProjectPath))
            return false;

        var content = File.ReadAllText(fullProjectPath).Replace('\\', '/');
        return content.Contains(expectedReference, StringComparison.OrdinalIgnoreCase);
    }

    private static (bool Success, string Summary) ValidateProjectReference(string fullProjectPath, string fullReferencePath, string expectedReference, string referencePath)
    {
        if (PathsResolveToSameTarget(fullProjectPath, fullReferencePath))
            return (false, $"Project reference validation failed: `{NormalizeRelativePath(referencePath)}` resolves to the same project and is blocked.");

        return ProjectContainsReference(fullProjectPath, expectedReference)
            ? (true, $"Project reference added: {NormalizeRelativePath(referencePath)}.")
            : (false, $"Project reference validation failed: expected reference `{NormalizeRelativePath(referencePath)}` was not found in the project file.");
    }

    private static bool PathsResolveToSameTarget(string? leftPath, string? rightPath)
    {
        if (string.IsNullOrWhiteSpace(leftPath) || string.IsNullOrWhiteSpace(rightPath))
            return false;

        return string.Equals(
            ResolveNormalizedFullPath(leftPath),
            ResolveNormalizedFullPath(rightPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveNormalizedFullPath(string path)
    {
        var fullPath = Path.GetFullPath(path ?? "");
        return fullPath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
    }

    private void EnsureExecutionMetadata(ToolRequest request)
    {
        if (request.ExecutionSourceType == ExecutionSourceType.Unknown)
            request.ExecutionSourceType = InferExecutionSourceType(request.ToolName);

        if (string.IsNullOrWhiteSpace(request.ExecutionSourceName))
            request.ExecutionSourceName = request.ToolName;

        if (string.IsNullOrWhiteSpace(request.ExecutionPolicyMode) && !request.IsAutomaticTrigger)
            request.ExecutionPolicyMode = "explicit_manual";

        if (string.IsNullOrWhiteSpace(request.ExecutionBuildFamily))
            request.ExecutionBuildFamily = DefaultBuildFamilyForTool(request.ToolName);
    }

    private static ExecutionSourceType InferExecutionSourceType(string toolName)
    {
        if (string.Equals(toolName, "run_command", StringComparison.OrdinalIgnoreCase))
            return ExecutionSourceType.ManualUserRequest;

        return ExternalExecutionToolNames.Contains(toolName)
            ? ExecutionSourceType.BuildTool
            : ExecutionSourceType.ManualUserRequest;
    }

    private static string DefaultBuildFamilyForTool(string toolName)
    {
        return NormalizeToolName(toolName) switch
        {
            "create_dotnet_solution" or "create_dotnet_project" or "add_project_to_solution" or "add_dotnet_project_reference" => "dotnet",
            "create_dotnet_page_view" or "create_dotnet_viewmodel" or "register_navigation" or "register_di_service" or "initialize_sqlite_storage_boundary" => "dotnet",
            "dotnet_build" or "dotnet_test" => "dotnet",
            "create_cmake_project" or "create_cpp_source_file" or "create_cpp_header_file" or "create_c_source_file" or "create_c_header_file" => "cmake",
            "cmake_configure" or "cmake_build" or "ctest_run" => "cmake",
            "make_build" => "make",
            "ninja_build" => "ninja",
            "run_build_script" => "script",
            "git_status" or "git_diff" => "git",
            _ => ""
        };
    }

    private ExecutionGateDecision BuildExecutionGateDecision(
        ToolRequest request,
        string commandFamily,
        string buildFamily,
        bool executionAllowed,
        string scopeRiskClassification,
        string targetPath)
    {
        EnsureExecutionMetadata(request);

        var gateRequest = new ExecutionGateRequest
        {
            SourceType = request.ExecutionSourceType,
            SourceName = FirstNonEmpty(request.ExecutionSourceName, request.ToolName),
            CommandFamily = NormalizeCommandFamily(commandFamily),
            BuildFamily = FirstNonEmpty(NormalizeBuildFamily(buildFamily), request.ExecutionBuildFamily),
            PolicyMode = request.ExecutionPolicyMode,
            ScopeRiskClassification = FirstNonEmpty(request.ExecutionScopeRiskClassification, scopeRiskClassification),
            IsAutomaticTrigger = request.IsAutomaticTrigger,
            ExecutionAllowed = request.ExecutionAllowed && executionAllowed,
            TargetPath = NormalizeRelativePath(targetPath),
            Reason = request.Reason
        };

        return _executionGateService.Evaluate(gateRequest);
    }

    private ToolResult BuildExecutionGateDeniedToolResult(string toolName, ExecutionGateDecision gateDecision, string targetPath)
    {
        RecordExecutionBlocked(_workspaceService.HasWorkspace() ? _workspaceService.WorkspaceRoot : "", gateDecision, toolName, gateDecision.BlockedReason);
        var output = string.Join(
            Environment.NewLine,
            gateDecision.BlockedReason,
            $"Tool: {toolName}",
            $"Target: {DisplayValue(NormalizeRelativePath(targetPath))}",
            $"Policy mode: {DisplayValue(gateDecision.PolicyMode)}",
            $"Source: {DisplayValue(gateDecision.SourceName)}",
            "No external command launched.");
        var structuredDataJson = SerializeJson(new
        {
            gate_decision = gateDecision,
            target_path = NormalizeRelativePath(targetPath)
        });
        return Failure(toolName, output, "execution_gate_blocked", gateDecision.BlockedReason, structuredDataJson);
    }

    private static void RecordExecutionBlocked(
        string workspaceRoot,
        ExecutionGateDecision gateDecision,
        string toolName,
        string message)
    {
        ExecutionTraceService.Record(new ExecutionTraceEventRecord
        {
            EventKind = "execution_blocked",
            WorkspaceRoot = workspaceRoot,
            SourceType = FormatExecutionSourceType(gateDecision.SourceType),
            SourceName = gateDecision.SourceName,
            ToolName = toolName,
            CommandFamily = gateDecision.CommandFamily,
            BuildFamily = gateDecision.BuildFamily,
            GateDecisionId = gateDecision.DecisionId,
            GateDecisionSummary = gateDecision.Summary,
            Message = message
        });
    }

    private static void RecordNoExecutionPathTaken(string workspaceRoot, AutoValidationPlanRecord plan, string message)
    {
        ExecutionTraceService.Record(new ExecutionTraceEventRecord
        {
            EventKind = "no_execution_path_taken",
            WorkspaceRoot = workspaceRoot,
            SourceType = "auto_validation",
            SourceName = plan.SourceActionType,
            ToolName = plan.SelectedValidationTool,
            CommandFamily = NormalizeCommandFamily(plan.SelectedValidationTool),
            BuildFamily = FirstNonEmpty(plan.BuildFamily, NormalizeBuildFamily(plan.SelectedValidationTool)),
            Message = message
        });
    }

    private static string FormatExecutionSourceType(ExecutionSourceType sourceType)
    {
        return sourceType switch
        {
            ExecutionSourceType.ManualUserRequest => "manual_user_request",
            ExecutionSourceType.AutoValidation => "auto_validation",
            ExecutionSourceType.Verification => "verification",
            ExecutionSourceType.BuildTool => "build_tool",
            _ => "unknown"
        };
    }

    private static bool DidToolExecutionAttemptExternalCommand(ToolResult toolResult)
    {
        if (toolResult is null)
            return false;

        return toolResult.OutcomeType is not "resolution_failure"
            and not "validation_failure"
            and not "execution_gate_blocked"
            and not "not_applicable"
            and not "manual_only";
    }

    private static string NormalizeCommandFamily(string commandFamily)
    {
        return (commandFamily ?? "").Trim().ToLowerInvariant();
    }

    private static string NormalizeBuildFamily(string buildFamily)
    {
        return (buildFamily ?? "").Trim().ToLowerInvariant();
    }

    private static string NormalizeBuildFamily(BuildSystemType buildSystemType)
    {
        return NormalizeBuildSystemType(buildSystemType);
    }

    private string BuildRepairIneligibleMessage(string toolName, RepairEligibilityResult eligibility, string workspaceRoot)
    {
        var snapshot = _latestActionableStateService.GetLatestState(workspaceRoot, _ramDbService);
        var lines = new List<string>
        {
            eligibility.Message,
            "Checked: recorded failure state, failure artifacts, repair artifacts, patch drafts, and verification artifacts."
        };

        if (snapshot.LatestResultKind == "success")
        {
            lines.Add($"Latest result: success tool={DisplayValue(snapshot.LatestResultToolName)} target={DisplayValue(snapshot.LatestResultTargetPath)}.");
            lines.Add("No failure artifacts were recorded for this workspace, so repair actions are unavailable.");
        }
        else if (snapshot.LatestResultKind == "safety_abort")
        {
            lines.Add($"Latest result: safety abort tool={DisplayValue(snapshot.LatestResultToolName)} outcome={DisplayValue(snapshot.LatestResultOutcomeType)} target={DisplayValue(snapshot.LatestResultTargetPath)}.");
            lines.Add(string.Equals(snapshot.LatestResultOutcomeType, "safety_blocked_scope", StringComparison.OrdinalIgnoreCase)
                ? "This build was blocked before launch because the native target scope was too broad, so no repair chain was created."
                : "This was a safety-aborted execution, not a recorded code failure, so no repair chain was created.");
        }
        else if (snapshot.LatestResultKind == "none")
        {
            lines.Add("Latest result: none.");
        }

        lines.Add(toolName switch
        {
            "plan_repair" or "preview_patch_draft" => "Next: run build on a known failing target, or continue editing until a real failure exists.",
            "apply_patch_draft" => "Next: create a repair plan and preview a safe patch draft before applying anything.",
            "verify_patch_draft" => "Next: apply a safe patch draft from a real repair chain before verification.",
            "open_failure_context" => "Next: run build on a known failing target so RAM can capture file and line context.",
            _ => "Next: run build or test first, or select a failure-related artifact."
        });

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildFailureArtifactPath(string artifactType, string targetPath)
    {
        var slugSource = string.IsNullOrWhiteSpace(targetPath) ? "workspace" : NormalizeRelativePath(targetPath);
        var slug = new string(slugSource
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(slug))
            slug = "workspace";

        return $".ram/failures/{artifactType}-{slug}.json";
    }

    private static string BuildRepairProposalArtifactPath(string targetPath, string proposalId)
    {
        var slugSource = string.IsNullOrWhiteSpace(targetPath)
            ? FirstNonEmpty(proposalId, "workspace")
            : NormalizeRelativePath(targetPath);
        var slug = new string(slugSource
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(slug))
            slug = "workspace";

        return $".ram/repair-proposals/{slug}.json";
    }

    private static string BuildPatchDraftArtifactPath(string targetPath, string draftId)
    {
        var slugSource = string.IsNullOrWhiteSpace(targetPath)
            ? FirstNonEmpty(draftId, "workspace")
            : NormalizeRelativePath(targetPath);
        var slug = new string(slugSource
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(slug))
            slug = "workspace";

        return $".ram/patch-drafts/{slug}.json";
    }

    private static string BuildPatchApplyArtifactPath(string targetPath, string draftId)
    {
        var slugSource = string.IsNullOrWhiteSpace(targetPath)
            ? FirstNonEmpty(draftId, "workspace")
            : NormalizeRelativePath(targetPath);
        var slug = new string(slugSource
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(slug))
            slug = "workspace";

        return $".ram/patch-applies/{slug}.json";
    }

    private static string BuildVerificationPlanArtifactPath(string targetPath, string planId)
    {
        var slugSource = string.IsNullOrWhiteSpace(targetPath)
            ? FirstNonEmpty(planId, "workspace")
            : NormalizeRelativePath(targetPath);
        var slug = new string(slugSource
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(slug))
            slug = "workspace";

        return $".ram/verification-plans/{slug}.json";
    }

    private static string BuildVerificationResultArtifactPath(string targetPath, string planId)
    {
        var slugSource = string.IsNullOrWhiteSpace(targetPath)
            ? FirstNonEmpty(planId, "workspace")
            : NormalizeRelativePath(targetPath);
        var slug = new string(slugSource
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(slug))
            slug = "workspace";

        return $".ram/verification-results/{slug}.json";
    }

    private static string BuildAutoValidationPlanArtifactPath(AutoValidationPlanRecord plan)
    {
        var slugSource = plan.ChangedFilePaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
            ?? FirstNonEmpty(plan.SelectedTargetPath, plan.PlanId, "workspace");
        var slug = new string(slugSource
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(slug))
            slug = "workspace";

        return $".ram/auto-validation/plans/{plan.SourceActionType}-{slug}.json";
    }

    private static string BuildAutoValidationResultArtifactPath(AutoValidationResultRecord result)
    {
        var slugSource = FirstNonEmpty(result.ResolvedTarget, result.PlanId, "workspace");
        var slug = new string(slugSource
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(slug))
            slug = "workspace";

        return $".ram/auto-validation/results/{result.SourceActionType}-{slug}.json";
    }

    private static string BuildAutoValidationResultArtifactPath(AutoValidationPlanRecord plan)
    {
        var slugSource = FirstNonEmpty(plan.SelectedTargetPath, plan.PlanId, "workspace");
        var slug = new string(slugSource
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(slug))
            slug = "workspace";

        return $".ram/auto-validation/results/{plan.SourceActionType}-{slug}.json";
    }

    private static string BuildCSharpPatchContractArtifactPath(string targetPath, string contractId)
    {
        var slug = BuildArtifactSlug(FirstNonEmpty(targetPath, contractId, "workspace"));
        return $".ram/csharp-patch/contracts/{slug}-{contractId}.json";
    }

    private static string BuildCSharpPatchPlanArtifactPath(string targetPath, string planId)
    {
        var slug = BuildArtifactSlug(FirstNonEmpty(targetPath, planId, "workspace"));
        return $".ram/csharp-patch/plans/{slug}-{planId}.json";
    }

    private static string BuildManualOnlyCooldownKey(string workspaceRoot, AutoValidationPlanRecord plan)
    {
        return string.Join(
            "|",
            workspaceRoot.Trim(),
            plan.BuildFamily.Trim(),
            plan.SourceActionType.Trim(),
            FirstNonEmpty(plan.SelectedTargetPath, plan.ChangedFilePaths.FirstOrDefault() ?? "").Trim());
    }

    private static string BuildRepairLoopClosureArtifactPath(string targetPath, string draftId)
    {
        var slugSource = string.IsNullOrWhiteSpace(targetPath)
            ? FirstNonEmpty(draftId, "workspace")
            : NormalizeRelativePath(targetPath);
        var slug = new string(slugSource
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(slug))
            slug = "workspace";

        return $".ram/repair-loop-closure/{slug}.json";
    }

    private static string BuildRepairProposalSummary(RepairProposalRecord proposal)
    {
        var scope = proposal.RequiresModel ? "Model brief ready" : "Local repair plan ready";
        var target = string.IsNullOrWhiteSpace(proposal.TargetFilePath) ? "(none)" : proposal.TargetFilePath;
        return $"{scope}: {proposal.Title} Target: {target}.";
    }

    private static string BuildCSharpPatchContractSummary(CSharpPatchWorkContractRecord contract)
    {
        var target = FirstNonEmpty(contract.TargetFiles.FirstOrDefault() ?? "", contract.TargetProjectPath, contract.TargetSolutionPath, "(none)");
        return $"C# patch contract: intent={DisplayValue(contract.ModificationIntent)} surface={DisplayValue(contract.TargetSurfaceType)} family={DisplayValue(contract.MutationFamily)} scope={DisplayValue(contract.AllowedEditScope)} approved={(contract.ScopeApproved ? "yes" : "no")} target={target}.";
    }

    private static string BuildCSharpPatchPlanSummary(CSharpPatchPlanRecord plan)
    {
        var target = FirstNonEmpty(plan.TargetFiles.FirstOrDefault() ?? "", plan.TargetProjectPath, plan.TargetSolutionPath, "(none)");
        return $"C# patch plan: intent={DisplayValue(plan.ModificationIntent)} surface={DisplayValue(plan.TargetSurfaceType)} family={DisplayValue(plan.MutationFamily)} scope={DisplayValue(plan.AllowedEditScope)} target={target} validation={string.Join(",", plan.ValidationSteps)}.";
    }

    private static string BuildPatchDraftSummary(PatchDraftRecord draft)
    {
        var scope = draft.CanApplyLocally ? "Local patch draft ready" : "Patch inspection brief";
        var target = string.IsNullOrWhiteSpace(draft.TargetFilePath) ? "(none)" : draft.TargetFilePath;
        return $"{scope}: {draft.ProposalSummary} Target: {target}.";
    }

    private static string BuildVerificationPlanSummary(VerificationPlanRecord plan)
    {
        var tool = string.IsNullOrWhiteSpace(plan.VerificationTool) ? "read_only_check" : plan.VerificationTool;
        var target = string.IsNullOrWhiteSpace(plan.TargetPath) ? "(none)" : plan.TargetPath;
        return $"Verification plan ready: intent={DisplayValue(plan.ModificationIntent)} surface={DisplayValue(plan.TargetSurfaceType)} tool={tool} target={target} warnings={DisplayValue(plan.WarningPolicyMode)}.";
    }

    private static List<string> BuildPatchVerificationTargetFiles(PatchDraftRecord draft)
    {
        var values = new List<string>();
        AddUniquePath(values, draft.TargetFilePath);
        foreach (var supportingFile in draft.SupportingFiles)
            AddUniquePath(values, supportingFile);
        return values;
    }

    private static string BuildVerificationResultSummary(VerificationOutcomeRecord outcome)
    {
        var target = string.IsNullOrWhiteSpace(outcome.ResolvedTarget) ? "(none)" : outcome.ResolvedTarget;
        return $"Verification {outcome.OutcomeClassification}: {target} warnings={DisplayValue(outcome.AfterWarningCount?.ToString() ?? "")}.";
    }

    private static string BuildAutoValidationPlanSummary(AutoValidationPlanRecord plan)
    {
        var tool = string.IsNullOrWhiteSpace(plan.SelectedValidationTool) ? "(none)" : plan.SelectedValidationTool;
        var target = string.IsNullOrWhiteSpace(plan.SelectedTargetPath)
            ? DisplayValue(plan.ChangedFilePaths.FirstOrDefault() ?? "")
            : plan.SelectedTargetPath;
        return $"Auto-validation plan: mode={DisplayValue(plan.PolicyMode)} tool={tool} target={target} allowed={(plan.ExecutionAllowed ? "yes" : "no")}.";
    }

    private static string BuildAutoValidationResultSummary(AutoValidationResultRecord result)
    {
        var target = string.IsNullOrWhiteSpace(result.ResolvedTarget) ? "(none)" : result.ResolvedTarget;
        return $"Auto-validation {result.OutcomeClassification}: {target}.";
    }

    private static string SerializeJson(object value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static DateTime ParseUtc(string value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed
            : DateTime.MinValue;
    }

    private static string BoundText(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxChars)
            return text ?? "";

        return text[..maxChars].TrimEnd() + "...";
    }

    private static string BuildDefaultSummary(ToolResult result)
    {
        return result.Success
            ? BuildDefaultSummary(result.Output)
            : BuildDefaultSummary(result.ErrorMessage);
    }

    private static string BuildDefaultSummary(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(workspace)" : value;
    }

    private static string ExtractTargetPath(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("target_path", out var targetPath))
                return targetPath.GetString() ?? "";
        }
        catch
        {
            return "";
        }

        return "";
    }

    private static List<string> SplitPipeList(string value)
    {
        return (value ?? "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildMaintenanceBaselineSummary(RepairPlanInput input)
    {
        if (string.IsNullOrWhiteSpace(input.BaselineSolutionPath)
            && input.BaselineAllowedRoots.Count == 0
            && input.BaselineExcludedRoots.Count == 0)
        {
            return "";
        }

        var allowed = input.BaselineAllowedRoots.Count == 0
            ? "(none)"
            : string.Join(", ", input.BaselineAllowedRoots);
        var excluded = input.BaselineExcludedRoots.Count == 0
            ? "(none)"
            : string.Join(", ", input.BaselineExcludedRoots);
        return $"solution={FirstNonEmpty(input.BaselineSolutionPath, "(none)")} allowed_roots={allowed} excluded_roots={excluded}";
    }

    private static T? DeserializeParsedSection<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("parsed", out var parsedElement))
                return parsedElement.Deserialize<T>();
        }
        catch
        {
            return default;
        }

        return default;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private string BuildGitDiffOutput(
        string repoRoot,
        string normalizedPath,
        CommandExecutionResult statusResult,
        CommandExecutionResult diffResult)
    {
        var statusLines = ParseGitStatusLines(statusResult.StandardOutput);
        var changedFiles = statusLines
            .Select(line => line.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var stagedCount = statusLines.Count(line => line.IsStaged);
        var unstagedCount = statusLines.Count(line => line.IsUnstaged);
        var untrackedCount = statusLines.Count(line => line.IsUntracked);

        var lines = new List<string>
        {
            "Git diff:",
            $"Repo root: {repoRoot}",
            $"Path filter: {(string.IsNullOrWhiteSpace(normalizedPath) ? "(workspace)" : normalizedPath)}",
            $"Staged changes: {stagedCount}",
            $"Unstaged changes: {unstagedCount}",
            $"Untracked files: {untrackedCount}"
        };

        if (changedFiles.Count == 0)
        {
            lines.Add("Changed files: (none)");
        }
        else
        {
            lines.Add("Changed files:");
            foreach (var file in changedFiles.Take(20))
                lines.Add($"- {file}");
        }

        if (!string.IsNullOrWhiteSpace(diffResult.StandardOutput))
        {
            lines.Add("Patch excerpt:");
            lines.Add(diffResult.StandardOutput.Trim());
        }
        else
        {
            lines.Add("Patch excerpt: (none)");
        }

        if (!string.IsNullOrWhiteSpace(diffResult.StandardError))
        {
            lines.Add("stderr:");
            lines.Add(diffResult.StandardError.Trim());
        }

        if (diffResult.OutputWasTruncated)
            lines.Add("[OUTPUT TRUNCATED]");

        return string.Join(Environment.NewLine, lines);
    }

    private static string TrimCommandOutput(string output)
    {
        return (output ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
    }

    private static List<GitStatusLine> ParseGitStatusLines(string output)
    {
        var results = new List<GitStatusLine>();
        foreach (var rawLine in (output ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawLine.StartsWith("##", StringComparison.Ordinal))
                continue;

            if (rawLine.Length < 3)
                continue;

            var indexStatus = rawLine[0];
            var workTreeStatus = rawLine[1];
            var path = rawLine[3..].Trim();
            if (path.Contains("->", StringComparison.Ordinal))
                path = path[(path.LastIndexOf("->", StringComparison.Ordinal) + 2)..].Trim();

            results.Add(new GitStatusLine
            {
                Path = path.Replace('\\', '/'),
                IsStaged = indexStatus != ' ' && indexStatus != '?',
                IsUnstaged = workTreeStatus != ' ',
                IsUntracked = indexStatus == '?' && workTreeStatus == '?'
            });
        }

        return results;
    }

    private sealed class GitStatusLine
    {
        public string Path { get; set; } = "";
        public bool IsStaged { get; set; }
        public bool IsUnstaged { get; set; }
        public bool IsUntracked { get; set; }
    }

    private sealed class ArtifactPayload<T>
    {
        public ArtifactPayload(ArtifactRecord artifact, T payload, string targetPath)
        {
            Artifact = artifact;
            Payload = payload;
            TargetPath = targetPath;
        }

        public ArtifactRecord Artifact { get; }
        public T Payload { get; }
        public string TargetPath { get; }
    }

    private RepairProposalRecord? TryLoadRepairProposalById(string workspaceRoot, long artifactId)
    {
        if (artifactId <= 0)
            return null;

        var artifact = _ramDbService.LoadArtifactById(workspaceRoot, artifactId);
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.Content))
            return null;

        try
        {
            return JsonSerializer.Deserialize<RepairProposalRecord>(artifact.Content);
        }
        catch
        {
            return null;
        }
    }

    private ArtifactRecord? LoadArtifactByRelativePath(string workspaceRoot, string? relativePath)
    {
        return string.IsNullOrWhiteSpace(relativePath)
            ? null
            : _ramDbService.LoadLatestArtifactByRelativePath(workspaceRoot, relativePath);
    }

    private static T? TryDeserializeArtifact<T>(ArtifactRecord? artifact)
    {
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.Content))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(artifact.Content);
        }
        catch
        {
            return default;
        }
    }

    private static void AddUniquePath(ICollection<string> values, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;

        if (!values.Any(current => string.Equals(current, candidate, StringComparison.OrdinalIgnoreCase)))
            values.Add(candidate);
    }
}
