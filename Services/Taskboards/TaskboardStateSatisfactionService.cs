using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardStateSatisfactionService
{
    private static readonly HashSet<string> NoSkipTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "open_failure_context",
        "plan_repair",
        "preview_patch_draft",
        "apply_patch_draft",
        "verify_patch_draft"
    };

    public TaskboardStateSatisfactionResultRecord Evaluate(
        string workspaceRoot,
        TaskboardPlanRunStateRecord runState,
        TaskboardBatchRunStateRecord batch,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionBridgeResult bridge,
        RamDbService ramDbService)
    {
        var request = bridge.ToolRequest;
        if (request is null)
        {
            var result = new TaskboardStateSatisfactionResultRecord
            {
                WorkItemId = workItem.WorkItemId,
                StepId = BuildStepId(request, bridge, workItem),
                CheckFamily = ResolveCheckFamily(request),
                ReasonCode = "state_not_satisfied",
                EvidenceSummary = "No deterministic state-satisfaction proof was found."
            };
            result.ReasonCode = "missing_tool_request";
            result.EvidenceSummary = "Execution bridge did not produce a tool request, so no state-satisfaction check could run.";
            return result;
        }

        var touches = ramDbService.LoadFileTouchRecordsForRun(workspaceRoot, runState.RunStateId, 4000);
        return EvaluateCore(workspaceRoot, runState, workItem, bridge, request, touches, ramDbService, allowNoSkipReuse: false);
    }

    public TaskboardStateSatisfactionResultRecord EvaluatePlannedStep(
        string workspaceRoot,
        TaskboardPlanRunStateRecord runState,
        string workItemId,
        string workItemTitle,
        string workFamily,
        ToolRequest request,
        string resolvedTargetPath,
        RamDbService ramDbService,
        bool allowNoSkipReuse = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        var touches = ramDbService.LoadFileTouchRecordsForRun(workspaceRoot, runState.RunStateId, 4000);
        var syntheticWorkItem = new TaskboardWorkItemRunStateRecord
        {
            WorkItemId = workItemId,
            Title = workItemTitle,
            WorkFamily = workFamily
        };
        var syntheticBridge = new TaskboardExecutionBridgeResult
        {
            ToolRequest = request,
            ResolvedTargetPath = resolvedTargetPath
        };
        return EvaluateCore(workspaceRoot, runState, syntheticWorkItem, syntheticBridge, request, touches, ramDbService, allowNoSkipReuse);
    }

    private static TaskboardStateSatisfactionResultRecord EvaluateCore(
        string workspaceRoot,
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionBridgeResult bridge,
        ToolRequest request,
        IReadOnlyList<RamFileTouchRecord> touches,
        RamDbService ramDbService,
        bool allowNoSkipReuse)
    {
        if (!allowNoSkipReuse && IsNoSkipZone(request, workItem))
        {
            return new TaskboardStateSatisfactionResultRecord
            {
                WorkItemId = workItem.WorkItemId,
                StepId = BuildStepId(request, bridge, workItem),
                CheckFamily = "no_skip_zone",
                SkipAllowed = false,
                TrustSource = "no_skip_policy",
                ReasonCode = "no_skip_zone",
                EvidenceSummary = $"Deterministic fast-path skipping is disabled for `{request.ToolName}`."
            };
        }

        if (BlocksLocalSkipDueToBehaviorFollowThrough(runState, workItem, request, out var followThroughReason))
        {
            return new TaskboardStateSatisfactionResultRecord
            {
                WorkItemId = workItem.WorkItemId,
                StepId = BuildStepId(request, bridge, workItem),
                CheckFamily = ResolveCheckFamily(request),
                SkipAllowed = false,
                TrustSource = "behavior_depth_followthrough",
                ReasonCode = "feature_followthrough_required",
                EvidenceSummary = followThroughReason
            };
        }

        return NormalizeToolName(request.ToolName) switch
        {
            "create_dotnet_solution" => EvaluateSolutionScaffold(workspaceRoot, workItem, bridge, request, touches),
            "create_dotnet_project" => EvaluateProjectScaffold(workspaceRoot, workItem, bridge, request, touches),
            "add_project_to_solution" => EvaluateSolutionMembership(workspaceRoot, workItem, bridge, request, touches),
            "add_dotnet_project_reference" => EvaluateProjectReference(workspaceRoot, workItem, bridge, request, touches),
            "make_dir" => EvaluateDirectoryPresence(workspaceRoot, workItem, bridge, request, touches),
            "dotnet_build" => EvaluateBuildVerification(workspaceRoot, runState, workItem, bridge, request, touches, ramDbService),
            "dotnet_test" => EvaluateTestVerification(workspaceRoot, runState, workItem, bridge, request, touches, ramDbService),
            _ => EvaluateWorkspaceTextFile(workspaceRoot, workItem, bridge, request, touches)
        };
    }

    private static TaskboardStateSatisfactionResultRecord EvaluateSolutionScaffold(
        string workspaceRoot,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionBridgeResult bridge,
        ToolRequest request,
        IReadOnlyList<RamFileTouchRecord> touches)
    {
        var solutionName = request.TryGetArgument("solution_name", out var requestedName)
            ? requestedName.Trim()
            : "";
        if (string.IsNullOrWhiteSpace(solutionName))
            return BuildUnsatisfied(workItem, request, bridge, "solution_scaffold", "missing_solution_name", "Tool request did not include solution_name.");

        var targetPath = NormalizeRelativePath($"{solutionName}.sln");
        var fullPath = ResolveWorkspacePath(workspaceRoot, targetPath);
        if (!File.Exists(fullPath))
            return BuildUnsatisfied(workItem, request, bridge, "solution_scaffold", "solution_missing", $"Expected solution `{targetPath}` does not exist.");

        var content = SafeReadAllText(fullPath);
        if (!content.Contains("Microsoft Visual Studio Solution File", StringComparison.OrdinalIgnoreCase))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "solution_scaffold",
                "solution_structure_invalid",
                $"Solution `{targetPath}` exists, but the expected Visual Studio solution header was not found.",
                targetPath);
        }

        var sameRunTouches = CountTouchesForPath(touches, targetPath);
        return BuildSatisfied(
            workItem,
            request,
            bridge,
            "solution_scaffold",
            sameRunTouches > 0 ? "file_touch_and_solution_structure" : "solution_structure_valid",
            "solution_exists_valid",
            $"Solution `{targetPath}` already exists and has a valid solution-file header.",
            targetPath,
            usedFileTouchFastPath: sameRunTouches > 0,
            repeatedTouchesAvoidedCount: sameRunTouches > 0 ? 1 : 0);
    }

    private static TaskboardStateSatisfactionResultRecord EvaluateProjectScaffold(
        string workspaceRoot,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionBridgeResult bridge,
        ToolRequest request,
        IReadOnlyList<RamFileTouchRecord> touches)
    {
        if (!request.TryGetArgument("project_name", out var projectName)
            || !request.TryGetArgument("output_path", out var outputPath))
        {
            return BuildUnsatisfied(workItem, request, bridge, "project_scaffold", "missing_project_target", "Tool request did not include a deterministic project target.");
        }

        var targetPath = NormalizeRelativePath(Path.Combine(outputPath, $"{projectName}.csproj"));
        var fullPath = ResolveWorkspacePath(workspaceRoot, targetPath);
        if (!File.Exists(fullPath))
            return BuildUnsatisfied(workItem, request, bridge, "project_scaffold", "project_missing", $"Expected project `{targetPath}` does not exist.");

        if (!LooksLikeProjectFile(fullPath))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "project_scaffold",
                "project_structure_invalid",
                $"Project `{targetPath}` exists, but it does not parse as a valid SDK-style project file.",
                targetPath);
        }

        var sameRunTouches = CountTouchesForPath(touches, targetPath);
        return BuildSatisfied(
            workItem,
            request,
            bridge,
            "project_scaffold",
            sameRunTouches > 0 ? "file_touch_and_project_structure" : "project_structure_valid",
            "project_exists_valid",
            $"Project `{targetPath}` already exists and parses as a valid project file.",
            targetPath,
            usedFileTouchFastPath: sameRunTouches > 0,
            repeatedTouchesAvoidedCount: sameRunTouches > 0 ? 1 : 0);
    }

    private static TaskboardStateSatisfactionResultRecord EvaluateSolutionMembership(
        string workspaceRoot,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionBridgeResult bridge,
        ToolRequest request,
        IReadOnlyList<RamFileTouchRecord> touches)
    {
        if (!request.TryGetArgument("solution_path", out var solutionPath)
            || !request.TryGetArgument("project_path", out var projectPath))
        {
            return BuildUnsatisfied(workItem, request, bridge, "solution_membership", "missing_solution_membership_target", "Tool request did not include deterministic solution and project paths.");
        }

        solutionPath = NormalizeRelativePath(solutionPath);
        projectPath = NormalizeRelativePath(projectPath);
        var fullSolutionPath = ResolveWorkspacePath(workspaceRoot, solutionPath);
        var fullProjectPath = ResolveWorkspacePath(workspaceRoot, projectPath);
        if (!File.Exists(fullSolutionPath) || !File.Exists(fullProjectPath))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "solution_membership",
                "membership_target_missing",
                $"Solution membership cannot be verified because `{solutionPath}` or `{projectPath}` is missing.",
                solutionPath,
                projectPath);
        }

        var expectedReference = NormalizeRelativePath(Path.GetRelativePath(Path.GetDirectoryName(fullSolutionPath) ?? workspaceRoot, fullProjectPath))
            .Replace('/', '\\');
        var solutionContent = SafeReadAllText(fullSolutionPath);
        if (!solutionContent.Contains(expectedReference, StringComparison.OrdinalIgnoreCase))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "solution_membership",
                "solution_membership_missing",
                $"Solution `{solutionPath}` does not yet reference project `{projectPath}`.",
                solutionPath,
                projectPath);
        }

        var sameRunTouches = CountTouchesForPath(touches, solutionPath) + CountTouchesForPath(touches, projectPath);
        return BuildSatisfied(
            workItem,
            request,
            bridge,
            "solution_membership",
            sameRunTouches > 0 ? "file_touch_and_solution_membership" : "solution_membership_valid",
            "project_already_in_solution",
            $"Solution `{solutionPath}` already references project `{projectPath}`.",
            solutionPath,
            projectPath,
            usedFileTouchFastPath: sameRunTouches > 0,
            repeatedTouchesAvoidedCount: sameRunTouches > 0 ? 1 : 0);
    }

    private static TaskboardStateSatisfactionResultRecord EvaluateProjectReference(
        string workspaceRoot,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionBridgeResult bridge,
        ToolRequest request,
        IReadOnlyList<RamFileTouchRecord> touches)
    {
        if (!request.TryGetArgument("project_path", out var projectPath)
            || !request.TryGetArgument("reference_path", out var referencePath))
        {
            return BuildUnsatisfied(workItem, request, bridge, "project_reference", "missing_project_reference_target", "Tool request did not include deterministic project-reference paths.");
        }

        projectPath = NormalizeRelativePath(projectPath);
        referencePath = NormalizeRelativePath(referencePath);
        var fullProjectPath = ResolveWorkspacePath(workspaceRoot, projectPath);
        var fullReferencePath = ResolveWorkspacePath(workspaceRoot, referencePath);
        if (string.Equals(fullProjectPath, fullReferencePath, StringComparison.OrdinalIgnoreCase))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "project_reference",
                "blocked_self_project_reference",
                $"Project reference validation blocked because `{projectPath}` resolves to the same project as `{referencePath}`.",
                projectPath,
                referencePath);
        }

        if (!File.Exists(fullProjectPath) || !File.Exists(fullReferencePath))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "project_reference",
                "project_reference_target_missing",
                $"Project reference cannot be verified because `{projectPath}` or `{referencePath}` is missing.",
                projectPath,
                referencePath);
        }

        if (!ProjectContainsReference(fullProjectPath, fullReferencePath))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "project_reference",
                "project_reference_missing",
                $"Project `{projectPath}` does not yet include a reference to `{referencePath}`.",
                projectPath,
                referencePath);
        }

        var sameRunTouches = CountTouchesForPath(touches, projectPath) + CountTouchesForPath(touches, referencePath);
        return BuildSatisfied(
            workItem,
            request,
            bridge,
            "project_reference",
            sameRunTouches > 0 ? "file_touch_and_project_reference" : "project_reference_valid",
            "project_reference_already_present",
            $"Project `{projectPath}` already references `{referencePath}`.",
            projectPath,
            referencePath,
            usedFileTouchFastPath: sameRunTouches > 0,
            repeatedTouchesAvoidedCount: sameRunTouches > 0 ? 1 : 0);
    }

    private static TaskboardStateSatisfactionResultRecord EvaluateWorkspaceTextFile(
        string workspaceRoot,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionBridgeResult bridge,
        ToolRequest request,
        IReadOnlyList<RamFileTouchRecord> touches)
    {
        if (!request.TryGetArgument("path", out var path) || !request.TryGetArgument("content", out var expectedContent))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "workspace_text_file",
                "no_state_satisfaction_rule",
                $"No deterministic state-satisfaction rule is defined for `{request.ToolName}`.");
        }

        var targetPath = NormalizeRelativePath(path);
        var fullPath = ResolveWorkspacePath(workspaceRoot, targetPath);
        var parentDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "workspace_text_file",
                "directory_missing_before_write",
                $"Expected parent directory for `{targetPath}` does not exist yet, so directory_exists_before_write is not satisfied.",
                targetPath);
        }

        if (!File.Exists(fullPath))
            return BuildUnsatisfied(workItem, request, bridge, "workspace_text_file", "target_file_missing", $"Expected file `{targetPath}` does not exist.", targetPath);

        var actualContent = SafeReadAllText(fullPath);
        if (!string.Equals(NormalizeText(actualContent), NormalizeText(expectedContent), StringComparison.Ordinal))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "workspace_text_file",
                "file_content_mismatch",
                $"Existing file `{targetPath}` does not match the expected content for `{request.ToolName}`.",
                targetPath);
        }

        var sameRunTouches = CountTouchesForPath(touches, targetPath);
        return BuildSatisfied(
            workItem,
            request,
            bridge,
            "workspace_text_file",
            sameRunTouches > 0 ? "file_touch_and_exact_content" : "exact_content_match",
            sameRunTouches > 0 ? "duplicate_write_replay_suppressed" : "file_content_match",
            sameRunTouches > 0
                ? $"File `{targetPath}` already matches the expected content for `{request.ToolName}`, directory_exists_before_write is satisfied, and the same run already touched that file, so duplicate write replay was suppressed."
                : $"File `{targetPath}` already matches the expected content for `{request.ToolName}` and directory_exists_before_write is satisfied.",
            targetPath,
            usedFileTouchFastPath: sameRunTouches > 0,
            repeatedTouchesAvoidedCount: sameRunTouches > 0 ? 1 : 0);
    }

    private static TaskboardStateSatisfactionResultRecord EvaluateDirectoryPresence(
        string workspaceRoot,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionBridgeResult bridge,
        ToolRequest request,
        IReadOnlyList<RamFileTouchRecord> touches)
    {
        if (!request.TryGetArgument("path", out var path))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "directory_presence",
                "missing_directory_target",
                "Tool request did not include a deterministic directory path.");
        }

        var targetPath = NormalizeRelativePath(path);
        var fullPath = ResolveWorkspacePath(workspaceRoot, targetPath);
        if (!Directory.Exists(fullPath))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "directory_presence",
                "directory_missing",
                $"Expected directory `{targetPath}` does not exist.",
                targetPath);
        }

        var sameRunTouches = CountTouchesForPath(touches, targetPath);
        return BuildSatisfied(
            workItem,
            request,
            bridge,
            "directory_presence",
            sameRunTouches > 0 ? "file_touch_and_directory_presence" : "directory_present",
            "directory_already_exists",
            $"Directory `{targetPath}` already exists.",
            targetPath,
            usedFileTouchFastPath: sameRunTouches > 0,
            repeatedTouchesAvoidedCount: sameRunTouches > 0 ? 1 : 0);
    }

    private static TaskboardStateSatisfactionResultRecord EvaluateBuildVerification(
        string workspaceRoot,
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionBridgeResult bridge,
        ToolRequest request,
        IReadOnlyList<RamFileTouchRecord> touches,
        RamDbService ramDbService)
    {
        var targetPath = ResolveBuildTargetPath(request, bridge);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "build_verify",
                "missing_build_target",
                "Build verification did not resolve to a deterministic target.");
        }

        targetPath = NormalizeRelativePath(targetPath);
        var fullTargetPath = ResolveWorkspacePath(workspaceRoot, targetPath);
        if (!File.Exists(fullTargetPath))
            return BuildUnsatisfied(workItem, request, bridge, "build_verify", "build_target_missing", $"Build target `{targetPath}` does not exist.", targetPath);

        var verificationArtifact = FindLatestSuccessfulBuildArtifact(workspaceRoot, runState, targetPath, ramDbService);
        var executionState = ramDbService.LoadExecutionState(workspaceRoot);
        var successUtc = FirstNonEmpty(
            verificationArtifact?.UpdatedUtc,
            IsMatchingSuccessfulExecutionState(executionState, runState, targetPath) ? executionState.LastSuccessUtc : "",
            IsMatchingVerifiedExecutionState(executionState, runState, targetPath) ? executionState.LastVerificationUtc : "");

        if (string.IsNullOrWhiteSpace(successUtc))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "build_verify",
                "no_prior_verified_build",
                $"No same-run successful build verification was recorded yet for `{targetPath}`.",
                targetPath);
        }

        var invalidatingTouch = touches
            .Where(current => current.ContentChanged && ParseUtc(current.CreatedUtc) > ParseUtc(successUtc))
            .OrderByDescending(current => current.TouchOrderIndex)
            .FirstOrDefault();
        if (invalidatingTouch is not null)
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "build_verify",
                "build_verification_invalidated",
                $"Build verification for `{targetPath}` was invalidated by a later content-changing touch to `{invalidatingTouch.FilePath}`.",
                invalidatingTouch.FilePath,
                targetPath,
                linkedArtifactIds: verificationArtifact is null ? null : [verificationArtifact.Id],
                invalidationReasonCode: "later_content_change");
        }

        return BuildSatisfied(
            workItem,
            request,
            bridge,
            "build_verify",
            touches.Count > 0 ? "prior_verified_build_and_touch_history" : "prior_verified_build",
            "green_validation_replay_suppressed",
            $"A same-run successful build verification for `{targetPath}` already exists and no later content changes invalidated that green proof, so duplicate validation replay was suppressed.",
            targetPath,
            usedFileTouchFastPath: touches.Count > 0,
            repeatedTouchesAvoidedCount: touches.Count > 0 ? 1 : 0,
            linkedArtifactIds: verificationArtifact is null ? null : [verificationArtifact.Id]);
    }

    private static TaskboardStateSatisfactionResultRecord EvaluateTestVerification(
        string workspaceRoot,
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord workItem,
        TaskboardExecutionBridgeResult bridge,
        ToolRequest request,
        IReadOnlyList<RamFileTouchRecord> touches,
        RamDbService ramDbService)
    {
        var targetPath = ResolveTestTargetPath(request, bridge);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "test_verify",
                "missing_test_target",
                "Test verification did not resolve to a deterministic target.");
        }

        targetPath = NormalizeRelativePath(targetPath);
        var fullTargetPath = ResolveWorkspacePath(workspaceRoot, targetPath);
        if (!File.Exists(fullTargetPath))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "test_verify",
                "test_target_missing",
                $"Test target `{targetPath}` does not exist.",
                targetPath);
        }

        var executionState = ramDbService.LoadExecutionState(workspaceRoot);
        var successUtc = FirstNonEmpty(
            IsMatchingSuccessfulTestExecutionState(executionState, runState, targetPath) ? executionState.LastSuccessUtc : "",
            IsMatchingVerifiedTestExecutionState(executionState, runState, targetPath) ? executionState.LastVerificationUtc : "");
        if (string.IsNullOrWhiteSpace(successUtc))
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "test_verify",
                "no_prior_green_test_proof",
                $"No same-run successful direct test proof was recorded yet for `{targetPath}`.",
                targetPath);
        }

        var invalidatingTouch = touches
            .Where(current => current.ContentChanged && ParseUtc(current.CreatedUtc) > ParseUtc(successUtc))
            .OrderByDescending(current => current.TouchOrderIndex)
            .FirstOrDefault();
        if (invalidatingTouch is not null)
        {
            return BuildUnsatisfied(
                workItem,
                request,
                bridge,
                "test_verify",
                "direct_test_proof_invalidated",
                $"The last green direct-test proof for `{targetPath}` was invalidated by a later content-changing touch to `{invalidatingTouch.FilePath}`.",
                invalidatingTouch.FilePath,
                targetPath,
                invalidationReasonCode: "later_content_change");
        }

        return BuildSatisfied(
            workItem,
            request,
            bridge,
            "test_verify",
            "prior_green_test_proof",
            "direct_test_replay_suppressed",
            $"A same-run successful direct test proof for `{targetPath}` already exists and no later content changes invalidated it, so duplicate direct-test replay was suppressed.",
            targetPath,
            usedFileTouchFastPath: touches.Count > 0,
            repeatedTouchesAvoidedCount: 1);
    }

    private static TaskboardStateSatisfactionResultRecord BuildSatisfied(
        TaskboardWorkItemRunStateRecord workItem,
        ToolRequest request,
        TaskboardExecutionBridgeResult bridge,
        string checkFamily,
        string trustSource,
        string reasonCode,
        string evidenceSummary,
        string? checkedFilePath = null,
        string? secondaryCheckedFilePath = null,
        bool usedFileTouchFastPath = false,
        int repeatedTouchesAvoidedCount = 0,
        IReadOnlyList<long>? linkedArtifactIds = null)
    {
        var result = CreateBaseResult(workItem, request, bridge, checkFamily);
        result.Satisfied = true;
        result.SkipAllowed = true;
        result.TrustSource = trustSource;
        result.ReasonCode = reasonCode;
        result.EvidenceSummary = evidenceSummary;
        result.UsedFileTouchFastPath = usedFileTouchFastPath;
        result.RepeatedTouchesAvoidedCount = repeatedTouchesAvoidedCount;
        if (!string.IsNullOrWhiteSpace(checkedFilePath))
            result.CheckedFilePaths.Add(NormalizeRelativePath(checkedFilePath));
        if (!string.IsNullOrWhiteSpace(secondaryCheckedFilePath))
            result.CheckedFilePaths.Add(NormalizeRelativePath(secondaryCheckedFilePath));
        if (linkedArtifactIds is not null)
            result.LinkedArtifactIds = linkedArtifactIds.Where(current => current > 0).Distinct().ToList();
        return result;
    }

    private static TaskboardStateSatisfactionResultRecord BuildUnsatisfied(
        TaskboardWorkItemRunStateRecord workItem,
        ToolRequest request,
        TaskboardExecutionBridgeResult bridge,
        string checkFamily,
        string reasonCode,
        string evidenceSummary,
        string? checkedFilePath = null,
        string? secondaryCheckedFilePath = null,
        IReadOnlyList<long>? linkedArtifactIds = null,
        string invalidationReasonCode = "")
    {
        var result = CreateBaseResult(workItem, request, bridge, checkFamily);
        result.TrustSource = "deterministic_check";
        result.ReasonCode = reasonCode;
        result.EvidenceSummary = evidenceSummary;
        result.InvalidationReasonCode = invalidationReasonCode;
        if (!string.IsNullOrWhiteSpace(checkedFilePath))
            result.CheckedFilePaths.Add(NormalizeRelativePath(checkedFilePath));
        if (!string.IsNullOrWhiteSpace(secondaryCheckedFilePath))
            result.CheckedFilePaths.Add(NormalizeRelativePath(secondaryCheckedFilePath));
        if (linkedArtifactIds is not null)
            result.LinkedArtifactIds = linkedArtifactIds.Where(current => current > 0).Distinct().ToList();
        return result;
    }

    private static TaskboardStateSatisfactionResultRecord CreateBaseResult(
        TaskboardWorkItemRunStateRecord workItem,
        ToolRequest request,
        TaskboardExecutionBridgeResult bridge,
        string checkFamily)
    {
        return new TaskboardStateSatisfactionResultRecord
        {
            WorkItemId = workItem.WorkItemId,
            StepId = BuildStepId(request, bridge, workItem),
            CheckFamily = checkFamily
        };
    }

    private static ArtifactRecord? FindLatestSuccessfulBuildArtifact(
        string workspaceRoot,
        TaskboardPlanRunStateRecord runState,
        string targetPath,
        RamDbService ramDbService)
    {
        var sinceUtc = FirstNonEmpty(runState.StartedUtc, DateTime.UtcNow.AddHours(-4).ToString("O"));
        return ramDbService.LoadArtifactsSince(workspaceRoot, sinceUtc, 200)
            .Where(current => string.Equals(current.ArtifactType, "build_result", StringComparison.OrdinalIgnoreCase))
            .Select(current => new
            {
                Artifact = current,
                OutcomeType = TryReadJsonString(current.Content, "outcome_type"),
                ArtifactTargetPath = NormalizeRelativePath(TryReadJsonString(current.Content, "target_path"))
            })
            .Where(current =>
                string.Equals(current.OutcomeType, "success", StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.ArtifactTargetPath, targetPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(current => ParseUtc(current.Artifact.UpdatedUtc))
            .Select(current => current.Artifact)
            .FirstOrDefault();
    }

    private static bool IsMatchingSuccessfulExecutionState(
        WorkspaceExecutionStateRecord executionState,
        TaskboardPlanRunStateRecord runState,
        string targetPath)
    {
        return string.Equals(executionState.LastSuccessOutcomeType, "success", StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeRelativePath(executionState.LastSuccessTargetPath), targetPath, StringComparison.OrdinalIgnoreCase)
            && ParseUtc(executionState.LastSuccessUtc) >= ParseUtc(runState.StartedUtc);
    }

    private static bool IsMatchingSuccessfulTestExecutionState(
        WorkspaceExecutionStateRecord executionState,
        TaskboardPlanRunStateRecord runState,
        string targetPath)
    {
        return string.Equals(executionState.LastSuccessToolName, "dotnet_test", StringComparison.OrdinalIgnoreCase)
            && string.Equals(executionState.LastSuccessOutcomeType, "success", StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeRelativePath(executionState.LastSuccessTargetPath), targetPath, StringComparison.OrdinalIgnoreCase)
            && ParseUtc(executionState.LastSuccessUtc) >= ParseUtc(runState.StartedUtc);
    }

    private static bool IsMatchingVerifiedExecutionState(
        WorkspaceExecutionStateRecord executionState,
        TaskboardPlanRunStateRecord runState,
        string targetPath)
    {
        return string.Equals(executionState.LastVerificationOutcomeType, "success", StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeRelativePath(executionState.LastVerificationTargetPath), targetPath, StringComparison.OrdinalIgnoreCase)
            && ParseUtc(executionState.LastVerificationUtc) >= ParseUtc(runState.StartedUtc);
    }

    private static bool IsMatchingVerifiedTestExecutionState(
        WorkspaceExecutionStateRecord executionState,
        TaskboardPlanRunStateRecord runState,
        string targetPath)
    {
        return string.Equals(executionState.LastVerificationToolName, "dotnet_test", StringComparison.OrdinalIgnoreCase)
            && string.Equals(executionState.LastVerificationOutcomeType, "success", StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeRelativePath(executionState.LastVerificationTargetPath), targetPath, StringComparison.OrdinalIgnoreCase)
            && ParseUtc(executionState.LastVerificationUtc) >= ParseUtc(runState.StartedUtc);
    }

    private static bool LooksLikeProjectFile(string fullPath)
    {
        try
        {
            var document = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
            return string.Equals(document.Root?.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool ProjectContainsReference(string fullProjectPath, string fullReferencePath)
    {
        try
        {
            var projectDirectory = Path.GetDirectoryName(fullProjectPath) ?? "";
            var expected = NormalizeReferenceInclude(Path.GetRelativePath(projectDirectory, fullReferencePath));
            var document = XDocument.Load(fullProjectPath, LoadOptions.PreserveWhitespace);
            var includes = document
                .Descendants()
                .Where(current => string.Equals(current.Name.LocalName, "ProjectReference", StringComparison.OrdinalIgnoreCase))
                .Select(current => NormalizeReferenceInclude(current.Attribute("Include")?.Value ?? ""))
                .Where(current => !string.IsNullOrWhiteSpace(current))
                .ToList();
            return includes.Any(current => string.Equals(current, expected, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNoSkipZone(ToolRequest request, TaskboardWorkItemRunStateRecord workItem)
    {
        var toolName = NormalizeToolName(request.ToolName);
        if (NoSkipTools.Contains(toolName))
            return true;

        if (string.Equals(Normalize(workItem.WorkFamily), "build_repair", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(Normalize(request.ExecutionBuildFamily), "repair", StringComparison.OrdinalIgnoreCase);
    }

    private static bool BlocksLocalSkipDueToBehaviorFollowThrough(
        TaskboardPlanRunStateRecord runState,
        TaskboardWorkItemRunStateRecord workItem,
        ToolRequest request,
        out string evidenceSummary)
    {
        evidenceSummary = "";
        if (!HasBehaviorDepthFollowUpRequirement(runState))
            return false;

        var toolName = NormalizeToolName(request.ToolName);
        if (toolName is not ("write_file" or "replace_in_file" or "append_file" or "create_file"))
            return false;

        var workFamily = Normalize(workItem.WorkFamily);
        var recommendation = FirstNonEmpty(runState.LastBehaviorDepthFollowUpRecommendation, runState.LastBehaviorDepthCompletionRecommendation);
        if (!RequiresAdjacentIntegrationFollowThrough(workFamily, recommendation, runState.LastBehaviorDepthIntegrationGapKind))
            return false;

        var followThroughDetail = new List<string>();
        if (!string.IsNullOrWhiteSpace(runState.LastBehaviorDepthIntegrationGapKind))
            followThroughDetail.Add($"gap={runState.LastBehaviorDepthIntegrationGapKind}");
        if (!string.IsNullOrWhiteSpace(runState.LastBehaviorDepthNextFollowThroughHint))
            followThroughDetail.Add($"next_followthrough={runState.LastBehaviorDepthNextFollowThroughHint}");
        if (!string.IsNullOrWhiteSpace(runState.LastBehaviorDepthTargetPath))
            followThroughDetail.Add($"source={runState.LastBehaviorDepthTargetPath}");

        var followThroughSuffix = followThroughDetail.Count == 0
            ? "."
            : $" ({string.Join(" ", followThroughDetail)}).";
        evidenceSummary = $"Local file content alone is not enough to skip `{FirstNonEmpty(workItem.Title, workItem.WorkItemId, "(unknown)")}` because behavior-depth evidence still requires adjacent integration follow-through: {FirstNonEmpty(recommendation, "follow-up required")}{followThroughSuffix}";
        return true;
    }

    private static bool HasBehaviorDepthFollowUpRequirement(TaskboardPlanRunStateRecord runState)
    {
        if (runState is null)
            return false;

        if (string.Equals(runState.LastBehaviorDepthCompletionRecommendation, "followup_required_for_behavior_depth", StringComparison.OrdinalIgnoreCase)
            || string.Equals(runState.LastBehaviorDepthCompletionRecommendation, "accepted_behavior_without_closure", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(runState.LastBehaviorDepthFollowUpRecommendation)
            && !string.Equals(runState.LastBehaviorDepthFollowUpRecommendation, "no_additional_followup_required", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresAdjacentIntegrationFollowThrough(string workFamily, string recommendation, string integrationGapKind)
    {
        if (string.IsNullOrWhiteSpace(workFamily))
            return false;

        if (integrationGapKind is "missing_service_registration" or "missing_repository_consumer" or "missing_repository_store_consumer")
            return workFamily is "repository_scaffold" or "storage_bootstrap" or "app_state_wiring" or "ui_wiring";

        if (integrationGapKind is "missing_viewmodel_consumer" or "missing_binding_surface" or "missing_navigation_use_site")
            return workFamily is "app_state_wiring" or "ui_wiring" or "ui_shell_sections" or "viewmodel_scaffold";

        if (integrationGapKind is "missing_helper_caller_path" or "missing_behavior_path_test")
            return workFamily is "check_runner" or "findings_pipeline";

        if (ContainsAny(recommendation, "consumer", "registration", "service"))
        {
            return workFamily is "repository_scaffold" or "storage_bootstrap" or "app_state_wiring" or "ui_wiring";
        }

        if (ContainsAny(recommendation, "binding", "viewmodel", "navigation", "shell", "state", "ui"))
        {
            return workFamily is "app_state_wiring" or "ui_wiring" or "ui_shell_sections" or "viewmodel_scaffold";
        }

        if (ContainsAny(recommendation, "caller path", "behavior-path test", "test"))
        {
            return workFamily is "check_runner" or "findings_pipeline";
        }

        return workFamily is "repository_scaffold"
            or "storage_bootstrap"
            or "app_state_wiring"
            or "ui_wiring"
            or "ui_shell_sections"
            or "viewmodel_scaffold"
            or "check_runner"
            or "findings_pipeline";
    }

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveBuildTargetPath(ToolRequest request, TaskboardExecutionBridgeResult bridge)
    {
        if (request.TryGetArgument("project", out var project))
            return project;
        if (request.TryGetArgument("path", out var path))
            return path;
        return bridge.ResolvedTargetPath;
    }

    private static string ResolveTestTargetPath(ToolRequest request, TaskboardExecutionBridgeResult bridge)
    {
        if (request.TryGetArgument("project", out var project))
            return project;
        if (request.TryGetArgument("path", out var path))
            return path;
        return bridge.ResolvedTargetPath;
    }

    private static string ResolveCheckFamily(ToolRequest? request)
    {
        return NormalizeToolName(request?.ToolName) switch
        {
            "create_dotnet_solution" => "solution_scaffold",
            "create_dotnet_project" => "project_scaffold",
            "add_project_to_solution" => "solution_membership",
            "add_dotnet_project_reference" => "project_reference",
            "make_dir" => "directory_presence",
            "dotnet_build" => "build_verify",
            "dotnet_test" => "test_verify",
            _ => "workspace_text_file"
        };
    }

    private static string BuildStepId(ToolRequest? request, TaskboardExecutionBridgeResult bridge, TaskboardWorkItemRunStateRecord workItem)
    {
        var toolName = NormalizeToolName(request?.ToolName);
        var target = NormalizeRelativePath(ResolveBuildTargetPath(request ?? new ToolRequest(), bridge));
        target = string.IsNullOrWhiteSpace(target) && request is not null && request.TryGetArgument("path", out var path)
            ? NormalizeRelativePath(path)
            : target;
        return string.IsNullOrWhiteSpace(target)
            ? $"{FirstNonEmpty(toolName, "step")}:{FirstNonEmpty(workItem.WorkItemId, workItem.Title, "unknown")}"
            : $"{FirstNonEmpty(toolName, "step")}:{target}";
    }

    private static int CountTouchesForPath(IReadOnlyList<RamFileTouchRecord> touches, string path)
    {
        var normalizedPath = NormalizeRelativePath(path);
        return touches.Count(current => string.Equals(NormalizeRelativePath(current.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeReferenceInclude(string value)
    {
        return NormalizeRelativePath(value).Replace('/', '\\');
    }

    private static string ResolveWorkspacePath(string workspaceRoot, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        return Path.GetFullPath(Path.Combine(workspaceRoot, normalized));
    }

    private static string SafeReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeText(string value)
    {
        return (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        return path
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Trim();
    }

    private static string Normalize(string value)
    {
        return (value ?? "").Trim();
    }

    private static string NormalizeToolName(string? toolName)
    {
        return Normalize(toolName ?? "").ToLowerInvariant();
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

    private static DateTime ParseUtc(string value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.MinValue;
    }

    private static string TryReadJsonString(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
            return "";

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return "";

            return document.RootElement.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                    ? property.GetString() ?? ""
                    : "";
        }
        catch
        {
            return "";
        }
    }
}
