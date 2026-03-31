using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardExecutionBridgeService
{
    private readonly TaskboardExecutionGoalMappingService _goalMappingService = new();

    public TaskboardExecutionBridgeResult Bridge(
        string workspaceRoot,
        TaskboardRunWorkItem workItem,
        string planTitle,
        string activeTargetRelativePath)
    {
        var goalResolution = _goalMappingService.Resolve(workspaceRoot, workItem, planTitle, activeTargetRelativePath);
        return goalResolution.GoalKind switch
        {
            TaskboardExecutionGoalKind.ToolGoal or TaskboardExecutionGoalKind.ChainGoal => BuildExecutableResult(goalResolution),
            TaskboardExecutionGoalKind.ManualOnlyGoal => new TaskboardExecutionBridgeResult
            {
                Disposition = TaskboardExecutionBridgeDisposition.ManualOnly,
                Eligibility = goalResolution.Eligibility,
                PromptText = goalResolution.PromptText,
                RequestKind = goalResolution.RequestKind,
                Reason = goalResolution.ResolutionReason,
                ResolvedTargetPath = goalResolution.ResolvedTargetPath,
                ExecutionGoalResolution = goalResolution
            },
            _ => new TaskboardExecutionBridgeResult
            {
                Disposition = TaskboardExecutionBridgeDisposition.Blocked,
                Eligibility = goalResolution.Eligibility,
                PromptText = goalResolution.PromptText,
                RequestKind = goalResolution.RequestKind,
                Reason = FirstNonEmpty(goalResolution.Blocker.Message, goalResolution.ResolutionReason),
                ResolvedTargetPath = goalResolution.ResolvedTargetPath,
                ExecutionGoalResolution = goalResolution
            }
        };
    }

    private static TaskboardExecutionBridgeResult BuildExecutableResult(TaskboardExecutionGoalResolution goalResolution)
    {
        var request = new ToolRequest
        {
            ToolName = goalResolution.Goal.SelectedToolId,
            PreferredChainTemplateName = goalResolution.Goal.SelectedChainTemplateId,
            Reason = goalResolution.Goal.ResolutionReason,
            ExecutionSourceType = ExecutionSourceType.BuildTool,
            ExecutionSourceName = $"taskboard_auto_run:{goalResolution.SourceWorkItemId}",
            IsAutomaticTrigger = true,
            ExecutionAllowed = true,
            ExecutionPolicyMode = "taskboard_auto_run",
            ExecutionBuildFamily = DetermineBuildFamily(goalResolution.Goal.SelectedToolId)
        };

        foreach (var argument in goalResolution.Goal.BoundedArguments)
            request.Arguments[argument.Key] = argument.Value;

        return new TaskboardExecutionBridgeResult
        {
            Disposition = TaskboardExecutionBridgeDisposition.Executable,
            Eligibility = goalResolution.Eligibility,
            PromptText = goalResolution.PromptText,
            RequestKind = goalResolution.RequestKind,
            ResponseMode = goalResolution.ResponseMode,
            Reason = goalResolution.ResolutionReason,
            ResolvedTargetPath = goalResolution.ResolvedTargetPath,
            ToolRequest = request,
            ExecutionGoalResolution = goalResolution
        };
    }

    private static string DetermineBuildFamily(string toolName)
    {
        return NormalizeToolName(toolName) switch
        {
            "create_dotnet_solution" or "create_dotnet_project" or "add_project_to_solution" or "add_dotnet_project_reference" or "create_dotnet_page_view" or "create_dotnet_viewmodel" or "register_navigation" or "register_di_service" or "initialize_sqlite_storage_boundary" or "dotnet_build" or "dotnet_test" => "dotnet",
            "create_cmake_project" or "cmake_configure" or "cmake_build" or "ctest_run" => "cmake",
            "create_cpp_source_file" or "create_cpp_header_file" or "create_c_source_file" or "create_c_header_file" => "native",
            "plan_repair" or "preview_patch_draft" or "apply_patch_draft" or "verify_patch_draft" => "repair",
            "make_build" => "make",
            "ninja_build" => "ninja",
            "run_build_script" => "script",
            _ => ""
        };
    }

    private static string NormalizeToolName(string toolName)
    {
        return (toolName ?? "").Trim().ToLowerInvariant();
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
