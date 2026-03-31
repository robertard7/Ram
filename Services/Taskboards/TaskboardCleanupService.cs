using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardCleanupService
{
    private readonly TaskboardArtifactStore _artifactStore = new();

    public TaskboardActionResult ArchiveSelected(string workspaceRoot, string importId, RamDbService ramDbService)
    {
        var target = LoadImport(workspaceRoot, importId, ramDbService, "Archive Selected", out var notFound);
        if (notFound is not null)
            return notFound;

        if (target!.State == TaskboardImportState.Archived)
        {
            return BuildResult("Archive Selected", "already_archived", $"Archive Selected skipped: `{target.Title}` is already archived.", target, wasSkipped: true);
        }

        if (target.State == TaskboardImportState.Deleted)
        {
            return BuildResult("Archive Selected", "deleted", $"Archive Selected rejected: `{target.Title}` was deleted.", target);
        }

        target.State = TaskboardImportState.Archived;
        target.UpdatedUtc = DateTime.UtcNow.ToString("O");
        _artifactStore.SaveImportRecordArtifact(ramDbService, workspaceRoot, target);
        return BuildResult("Archive Selected", "archived", $"Archive Selected succeeded: `{target.Title}` is now archived.", target, success: true, stateChanged: true);
    }

    public TaskboardActionResult DeleteSelected(string workspaceRoot, string importId, RamDbService ramDbService)
    {
        var target = LoadImport(workspaceRoot, importId, ramDbService, "Delete Selected", out var notFound);
        if (notFound is not null)
            return notFound;

        if (target!.State == TaskboardImportState.ActivePlan)
        {
            return BuildResult("Delete Selected", "active_plan_protected", $"Delete Selected blocked: `{target.Title}` is the active plan. Archive it first.", target);
        }

        if (target.State == TaskboardImportState.Deleted)
        {
            return BuildResult("Delete Selected", "already_deleted", $"Delete Selected skipped: `{target.Title}` was already deleted.", target, wasSkipped: true);
        }

        target.State = TaskboardImportState.Deleted;
        target.UpdatedUtc = DateTime.UtcNow.ToString("O");
        _artifactStore.SaveImportRecordArtifact(ramDbService, workspaceRoot, target);
        return BuildResult("Delete Selected", "deleted", $"Delete Selected succeeded: `{target.Title}` was removed from the active taskboard list.", target, success: true, stateChanged: true);
    }

    public TaskboardActionResult ClearRejected(string workspaceRoot, RamDbService ramDbService)
    {
        var imports = _artifactStore.LoadImports(ramDbService, workspaceRoot, 200);
        var targets = imports
            .Where(record => record.State == TaskboardImportState.Rejected)
            .ToList();
        if (targets.Count == 0)
        {
            return new TaskboardActionResult
            {
                ActionName = "Clear Rejected",
                WasSkipped = true,
                StatusCode = "none_found",
                Message = "Clear Rejected skipped: no rejected taskboards were found."
            };
        }

        foreach (var target in targets)
        {
            target.State = TaskboardImportState.Deleted;
            target.UpdatedUtc = DateTime.UtcNow.ToString("O");
            _artifactStore.SaveImportRecordArtifact(ramDbService, workspaceRoot, target);
        }

        return new TaskboardActionResult
        {
            ActionName = "Clear Rejected",
            Success = true,
            StateChanged = true,
            StatusCode = "cleared",
            AffectedCount = targets.Count,
            Message = $"Clear Rejected removed {targets.Count} taskboard{(targets.Count == 1 ? "" : "s")}."
        };
    }

    public TaskboardActionResult ClearInactive(string workspaceRoot, RamDbService ramDbService)
    {
        var imports = _artifactStore.LoadImports(ramDbService, workspaceRoot, 200);
        var targets = imports
            .Where(record => record.State is not TaskboardImportState.ActivePlan and not TaskboardImportState.Deleted)
            .ToList();
        if (targets.Count == 0)
        {
            return new TaskboardActionResult
            {
                ActionName = "Clear Inactive",
                WasSkipped = true,
                StatusCode = "none_found",
                Message = "Clear Inactive skipped: no inactive taskboards were found."
            };
        }

        foreach (var target in targets)
        {
            target.State = TaskboardImportState.Deleted;
            target.UpdatedUtc = DateTime.UtcNow.ToString("O");
            _artifactStore.SaveImportRecordArtifact(ramDbService, workspaceRoot, target);
        }

        return new TaskboardActionResult
        {
            ActionName = "Clear Inactive",
            Success = true,
            StateChanged = true,
            StatusCode = "cleared",
            AffectedCount = targets.Count,
            Message = $"Clear Inactive removed {targets.Count} non-active taskboard{(targets.Count == 1 ? "" : "s")}."
        };
    }

    private TaskboardImportRecord? LoadImport(
        string workspaceRoot,
        string importId,
        RamDbService ramDbService,
        string actionName,
        out TaskboardActionResult? notFoundResult)
    {
        notFoundResult = null;
        var imports = _artifactStore.LoadImports(ramDbService, workspaceRoot, 200);
        var target = imports.FirstOrDefault(record => string.Equals(record.ImportId, importId, StringComparison.OrdinalIgnoreCase));
        if (target is not null)
            return target;

        notFoundResult = new TaskboardActionResult
        {
            ActionName = actionName,
            StatusCode = "not_found",
            Message = $"{actionName} rejected: selected taskboard import could not be found."
        };
        return null;
    }

    private static TaskboardActionResult BuildResult(
        string actionName,
        string statusCode,
        string message,
        TaskboardImportRecord importRecord,
        bool success = false,
        bool stateChanged = false,
        bool wasSkipped = false)
    {
        return new TaskboardActionResult
        {
            ActionName = actionName,
            Success = success,
            StateChanged = stateChanged,
            WasSkipped = wasSkipped,
            StatusCode = statusCode,
            Message = message,
            ImportRecord = importRecord
        };
    }
}
