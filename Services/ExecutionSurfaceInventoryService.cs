using RAM.Models;

namespace RAM.Services;

public sealed class ExecutionSurfaceInventoryService
{
    public IReadOnlyList<ExecutionSurfaceRecord> GetRecords()
    {
        return
        [
            new ExecutionSurfaceRecord
            {
                FilePath = @"C:\dev\ram\RAM\Services\CommandExecutionService.cs",
                MethodName = "Execute",
                TriggerSource = "run_command, git_status, git_diff, dotnet_build, dotnet_test",
                TrustLevel = ExecutionSurfaceTrustLevel.GateProtected,
                ReachableFromPostWriteFlows = true,
                Notes = "Shared executor for command-backed tools. Phase 15B requires explicit gate metadata before launch."
            },
            new ExecutionSurfaceRecord
            {
                FilePath = @"C:\dev\ram\RAM\Services\CommandExecutionService.cs",
                MethodName = "ExecuteTrustedToolCommand",
                TriggerSource = "create_dotnet_solution, create_dotnet_project, add_project_to_solution, add_dotnet_project_reference, cmake_configure, cmake_build, make_build, ninja_build, run_build_script",
                TrustLevel = ExecutionSurfaceTrustLevel.GateProtected,
                ReachableFromPostWriteFlows = true,
                Notes = "Trusted bounded external tool launcher. Phase 15B hard-locks this behind gate-approved metadata."
            },
            new ExecutionSurfaceRecord
            {
                FilePath = @"C:\dev\ram\RAM\Services\ToolExecutionService.cs",
                MethodName = "RunAutoValidationPlan",
                TriggerSource = "write_file, replace_in_file, save_output, apply_patch_draft post-change flow",
                TrustLevel = ExecutionSurfaceTrustLevel.GateProtected,
                ReachableFromPostWriteFlows = true,
                Notes = "Planner/persistence branch that must stop cleanly for manual_only and not_applicable outcomes."
            },
            new ExecutionSurfaceRecord
            {
                FilePath = @"C:\dev\ram\RAM\Services\ToolExecutionService.cs",
                MethodName = "ExecuteVerifyPatchDraft",
                TriggerSource = "explicit verification follow-up after patch apply",
                TrustLevel = ExecutionSurfaceTrustLevel.GateProtected,
                ReachableFromPostWriteFlows = true,
                Notes = "Can launch verification builds only after explicit verify flow and valid repair chain."
            },
            new ExecutionSurfaceRecord
            {
                FilePath = @"C:\dev\ram\RAM\Services\ToolExecutionService.cs",
                MethodName = "ExecuteGitDiff",
                TriggerSource = "explicit git_diff request with repo probe support",
                TrustLevel = ExecutionSurfaceTrustLevel.GateProtected,
                ReachableFromPostWriteFlows = false,
                Notes = "Manual git helper path. Uses the same gated executor for rev-parse and status probes."
            },
            new ExecutionSurfaceRecord
            {
                FilePath = @"C:\dev\ram\RAM\UI\MainWindow.Tools.Execution.cs",
                MethodName = "ExecuteToolRequest",
                TriggerSource = "WPF UI routing for manual, deterministic, and AI tool requests",
                TrustLevel = ExecutionSurfaceTrustLevel.Unknown,
                ReachableFromPostWriteFlows = true,
                Notes = "Not a launcher itself, but the main UI entry that must preserve execution-source metadata."
            },
            new ExecutionSurfaceRecord
            {
                FilePath = @"C:\dev\ram\RAM",
                MethodName = "Repo audit",
                TriggerSource = "FileSystemWatcher / Task.Run / delayed launch scan",
                TrustLevel = ExecutionSurfaceTrustLevel.BackgroundTrigger,
                ReachableFromPostWriteFlows = false,
                Notes = "No FileSystemWatcher, Task.Run, ContinueWith, queued background launcher, or timer-based execution path was found in the repo audit."
            }
        ];
    }
}
