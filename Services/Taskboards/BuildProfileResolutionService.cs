using System.IO;
using System.Text.RegularExpressions;
using RAM.Models;

namespace RAM.Services;

public sealed class BuildProfileResolutionService
{
    public const string ResolverContractVersion = "build_profile_resolution.v3";

    private readonly BuildSystemDetectionService _buildSystemDetectionService = new();
    private readonly BuildProfileAgentService? _buildProfileAgentService;

    public BuildProfileResolutionService(BuildProfileAgentService? buildProfileAgentService = null)
    {
        _buildProfileAgentService = buildProfileAgentService;
    }

    public async Task<TaskboardBuildProfileResolutionRecord> ResolveAsync(
        string workspaceRoot,
        TaskboardDocument? document,
        TaskboardBatch? batch,
        TaskboardRunWorkItem workItem,
        RamDbService? ramDbService = null,
        AppSettings? settings = null,
        string endpoint = "",
        string selectedModel = "",
        CancellationToken cancellationToken = default,
        TaskboardPlanRunStateRecord? activeRunState = null)
    {
        var explicitIntent = ResolveExplicitTaskboardIntent(document, batch, workItem);
        var workspaceEvidence = ResolveWorkspaceEvidence(workspaceRoot, document, batch, workItem);
        var artifactEvidence = ResolveArtifactEvidence(workspaceRoot, ramDbService);
        var activeRunEvidence = ResolveActiveRunEvidence(activeRunState, workItem, workspaceEvidence, artifactEvidence);

        if (explicitIntent.Status == TaskboardBuildProfileResolutionStatus.Conflict)
        {
            if (CanPreferActiveRunEvidence(activeRunEvidence, workspaceEvidence, artifactEvidence))
                return MergeResolvedWithSupport(activeRunEvidence, workspaceEvidence, artifactEvidence);

            return explicitIntent;
        }

        if (explicitIntent.Status == TaskboardBuildProfileResolutionStatus.Resolved)
        {
            if (activeRunEvidence.Status == TaskboardBuildProfileResolutionStatus.Resolved
                && activeRunEvidence.StackFamily != TaskboardStackFamily.Unknown
                && activeRunEvidence.StackFamily != explicitIntent.StackFamily
                && CanPreferActiveRunEvidence(activeRunEvidence, workspaceEvidence, artifactEvidence))
            {
                return MergeResolvedWithSupport(activeRunEvidence, workspaceEvidence, artifactEvidence);
            }

            if (workspaceEvidence.Status == TaskboardBuildProfileResolutionStatus.Conflict)
                return BuildConflict(
                    explicitIntent,
                    workspaceEvidence,
                    $"Taskboard auto-run paused: explicit taskboard stack intent conflicts with the current workspace evidence. {workspaceEvidence.ResolutionReason}");

            if (workspaceEvidence.Status == TaskboardBuildProfileResolutionStatus.Resolved
                && workspaceEvidence.StackFamily != TaskboardStackFamily.Unknown
                && workspaceEvidence.StackFamily != explicitIntent.StackFamily
                && workspaceEvidence.Confidence >= TaskboardBuildProfileConfidence.Medium)
            {
                return BuildConflict(
                    explicitIntent,
                    workspaceEvidence,
                    $"Taskboard auto-run paused: taskboard intent resolves to `{FormatStackFamily(explicitIntent.StackFamily)}`, but workspace evidence resolves to `{FormatStackFamily(workspaceEvidence.StackFamily)}`.");
            }

            return MergeResolved(explicitIntent, workspaceEvidence, artifactEvidence);
        }

        if (workspaceEvidence.Status == TaskboardBuildProfileResolutionStatus.Conflict)
            return workspaceEvidence;

        if (workspaceEvidence.Status == TaskboardBuildProfileResolutionStatus.Resolved)
            return MergeResolved(workspaceEvidence, artifactEvidence);

        if (activeRunEvidence.Status == TaskboardBuildProfileResolutionStatus.Resolved)
            return MergeResolvedWithSupport(activeRunEvidence, workspaceEvidence, artifactEvidence);

        if (artifactEvidence.Status == TaskboardBuildProfileResolutionStatus.Resolved)
            return artifactEvidence;

        var advisory = await ResolveWithAgentAsync(
            workspaceRoot,
            document,
            batch,
            workItem,
            artifactEvidence,
            settings,
            endpoint,
            selectedModel,
            cancellationToken);
        if (advisory.Status == TaskboardBuildProfileResolutionStatus.Resolved)
            return advisory;

        return new TaskboardBuildProfileResolutionRecord
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            Status = TaskboardBuildProfileResolutionStatus.Unknown,
            StackFamily = TaskboardStackFamily.Unknown,
            Confidence = TaskboardBuildProfileConfidence.None,
            ResolutionReason = "Taskboard auto-run paused: build stack intent is unresolved, and the workspace does not yet provide enough evidence to choose a safe decomposition template.",
            SourceEvidence =
            [
                .. explicitIntent.SourceEvidence,
                .. workspaceEvidence.SourceEvidence,
                .. artifactEvidence.SourceEvidence
            ],
            MissingEvidence = MergeMissingEvidence(
                "No explicit taskboard stack intent was found.",
                "Workspace build evidence was insufficient.",
                "No persisted build-profile evidence was available.")
        };
    }

    private async Task<TaskboardBuildProfileResolutionRecord> ResolveWithAgentAsync(
        string workspaceRoot,
        TaskboardDocument? document,
        TaskboardBatch? batch,
        TaskboardRunWorkItem workItem,
        TaskboardBuildProfileResolutionRecord artifactEvidence,
        AppSettings? settings,
        string endpoint,
        string selectedModel,
        CancellationToken cancellationToken)
    {
        if (_buildProfileAgentService is null || settings is null)
            return new TaskboardBuildProfileResolutionRecord();

        var request = new BuildProfileAgentRequestPayload
        {
            TaskboardTitle = document?.Title ?? "",
            ObjectiveExcerpt = BuildObjectiveExcerpt(document),
            BatchTitle = batch?.Title ?? "",
            WorkItemTitle = workItem.Title,
            WorkItemSummary = workItem.Summary,
            WorkspaceEvidence = ResolveWorkspaceEvidenceLines(workspaceRoot),
            ArtifactEvidence = artifactEvidence.SourceEvidence
                .Select(evidence => $"{FormatEvidenceSource(evidence.Source)}:{evidence.Code}:{evidence.Value}")
                .ToList(),
            MissingEvidence =
            [
                "Explicit stack intent is unresolved.",
                "Workspace evidence was insufficient for deterministic resolution."
            ],
            AllowedStackFamilies =
            [
                "dotnet_desktop",
                "native_cpp_desktop",
                "web_app",
                "rust_app",
                "unknown"
            ]
        };

        var result = await _buildProfileAgentService.InferAsync(
            endpoint,
            selectedModel,
            settings,
            workspaceRoot,
            request,
            cancellationToken);
        if (!result.Accepted)
            return new TaskboardBuildProfileResolutionRecord();

        var stackFamily = ParseStackFamily(result.Payload.StackFamily);
        if (stackFamily == TaskboardStackFamily.Unknown)
            return new TaskboardBuildProfileResolutionRecord();

        return new TaskboardBuildProfileResolutionRecord
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            Status = TaskboardBuildProfileResolutionStatus.Resolved,
            StackFamily = stackFamily,
            Language = result.Payload.Language,
            Framework = result.Payload.Framework,
            UiShellKind = result.Payload.UiShellKind,
            Confidence = ParseConfidence(result.Payload.Confidence),
            ResolutionReason = $"Advisory build-profile inference suggested `{FormatStackFamily(stackFamily)}` because deterministic evidence was incomplete.",
            SourceEvidence =
            [
                new TaskboardBuildProfileEvidenceRecord
                {
                    Source = TaskboardBuildProfileEvidenceSource.AdvisoryAgent,
                    Code = string.Join("+", result.Payload.RationaleCodes.Take(4)),
                    Value = result.Payload.StackFamily,
                    Detail = $"trace={result.TraceId}"
                }
            ],
            MissingEvidence = result.Payload.MissingEvidence,
            AdvisoryUsed = true,
            AdvisoryTraceId = result.TraceId
        };
    }

    private TaskboardBuildProfileResolutionRecord ResolveExplicitTaskboardIntent(
        TaskboardDocument? document,
        TaskboardBatch? batch,
        TaskboardRunWorkItem workItem)
    {
        var explicitWorkItemStack = ParseStackFamily(workItem.TargetStack);
        if (explicitWorkItemStack != TaskboardStackFamily.Unknown)
        {
            return new TaskboardBuildProfileResolutionRecord
            {
                ResolutionId = Guid.NewGuid().ToString("N"),
                Status = TaskboardBuildProfileResolutionStatus.Resolved,
                StackFamily = explicitWorkItemStack,
                Language = InferLanguage(explicitWorkItemStack),
                Framework = InferFramework(explicitWorkItemStack, FirstNonEmpty(workItem.PromptText, workItem.Title)),
                UiShellKind = InferUiShellKind(explicitWorkItemStack, FirstNonEmpty(workItem.PromptText, workItem.Title)),
                Confidence = TaskboardBuildProfileConfidence.High,
                ResolutionReason = $"Resolved build profile from explicit work-item target stack: `{FormatStackFamily(explicitWorkItemStack)}`.",
                SourceEvidence =
                [
                    new TaskboardBuildProfileEvidenceRecord
                    {
                        Source = TaskboardBuildProfileEvidenceSource.TaskboardIntent,
                        Code = "explicit_work_item_target_stack",
                        Value = FormatStackFamily(explicitWorkItemStack),
                        Detail = FirstNonEmpty(workItem.Title, workItem.WorkItemId, "work item target stack")
                    }
                ]
            };
        }

        var scopedText = CollectScopedTaskboardIntentText(batch, workItem);
        var scopedFamilies = DetectStackFamilies(scopedText);
        if (scopedFamilies.Count > 1)
        {
            return new TaskboardBuildProfileResolutionRecord
            {
                ResolutionId = Guid.NewGuid().ToString("N"),
                Status = TaskboardBuildProfileResolutionStatus.Conflict,
                StackFamily = TaskboardStackFamily.Unknown,
                Confidence = TaskboardBuildProfileConfidence.High,
                ResolutionReason = $"Taskboard auto-run paused: scoped work-item intent points to multiple stacks ({string.Join(", ", scopedFamilies.Select(FormatStackFamily))}).",
                SourceEvidence = scopedFamilies
                    .Select(family => new TaskboardBuildProfileEvidenceRecord
                    {
                        Source = TaskboardBuildProfileEvidenceSource.TaskboardIntent,
                        Code = "explicit_scoped_stack_conflict",
                        Value = FormatStackFamily(family),
                        Detail = "The current batch/work-item text contains multiple explicit stack markers."
                    })
                    .ToList()
            };
        }

        if (scopedFamilies.Count == 1)
        {
            var scopedStackFamily = scopedFamilies[0];
            return new TaskboardBuildProfileResolutionRecord
            {
                ResolutionId = Guid.NewGuid().ToString("N"),
                Status = TaskboardBuildProfileResolutionStatus.Resolved,
                StackFamily = scopedStackFamily,
                Language = InferLanguage(scopedStackFamily),
                Framework = InferFramework(scopedStackFamily, scopedText),
                UiShellKind = InferUiShellKind(scopedStackFamily, scopedText),
                Confidence = TaskboardBuildProfileConfidence.High,
                ResolutionReason = $"Resolved build profile from scoped work-item intent: `{FormatStackFamily(scopedStackFamily)}`.",
                SourceEvidence =
                [
                    new TaskboardBuildProfileEvidenceRecord
                    {
                        Source = TaskboardBuildProfileEvidenceSource.TaskboardIntent,
                        Code = "explicit_scoped_stack",
                        Value = FormatStackFamily(scopedStackFamily),
                        Detail = BuildEvidenceExcerpt(scopedText)
                    }
                ]
            };
        }

        var text = CollectTaskboardIntentText(document, batch, workItem);
        var families = DetectStackFamilies(text);
        if (families.Count > 1)
        {
            return new TaskboardBuildProfileResolutionRecord
            {
                ResolutionId = Guid.NewGuid().ToString("N"),
                Status = TaskboardBuildProfileResolutionStatus.Conflict,
                StackFamily = TaskboardStackFamily.Unknown,
                Confidence = TaskboardBuildProfileConfidence.High,
                ResolutionReason = $"Taskboard auto-run paused: explicit taskboard intent points to multiple stacks ({string.Join(", ", families.Select(FormatStackFamily))}).",
                SourceEvidence = families
                    .Select(family => new TaskboardBuildProfileEvidenceRecord
                    {
                        Source = TaskboardBuildProfileEvidenceSource.TaskboardIntent,
                        Code = "explicit_stack_conflict",
                        Value = FormatStackFamily(family),
                        Detail = "Multiple explicit taskboard stack markers were detected."
                    })
                    .ToList()
            };
        }

        if (families.Count == 0)
            return new TaskboardBuildProfileResolutionRecord();

        var stackFamily = families[0];
        return new TaskboardBuildProfileResolutionRecord
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            Status = TaskboardBuildProfileResolutionStatus.Resolved,
            StackFamily = stackFamily,
            Language = InferLanguage(stackFamily),
            Framework = InferFramework(stackFamily, text),
            UiShellKind = InferUiShellKind(stackFamily, text),
            Confidence = TaskboardBuildProfileConfidence.High,
            ResolutionReason = $"Resolved build profile from explicit taskboard intent: `{FormatStackFamily(stackFamily)}`.",
            SourceEvidence =
            [
                new TaskboardBuildProfileEvidenceRecord
                {
                    Source = TaskboardBuildProfileEvidenceSource.TaskboardIntent,
                    Code = "explicit_taskboard_stack",
                    Value = FormatStackFamily(stackFamily),
                    Detail = BuildEvidenceExcerpt(text)
                }
            ]
        };
    }

    private TaskboardBuildProfileResolutionRecord ResolveWorkspaceEvidence(
        string workspaceRoot,
        TaskboardDocument? document,
        TaskboardBatch? batch,
        TaskboardRunWorkItem workItem)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return new TaskboardBuildProfileResolutionRecord();

        var evidence = new List<TaskboardBuildProfileEvidenceRecord>();
        var candidateFamilies = new HashSet<TaskboardStackFamily>();
        var detection = _buildSystemDetectionService.Detect(workspaceRoot);
        if (detection.PreferredProfile is not null)
        {
            var mapped = MapBuildSystemToStackFamily(detection.PreferredProfile.BuildSystemType);
            if (mapped != TaskboardStackFamily.Unknown)
            {
                candidateFamilies.Add(mapped);
                evidence.Add(new TaskboardBuildProfileEvidenceRecord
                {
                    Source = TaskboardBuildProfileEvidenceSource.WorkspaceEvidence,
                    Code = "preferred_build_profile",
                    Value = NormalizeBuildSystemType(detection.PreferredProfile.BuildSystemType),
                    Detail = FirstNonEmpty(detection.PreferredProfile.PrimaryTargetPath, detection.PreferredProfile.BuildTargetPath)
                });
            }
        }

        if (WorkspaceContains(workspaceRoot, "*.xaml") || WorkspaceContains(workspaceRoot, "*.csproj") || WorkspaceContains(workspaceRoot, "*.sln"))
        {
            candidateFamilies.Add(TaskboardStackFamily.DotnetDesktop);
            evidence.Add(new TaskboardBuildProfileEvidenceRecord
            {
                Source = TaskboardBuildProfileEvidenceSource.WorkspaceEvidence,
                Code = "dotnet_workspace_files",
                Value = ".sln/.csproj/.xaml",
                Detail = "Workspace contains .NET desktop file indicators."
            });
        }

        if (WorkspaceContains(workspaceRoot, "CMakeLists.txt") || WorkspaceContains(workspaceRoot, "*.cpp") || WorkspaceContains(workspaceRoot, "*.h"))
        {
            candidateFamilies.Add(TaskboardStackFamily.NativeCppDesktop);
            evidence.Add(new TaskboardBuildProfileEvidenceRecord
            {
                Source = TaskboardBuildProfileEvidenceSource.WorkspaceEvidence,
                Code = "native_cpp_workspace_files",
                Value = "CMakeLists.txt/.cpp/.h",
                Detail = "Workspace contains native desktop file indicators."
            });
        }

        if (WorkspaceContains(workspaceRoot, "package.json"))
        {
            candidateFamilies.Add(TaskboardStackFamily.WebApp);
            evidence.Add(new TaskboardBuildProfileEvidenceRecord
            {
                Source = TaskboardBuildProfileEvidenceSource.WorkspaceEvidence,
                Code = "web_workspace_files",
                Value = "package.json",
                Detail = "Workspace contains a web app package manifest."
            });
        }

        if (WorkspaceContains(workspaceRoot, "Cargo.toml"))
        {
            candidateFamilies.Add(TaskboardStackFamily.RustApp);
            evidence.Add(new TaskboardBuildProfileEvidenceRecord
            {
                Source = TaskboardBuildProfileEvidenceSource.WorkspaceEvidence,
                Code = "rust_workspace_files",
                Value = "Cargo.toml",
                Detail = "Workspace contains a Rust package manifest."
            });
        }

        if (candidateFamilies.Count > 1)
        {
            return new TaskboardBuildProfileResolutionRecord
            {
                ResolutionId = Guid.NewGuid().ToString("N"),
                Status = TaskboardBuildProfileResolutionStatus.Conflict,
                StackFamily = TaskboardStackFamily.Unknown,
                Confidence = TaskboardBuildProfileConfidence.Medium,
                ResolutionReason = $"Workspace evidence points to multiple stacks ({string.Join(", ", candidateFamilies.Select(FormatStackFamily))}).",
                SourceEvidence = evidence
            };
        }

        if (candidateFamilies.Count == 0)
            return new TaskboardBuildProfileResolutionRecord();

        var family = candidateFamilies.First();
        var text = CollectTaskboardIntentText(document, batch, workItem);
        return new TaskboardBuildProfileResolutionRecord
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            Status = TaskboardBuildProfileResolutionStatus.Resolved,
            StackFamily = family,
            Language = InferLanguage(family),
            Framework = InferFramework(family, text),
            UiShellKind = InferUiShellKind(family, text),
            Confidence = detection.PreferredProfile is null
                ? TaskboardBuildProfileConfidence.Medium
                : TaskboardBuildProfileConfidence.High,
            ResolutionReason = $"Resolved build profile from workspace evidence: `{FormatStackFamily(family)}`.",
            SourceEvidence = evidence
        };
    }

    private static TaskboardBuildProfileResolutionRecord ResolveArtifactEvidence(string workspaceRoot, RamDbService? ramDbService)
    {
        if (ramDbService is null || string.IsNullOrWhiteSpace(workspaceRoot))
            return new TaskboardBuildProfileResolutionRecord();

        var state = ramDbService.LoadExecutionState(workspaceRoot);
        var family = ParsePersistedBuildProfile(state);
        if (family == TaskboardStackFamily.Unknown)
            return new TaskboardBuildProfileResolutionRecord();

        return new TaskboardBuildProfileResolutionRecord
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            Status = TaskboardBuildProfileResolutionStatus.Resolved,
            StackFamily = family,
            Language = InferLanguage(family),
            Framework = InferFrameworkFromPersistedState(state, family),
            UiShellKind = InferUiShellKindFromPersistedState(state, family),
            Confidence = TaskboardBuildProfileConfidence.Low,
            ResolutionReason = $"Resolved build profile from previously recorded workspace build state: `{FormatStackFamily(family)}`.",
            SourceEvidence =
            [
                new TaskboardBuildProfileEvidenceRecord
                {
                    Source = TaskboardBuildProfileEvidenceSource.ActiveArtifactEvidence,
                    Code = "last_selected_build_profile",
                    Value = FirstNonEmpty(state.LastSelectedBuildProfileType, state.LastDetectedBuildSystemType),
                    Detail = FirstNonEmpty(state.LastSelectedBuildProfileTargetPath, state.LastVerificationTargetPath, state.LastSuccessTargetPath)
                }
            ]
        };
    }

    private static TaskboardBuildProfileResolutionRecord MergeResolved(params TaskboardBuildProfileResolutionRecord[] records)
    {
        var resolved = records.FirstOrDefault(record => record.Status == TaskboardBuildProfileResolutionStatus.Resolved);
        if (resolved is null)
            return new TaskboardBuildProfileResolutionRecord();

        var merged = new TaskboardBuildProfileResolutionRecord
        {
            ResolutionId = resolved.ResolutionId,
            Status = TaskboardBuildProfileResolutionStatus.Resolved,
            StackFamily = resolved.StackFamily,
            Language = resolved.Language,
            Framework = resolved.Framework,
            UiShellKind = resolved.UiShellKind,
            Confidence = resolved.Confidence,
            ResolutionReason = resolved.ResolutionReason,
            AdvisoryUsed = records.Any(record => record.AdvisoryUsed),
            AdvisoryTraceId = records.FirstOrDefault(record => !string.IsNullOrWhiteSpace(record.AdvisoryTraceId))?.AdvisoryTraceId ?? ""
        };

        foreach (var record in records.Where(record => record.Status == TaskboardBuildProfileResolutionStatus.Resolved))
        {
            merged.SourceEvidence.AddRange(record.SourceEvidence);
            if (string.IsNullOrWhiteSpace(merged.Framework) && !string.IsNullOrWhiteSpace(record.Framework))
                merged.Framework = record.Framework;
            if (string.IsNullOrWhiteSpace(merged.UiShellKind) && !string.IsNullOrWhiteSpace(record.UiShellKind))
                merged.UiShellKind = record.UiShellKind;
            if (record.Confidence > merged.Confidence)
                merged.Confidence = record.Confidence;
        }

        return merged;
    }

    private static TaskboardBuildProfileResolutionRecord ResolveActiveRunEvidence(
        TaskboardPlanRunStateRecord? runState,
        TaskboardRunWorkItem workItem,
        TaskboardBuildProfileResolutionRecord workspaceEvidence,
        TaskboardBuildProfileResolutionRecord artifactEvidence)
    {
        if (runState is null)
            return new TaskboardBuildProfileResolutionRecord();

        var workItemStack = ParseStackFamily(workItem.TargetStack);
        var resolvedProfileStack = runState.LastResolvedBuildProfile.Status == TaskboardBuildProfileResolutionStatus.Resolved
            ? runState.LastResolvedBuildProfile.StackFamily
            : TaskboardStackFamily.Unknown;
        var lastCompletedStack = ParseStackFamily(runState.LastCompletedStackFamily);
        var lastFollowupStack = ParseStackFamily(runState.LastFollowupStackFamily);
        var lastBlockerStack = ParseStackFamily(runState.LastBlockerStackFamily);

        var activeStack = FirstMeaningfulStack(
            workItemStack,
            resolvedProfileStack,
            lastFollowupStack,
            lastCompletedStack,
            lastBlockerStack);
        if (activeStack == TaskboardStackFamily.Unknown)
            return new TaskboardBuildProfileResolutionRecord();

        var hasCommittedWorkItem =
            workItemStack == activeStack
            && (!string.IsNullOrWhiteSpace(workItem.OperationKind)
                || !string.IsNullOrWhiteSpace(workItem.PhraseFamily)
                || !string.IsNullOrWhiteSpace(workItem.WorkFamily));
        var hasResolvedProfile = resolvedProfileStack == activeStack;
        var hasCompletedEvidence = runState.CompletedWorkItemCount > 0 && lastCompletedStack == activeStack;
        var hasFollowupEvidence = lastFollowupStack == activeStack;
        var hasBlockerEvidence = lastBlockerStack == activeStack;
        var hasWorkspaceSupport = workspaceEvidence.Status == TaskboardBuildProfileResolutionStatus.Resolved
            && workspaceEvidence.StackFamily == activeStack;
        var hasArtifactSupport = artifactEvidence.Status == TaskboardBuildProfileResolutionStatus.Resolved
            && artifactEvidence.StackFamily == activeStack;

        var hasCommittedRunEvidence = hasResolvedProfile
                                      || hasCompletedEvidence
                                      || hasFollowupEvidence
                                      || hasBlockerEvidence;
        if (!hasCommittedRunEvidence
            || !(hasCommittedWorkItem
                 || hasResolvedProfile
                 || hasCompletedEvidence
                 || hasFollowupEvidence
                 || hasBlockerEvidence
                 || hasWorkspaceSupport
                 || hasArtifactSupport))
        {
            return new TaskboardBuildProfileResolutionRecord();
        }

        var confidence = hasCommittedWorkItem && (hasResolvedProfile || hasCompletedEvidence || hasWorkspaceSupport || hasArtifactSupport)
            ? TaskboardBuildProfileConfidence.High
            : TaskboardBuildProfileConfidence.Medium;

        var evidence = new List<TaskboardBuildProfileEvidenceRecord>();
        if (hasCommittedWorkItem)
        {
            evidence.Add(new TaskboardBuildProfileEvidenceRecord
            {
                Source = TaskboardBuildProfileEvidenceSource.ActiveArtifactEvidence,
                Code = "active_run_current_work_item_stack",
                Value = FormatStackFamily(activeStack),
                Detail = $"{FirstNonEmpty(workItem.Title, workItem.WorkItemId)}:{FirstNonEmpty(workItem.WorkFamily, workItem.OperationKind, workItem.PhraseFamily, "stack_committed")}"
            });
        }

        if (hasResolvedProfile)
        {
            evidence.Add(new TaskboardBuildProfileEvidenceRecord
            {
                Source = TaskboardBuildProfileEvidenceSource.ActiveArtifactEvidence,
                Code = "active_run_last_resolved_profile",
                Value = FormatStackFamily(activeStack),
                Detail = FirstNonEmpty(runState.LastResolvedBuildProfile.ResolutionReason, "active_run_profile")
            });
        }

        if (hasCompletedEvidence)
        {
            evidence.Add(new TaskboardBuildProfileEvidenceRecord
            {
                Source = TaskboardBuildProfileEvidenceSource.ActiveArtifactEvidence,
                Code = "active_run_last_completed_stack",
                Value = FormatStackFamily(activeStack),
                Detail = FirstNonEmpty(runState.LastCompletedWorkItemTitle, runState.LastCompletedWorkItemId, "last_completed")
            });
        }

        if (hasFollowupEvidence)
        {
            evidence.Add(new TaskboardBuildProfileEvidenceRecord
            {
                Source = TaskboardBuildProfileEvidenceSource.ActiveArtifactEvidence,
                Code = "active_run_followup_stack",
                Value = FormatStackFamily(activeStack),
                Detail = FirstNonEmpty(runState.LastFollowupWorkItemTitle, runState.LastFollowupWorkItemId, "followup")
            });
        }

        if (hasBlockerEvidence)
        {
            evidence.Add(new TaskboardBuildProfileEvidenceRecord
            {
                Source = TaskboardBuildProfileEvidenceSource.ActiveArtifactEvidence,
                Code = "active_run_blocker_stack",
                Value = FormatStackFamily(activeStack),
                Detail = FirstNonEmpty(runState.LastBlockerWorkItemTitle, runState.LastBlockerReason, "blocker")
            });
        }

        return new TaskboardBuildProfileResolutionRecord
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            Status = TaskboardBuildProfileResolutionStatus.Resolved,
            StackFamily = activeStack,
            Language = InferLanguage(activeStack),
            Framework = InferFrameworkFromActiveRun(runState, activeStack),
            UiShellKind = InferUiShellKindFromActiveRun(runState, activeStack),
            Confidence = confidence,
            ResolutionReason = $"Resolved build profile from committed active-run stack evidence: `{FormatStackFamily(activeStack)}`.",
            SourceEvidence = evidence
        };
    }

    private static bool CanPreferActiveRunEvidence(
        TaskboardBuildProfileResolutionRecord activeRunEvidence,
        TaskboardBuildProfileResolutionRecord workspaceEvidence,
        TaskboardBuildProfileResolutionRecord artifactEvidence)
    {
        if (activeRunEvidence.Status != TaskboardBuildProfileResolutionStatus.Resolved
            || activeRunEvidence.StackFamily == TaskboardStackFamily.Unknown)
        {
            return false;
        }

        if (activeRunEvidence.Confidence >= TaskboardBuildProfileConfidence.High)
            return true;

        return workspaceEvidence.Status == TaskboardBuildProfileResolutionStatus.Resolved
                   && workspaceEvidence.StackFamily == activeRunEvidence.StackFamily
               || artifactEvidence.Status == TaskboardBuildProfileResolutionStatus.Resolved
                   && artifactEvidence.StackFamily == activeRunEvidence.StackFamily;
    }

    private static IEnumerable<TaskboardBuildProfileResolutionRecord> GetSupportingResolvedEvidence(
        TaskboardStackFamily stackFamily,
        params TaskboardBuildProfileResolutionRecord[] records)
    {
        return records.Where(record =>
            record.Status == TaskboardBuildProfileResolutionStatus.Resolved
            && record.StackFamily == stackFamily);
    }

    private static TaskboardBuildProfileResolutionRecord MergeResolvedWithSupport(
        TaskboardBuildProfileResolutionRecord primary,
        params TaskboardBuildProfileResolutionRecord[] supportingRecords)
    {
        return MergeResolved(
            [primary, .. GetSupportingResolvedEvidence(primary.StackFamily, supportingRecords)]);
    }

    private static TaskboardBuildProfileResolutionRecord BuildConflict(
        TaskboardBuildProfileResolutionRecord first,
        TaskboardBuildProfileResolutionRecord second,
        string reason)
    {
        return new TaskboardBuildProfileResolutionRecord
        {
            ResolutionId = Guid.NewGuid().ToString("N"),
            Status = TaskboardBuildProfileResolutionStatus.Conflict,
            StackFamily = TaskboardStackFamily.Unknown,
            Confidence = TaskboardBuildProfileConfidence.High,
            ResolutionReason = reason,
            SourceEvidence =
            [
                .. first.SourceEvidence,
                .. second.SourceEvidence
            ]
        };
    }

    private static List<TaskboardStackFamily> DetectStackFamilies(string text)
    {
        var families = new HashSet<TaskboardStackFamily>();
        if (ContainsAny(text, "wpf", "windows app sdk", "xaml", ".net", "dotnet", "c#", "csproj", "sln", "winui"))
            families.Add(TaskboardStackFamily.DotnetDesktop);
        if (ContainsAny(text, "c++", "cpp", "win32", "cmake", "native cpp", "native c++", "makefile", "build.ninja"))
            families.Add(TaskboardStackFamily.NativeCppDesktop);
        if (ContainsWebAppIntent(text))
            families.Add(TaskboardStackFamily.WebApp);
        if (ContainsAny(text, "rust", "cargo", "cargo.toml", "tauri", "iced", "egui"))
            families.Add(TaskboardStackFamily.RustApp);
        return families.ToList();
    }

    private static string CollectTaskboardIntentText(TaskboardDocument? document, TaskboardBatch? batch, TaskboardRunWorkItem workItem)
    {
        var lines = new List<string>();
        if (document is not null)
        {
            lines.Add(document.Title);
            lines.Add(document.ObjectiveText);
            foreach (var section in document.AdditionalSections)
                lines.AddRange(EnumerateSectionLines(section));
            foreach (var bucket in document.Guardrails.Buckets)
                lines.AddRange(EnumerateSectionLines(bucket));
        }

        if (batch is not null)
        {
            lines.Add(batch.Title);
            lines.AddRange(EnumerateSectionLines(batch.Content));
        }

        lines.Add(workItem.Title);
        lines.Add(workItem.Summary);
        lines.Add(workItem.PromptText);
        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line))).ToLowerInvariant();
    }

    private static string CollectScopedTaskboardIntentText(TaskboardBatch? batch, TaskboardRunWorkItem workItem)
    {
        var lines = new List<string>();
        if (batch is not null)
        {
            lines.Add(batch.Title);
            lines.AddRange(EnumerateSectionLines(batch.Content));
        }

        lines.Add(workItem.Title);
        lines.Add(workItem.Summary);
        lines.Add(workItem.PromptText);
        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line))).ToLowerInvariant();
    }

    private static string BuildObjectiveExcerpt(TaskboardDocument? document)
    {
        var objective = document?.ObjectiveText ?? "";
        objective = objective.Replace(Environment.NewLine, " ").Trim();
        return objective.Length <= 220 ? objective : objective[..220];
    }

    private static string BuildEvidenceExcerpt(string text)
    {
        var compact = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return compact.Length <= 160 ? compact : compact[..160];
    }

    private static List<string> ResolveWorkspaceEvidenceLines(string workspaceRoot)
    {
        var lines = new List<string>();
        if (WorkspaceContains(workspaceRoot, "*.sln"))
            lines.Add("workspace_has_sln");
        if (WorkspaceContains(workspaceRoot, "*.csproj"))
            lines.Add("workspace_has_csproj");
        if (WorkspaceContains(workspaceRoot, "*.xaml"))
            lines.Add("workspace_has_xaml");
        if (WorkspaceContains(workspaceRoot, "CMakeLists.txt"))
            lines.Add("workspace_has_cmake");
        if (WorkspaceContains(workspaceRoot, "*.cpp"))
            lines.Add("workspace_has_cpp");
        if (WorkspaceContains(workspaceRoot, "package.json"))
            lines.Add("workspace_has_package_json");
        if (WorkspaceContains(workspaceRoot, "Cargo.toml"))
            lines.Add("workspace_has_cargo_toml");
        return lines;
    }

    private static TaskboardStackFamily MapBuildSystemToStackFamily(BuildSystemType buildSystemType)
    {
        return buildSystemType switch
        {
            BuildSystemType.Dotnet => TaskboardStackFamily.DotnetDesktop,
            BuildSystemType.CMake or BuildSystemType.Make or BuildSystemType.Ninja => TaskboardStackFamily.NativeCppDesktop,
            _ => TaskboardStackFamily.Unknown
        };
    }

    private static TaskboardStackFamily ParsePersistedBuildProfile(WorkspaceExecutionStateRecord state)
    {
        var type = FirstNonEmpty(state.LastSelectedBuildProfileType, state.LastDetectedBuildSystemType).ToLowerInvariant();
        return type switch
        {
            "dotnet" => TaskboardStackFamily.DotnetDesktop,
            "cmake" or "make" or "ninja" => TaskboardStackFamily.NativeCppDesktop,
            _ => TaskboardStackFamily.Unknown
        };
    }

    private static string InferFramework(TaskboardStackFamily family, string text)
    {
        return family switch
        {
            TaskboardStackFamily.DotnetDesktop when text.Contains("wpf", StringComparison.OrdinalIgnoreCase) => "wpf",
            TaskboardStackFamily.DotnetDesktop when text.Contains("windows app sdk", StringComparison.OrdinalIgnoreCase) => "windows_app_sdk",
            TaskboardStackFamily.DotnetDesktop => "dotnet_desktop",
            TaskboardStackFamily.NativeCppDesktop when text.Contains("win32", StringComparison.OrdinalIgnoreCase) => "win32",
            TaskboardStackFamily.NativeCppDesktop => "cmake",
            TaskboardStackFamily.WebApp when text.Contains("react", StringComparison.OrdinalIgnoreCase) => "react",
            TaskboardStackFamily.WebApp => "web_app",
            TaskboardStackFamily.RustApp => "cargo",
            _ => ""
        };
    }

    private static string InferFrameworkFromPersistedState(WorkspaceExecutionStateRecord state, TaskboardStackFamily family)
    {
        return family switch
        {
            TaskboardStackFamily.DotnetDesktop => "dotnet_desktop",
            TaskboardStackFamily.NativeCppDesktop => FirstNonEmpty(state.LastSelectedBuildProfileType, "cmake"),
            _ => ""
        };
    }

    private static string InferFrameworkFromActiveRun(TaskboardPlanRunStateRecord runState, TaskboardStackFamily family)
    {
        if (!string.IsNullOrWhiteSpace(runState.LastResolvedBuildProfile.Framework))
            return runState.LastResolvedBuildProfile.Framework;

        return family switch
        {
            TaskboardStackFamily.DotnetDesktop => "dotnet_desktop",
            TaskboardStackFamily.NativeCppDesktop => "cmake",
            TaskboardStackFamily.RustApp => "cargo",
            TaskboardStackFamily.WebApp => "web_app",
            _ => ""
        };
    }

    private static string InferUiShellKind(TaskboardStackFamily family, string text)
    {
        return family switch
        {
            TaskboardStackFamily.DotnetDesktop when text.Contains("wpf", StringComparison.OrdinalIgnoreCase) => "wpf_shell",
            TaskboardStackFamily.DotnetDesktop => "desktop_shell",
            TaskboardStackFamily.NativeCppDesktop when text.Contains("win32", StringComparison.OrdinalIgnoreCase) => "win32_window",
            TaskboardStackFamily.NativeCppDesktop => "native_window",
            TaskboardStackFamily.WebApp => "web_shell",
            TaskboardStackFamily.RustApp => "rust_shell",
            _ => ""
        };
    }

    private static string InferUiShellKindFromPersistedState(WorkspaceExecutionStateRecord state, TaskboardStackFamily family)
    {
        return family switch
        {
            TaskboardStackFamily.DotnetDesktop => "desktop_shell",
            TaskboardStackFamily.NativeCppDesktop => "native_window",
            _ => ""
        };
    }

    private static string InferUiShellKindFromActiveRun(TaskboardPlanRunStateRecord runState, TaskboardStackFamily family)
    {
        if (!string.IsNullOrWhiteSpace(runState.LastResolvedBuildProfile.UiShellKind))
            return runState.LastResolvedBuildProfile.UiShellKind;

        return family switch
        {
            TaskboardStackFamily.DotnetDesktop => "desktop_shell",
            TaskboardStackFamily.NativeCppDesktop => "native_window",
            TaskboardStackFamily.WebApp => "web_shell",
            TaskboardStackFamily.RustApp => "rust_shell",
            _ => ""
        };
    }

    private static string InferLanguage(TaskboardStackFamily family)
    {
        return family switch
        {
            TaskboardStackFamily.DotnetDesktop => "csharp",
            TaskboardStackFamily.NativeCppDesktop => "c++",
            TaskboardStackFamily.WebApp => "typescript",
            TaskboardStackFamily.RustApp => "rust",
            _ => ""
        };
    }

    private static TaskboardBuildProfileConfidence ParseConfidence(string value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "high" => TaskboardBuildProfileConfidence.High,
            "medium" => TaskboardBuildProfileConfidence.Medium,
            "low" => TaskboardBuildProfileConfidence.Low,
            _ => TaskboardBuildProfileConfidence.None
        };
    }

    private static TaskboardStackFamily ParseStackFamily(string value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "dotnet_desktop" => TaskboardStackFamily.DotnetDesktop,
            "native_cpp_desktop" => TaskboardStackFamily.NativeCppDesktop,
            "web_app" => TaskboardStackFamily.WebApp,
            "rust_app" => TaskboardStackFamily.RustApp,
            _ => TaskboardStackFamily.Unknown
        };
    }

    private static TaskboardStackFamily FirstMeaningfulStack(params TaskboardStackFamily[] values)
    {
        foreach (var value in values)
        {
            if (value != TaskboardStackFamily.Unknown)
                return value;
        }

        return TaskboardStackFamily.Unknown;
    }

    private static List<string> MergeMissingEvidence(params string[] values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool WorkspaceContains(string workspaceRoot, string pattern)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return false;

        if (!pattern.Contains('*'))
        {
            return Directory.EnumerateFiles(workspaceRoot, pattern, SearchOption.AllDirectories)
                .Any(path => !IsIgnoredPath(path));
        }

        return Directory.EnumerateFiles(workspaceRoot, pattern, SearchOption.AllDirectories)
            .Any(path => !IsIgnoredPath(path));
    }

    private static bool IsIgnoredPath(string path)
    {
        var parts = (path ?? "").Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part =>
            string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase)
            || string.Equals(part, ".ram", StringComparison.OrdinalIgnoreCase)
            || string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(part, "node_modules", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        foreach (var value in values)
        {
            if (text.Contains(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool ContainsWebAppIntent(string text)
    {
        if (ContainsAny(text, "electron", "package.json", "web app", "frontend"))
            return true;

        if (Regex.IsMatch(text, @"\breact\b(?!\s+to\b)", RegexOptions.IgnoreCase))
            return true;

        return Regex.IsMatch(text, @"\brouter\b", RegexOptions.IgnoreCase);
    }

    private static string NormalizeBuildSystemType(BuildSystemType buildSystemType)
    {
        return buildSystemType.ToString().ToLowerInvariant();
    }

    private static string FormatStackFamily(TaskboardStackFamily family)
    {
        return family switch
        {
            TaskboardStackFamily.DotnetDesktop => "dotnet_desktop",
            TaskboardStackFamily.NativeCppDesktop => "native_cpp_desktop",
            TaskboardStackFamily.WebApp => "web_app",
            TaskboardStackFamily.RustApp => "rust_app",
            _ => "unknown"
        };
    }

    private static string FormatEvidenceSource(TaskboardBuildProfileEvidenceSource source)
    {
        return source.ToString().ToLowerInvariant();
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

    private static IEnumerable<string> EnumerateSectionLines(TaskboardSectionContent section)
    {
        foreach (var paragraph in section.Paragraphs)
            yield return paragraph;
        foreach (var item in section.BulletItems)
            yield return item;
        foreach (var item in section.NumberedItems)
            yield return item;
        foreach (var subsection in section.Subsections)
        {
            foreach (var line in EnumerateSectionLines(subsection))
                yield return line;
        }
    }
}
