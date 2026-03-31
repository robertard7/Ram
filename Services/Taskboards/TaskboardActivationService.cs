using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardActivationService
{
    private readonly TaskboardArtifactStore _artifactStore = new();

    public TaskboardActivationResult Promote(
        string workspaceRoot,
        string importId,
        RamDbService ramDbService,
        bool allowReplaceActive = false,
        string actionName = "Activate Selected")
    {
        var imports = _artifactStore.LoadImports(ramDbService, workspaceRoot, 100);
        var target = imports.FirstOrDefault(record => string.Equals(record.ImportId, importId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return new TaskboardActivationResult
            {
                ActionName = actionName,
                StatusCode = "not_found",
                Message = $"{actionName} rejected: selected taskboard import could not be found."
            };
        }

        if (target.State == TaskboardImportState.ActivePlan)
        {
            return new TaskboardActivationResult
            {
                ActionName = actionName,
                WasSkipped = true,
                StatusCode = "already_active",
                Message = $"{actionName} skipped: selected taskboard `{target.Title}` is already the active plan.",
                ImportRecord = target
            };
        }

        if (target.State == TaskboardImportState.Archived)
        {
            return new TaskboardActivationResult
            {
                ActionName = actionName,
                StatusCode = "archived",
                Message = $"{actionName} rejected: selected taskboard `{target.Title}` is archived.",
                ImportRecord = target
            };
        }

        if (target.State == TaskboardImportState.Deleted)
        {
            return new TaskboardActivationResult
            {
                ActionName = actionName,
                StatusCode = "deleted",
                Message = $"{actionName} rejected: selected taskboard `{target.Title}` was deleted.",
                ImportRecord = target
            };
        }

        if (target.State == TaskboardImportState.Rejected || !target.ValidationOutcome.ToString().StartsWith("Valid", StringComparison.OrdinalIgnoreCase))
        {
            return new TaskboardActivationResult
            {
                ActionName = actionName,
                StatusCode = "not_promotable",
                Message = $"{actionName} rejected: selected taskboard `{target.Title}` validation={FormatValidation(target.ValidationOutcome)}.",
                ImportRecord = target
            };
        }

        if (target.State != TaskboardImportState.ReadyForPromotion && target.State != TaskboardImportState.Validated)
        {
            return new TaskboardActivationResult
            {
                ActionName = actionName,
                StatusCode = "wrong_state",
                Message = $"{actionName} rejected: selected taskboard `{target.Title}` is not in ready_for_promotion state.",
                ImportRecord = target
            };
        }

        var existingActive = imports.FirstOrDefault(record => record.State == TaskboardImportState.ActivePlan
            && !string.Equals(record.ImportId, importId, StringComparison.OrdinalIgnoreCase));
        var existingActiveCompleted = existingActive is not null && IsCompletedPlan(workspaceRoot, existingActive, ramDbService);
        if (existingActive is not null && !allowReplaceActive && !existingActiveCompleted)
        {
            return new TaskboardActivationResult
            {
                ActionName = actionName,
                StatusCode = "active_plan_exists",
                Message = $"{actionName} blocked: active plan `{existingActive.Title}` is still active. Archive it before activating `{target.Title}`.",
                ImportRecord = target
            };
        }

        foreach (var active in imports.Where(record => record.State == TaskboardImportState.ActivePlan
            && !string.Equals(record.ImportId, importId, StringComparison.OrdinalIgnoreCase)))
        {
            active.State = TaskboardImportState.Archived;
            active.UpdatedUtc = DateTime.UtcNow.ToString("O");
            _artifactStore.SaveImportRecordArtifact(ramDbService, workspaceRoot, active);
        }

        target.State = TaskboardImportState.ActivePlan;
        target.ActivatedUtc = DateTime.UtcNow.ToString("O");
        target.UpdatedUtc = target.ActivatedUtc;
        _artifactStore.SaveImportRecordArtifact(ramDbService, workspaceRoot, target);

        return new TaskboardActivationResult
        {
            ActionName = actionName,
            Success = true,
            StateChanged = true,
            ActivePlanChanged = true,
            StatusCode = existingActive is null
                ? "activated"
                : existingActiveCompleted
                    ? "activated_replacing_completed"
                    : "activated_replacing_active",
            Message = BuildActivationSuccessMessage(actionName, target.Title, existingActive?.Title, existingActiveCompleted),
            ImportRecord = target
        };
    }

    public TaskboardActivationResult TryAutoPromoteImportedTaskboard(
        string workspaceRoot,
        TaskboardImportRecord? importRecord,
        RamDbService ramDbService)
    {
        const string actionName = "Auto-promote";

        if (importRecord is null)
        {
            return new TaskboardActivationResult
            {
                ActionName = actionName,
                StatusCode = "no_import",
                Message = "Auto-promote skipped: no imported taskboard was available."
            };
        }

        if (importRecord.State == TaskboardImportState.ActivePlan)
        {
            return new TaskboardActivationResult
            {
                ActionName = actionName,
                WasSkipped = true,
                StatusCode = "already_active",
                Message = $"Auto-promote skipped: `{importRecord.Title}` is already the active plan.",
                ImportRecord = importRecord
            };
        }

        if (importRecord.State is not TaskboardImportState.ReadyForPromotion and not TaskboardImportState.Validated)
        {
            return new TaskboardActivationResult
            {
                ActionName = actionName,
                WasSkipped = true,
                StatusCode = "not_promotable",
                Message = $"Auto-promote skipped: imported taskboard `{importRecord.Title}` is in state {importRecord.State.ToString().ToLowerInvariant()} and is not ready for activation.",
                ImportRecord = importRecord
            };
        }

        var imports = _artifactStore.LoadImports(ramDbService, workspaceRoot, 100);
        var active = imports.FirstOrDefault(record =>
            record.State == TaskboardImportState.ActivePlan
            && !string.Equals(record.ImportId, importRecord.ImportId, StringComparison.OrdinalIgnoreCase));
        if (active is null)
        {
            return Promote(
                workspaceRoot,
                importRecord.ImportId,
                ramDbService,
                allowReplaceActive: false,
                actionName: actionName);
        }

        if (!IsCompletedPlan(workspaceRoot, active, ramDbService))
        {
            return new TaskboardActivationResult
            {
                ActionName = actionName,
                WasSkipped = true,
                StatusCode = "active_plan_incomplete",
                Message = $"Auto-promote skipped: active plan `{active.Title}` is not completed yet, so `{importRecord.Title}` remains imported only.",
                ImportRecord = importRecord
            };
        }

        return Promote(
            workspaceRoot,
            importRecord.ImportId,
            ramDbService,
            allowReplaceActive: true,
            actionName: actionName);
    }

    public TaskboardActivationResult TryAutoActivateWhenNoActivePlan(
        string workspaceRoot,
        TaskboardImportRecord? importRecord,
        RamDbService ramDbService)
    {
        if (importRecord is null)
        {
            return new TaskboardActivationResult
            {
                ActionName = "Auto-activate",
                StatusCode = "no_import",
                Message = "Auto-activate skipped: no taskboard import was available."
            };
        }

        var imports = _artifactStore.LoadImports(ramDbService, workspaceRoot, 100);
        var active = imports.FirstOrDefault(record => record.State == TaskboardImportState.ActivePlan);
        if (active is not null)
        {
            return new TaskboardActivationResult
            {
                ActionName = "Auto-activate",
                WasSkipped = true,
                StatusCode = "active_plan_exists",
                Message = $"Auto-activate skipped: active plan `{active.Title}` already exists.",
                ImportRecord = importRecord
            };
        }

        return Promote(
            workspaceRoot,
            importRecord.ImportId,
            ramDbService,
            allowReplaceActive: false,
            actionName: "Auto-activate");
    }

    public TaskboardActivationResult ArchiveActive(string workspaceRoot, RamDbService ramDbService)
    {
        var imports = _artifactStore.LoadImports(ramDbService, workspaceRoot, 100);
        var active = imports.FirstOrDefault(record => record.State == TaskboardImportState.ActivePlan);
        if (active is null)
        {
            return new TaskboardActivationResult
            {
                ActionName = "Archive Active",
                StatusCode = "no_active_plan",
                Message = "Archive Active blocked: no active taskboard plan is currently stored for this workspace."
            };
        }

        active.State = TaskboardImportState.Archived;
        active.UpdatedUtc = DateTime.UtcNow.ToString("O");
        _artifactStore.SaveImportRecordArtifact(ramDbService, workspaceRoot, active);

        return new TaskboardActivationResult
        {
            ActionName = "Archive Active",
            Success = true,
            StateChanged = true,
            StatusCode = "archived",
            Message = $"Archive Active succeeded: `{active.Title}` is now archived.",
            ImportRecord = active
        };
    }

    private static string FormatValidation(TaskboardValidationOutcome outcome)
    {
        return outcome switch
        {
            TaskboardValidationOutcome.ValidWithWarnings => "valid_with_warnings",
            TaskboardValidationOutcome.MissingRequiredSection => "missing_required_section",
            TaskboardValidationOutcome.DuplicateBatch => "duplicate_batch",
            TaskboardValidationOutcome.UnsupportedStructure => "unsupported_structure",
            TaskboardValidationOutcome.TextLimitExceeded => "text_limit_exceeded",
            TaskboardValidationOutcome.UnsafeContentDetected => "unsafe_content_detected",
            TaskboardValidationOutcome.ValidationException => "validation_exception",
            _ => outcome.ToString().ToLowerInvariant()
        };
    }

    private bool IsCompletedPlan(string workspaceRoot, TaskboardImportRecord importRecord, RamDbService ramDbService)
    {
        var runState = _artifactStore.LoadRunState(ramDbService, workspaceRoot, importRecord);
        if (runState?.PlanStatus == TaskboardPlanRuntimeStatus.Completed)
            return true;

        var summary = _artifactStore.LoadLatestRunSummary(ramDbService, workspaceRoot, importRecord);
        return string.Equals(summary?.FinalStatus, "completed", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildActivationSuccessMessage(
        string actionName,
        string targetTitle,
        string? replacedActiveTitle,
        bool replacedCompletedPlan)
    {
        if (string.IsNullOrWhiteSpace(replacedActiveTitle))
            return $"{actionName} succeeded: taskboard `{targetTitle}` is now the active plan.";

        if (replacedCompletedPlan && string.Equals(actionName, "Auto-promote", StringComparison.OrdinalIgnoreCase))
        {
            return $"Imported and activated next-phase taskboard `{targetTitle}`. Completed plan `{replacedActiveTitle}` was preserved as archived history.";
        }

        if (replacedCompletedPlan)
        {
            return $"{actionName} succeeded: taskboard `{targetTitle}` is now the active plan. Completed plan `{replacedActiveTitle}` was preserved as archived history.";
        }

        return $"{actionName} succeeded: taskboard `{targetTitle}` is now the active plan. Previous active plan `{replacedActiveTitle}` was archived.";
    }
}
