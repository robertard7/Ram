using RAM.Models;

namespace RAM.Services;

public sealed class RamDataDisciplineService
{
    private static readonly IReadOnlyList<RamDataCategoryDefinitionRecord> Categories =
    [
        new RamDataCategoryDefinitionRecord
        {
            CategoryKey = "run_summary",
            Description = "Terminal run summaries and normalized run truth used for retrieval and handoff.",
            IsDurableTruth = true,
            DefaultRetentionClass = "warm",
            ExampleRecordType = "taskboard_run_summary"
        },
        new RamDataCategoryDefinitionRecord
        {
            CategoryKey = "taskboard_metadata",
            Description = "Imported taskboard plans, validation reports, and activation metadata.",
            IsDurableTruth = true,
            DefaultRetentionClass = "warm",
            ExampleRecordType = "taskboard_plan"
        },
        new RamDataCategoryDefinitionRecord
        {
            CategoryKey = "work_item_lineage",
            Description = "Decomposition, follow-up, blocker, and work-item lineage records.",
            IsDurableTruth = true,
            DefaultRetentionClass = "warm",
            ExampleRecordType = "taskboard_followup_resolution"
        },
        new RamDataCategoryDefinitionRecord
        {
            CategoryKey = "lane_chain_decision_record",
            Description = "Execution goal, lane selection, coverage, and controlled-chain contract truth.",
            IsDurableTruth = true,
            DefaultRetentionClass = "warm",
            ExampleRecordType = "taskboard_execution_lane"
        },
        new RamDataCategoryDefinitionRecord
        {
            CategoryKey = "tool_execution_record",
            Description = "Executed chain records and compact execution outputs used for auditing runtime behavior.",
            IsDurableTruth = true,
            DefaultRetentionClass = "hot",
            ExampleRecordType = "tool_chain_record"
        },
        new RamDataCategoryDefinitionRecord
        {
            CategoryKey = "verification_outcome",
            Description = "Build, test, validation, and repair verification outcomes.",
            IsDurableTruth = true,
            DefaultRetentionClass = "warm",
            ExampleRecordType = "build_result"
        },
        new RamDataCategoryDefinitionRecord
        {
            CategoryKey = "repair_context",
            Description = "Failure context, repair plans, patch drafts, apply results, and repair closures.",
            IsDurableTruth = true,
            DefaultRetentionClass = "warm",
            ExampleRecordType = "repair_context"
        },
        new RamDataCategoryDefinitionRecord
        {
            CategoryKey = "file_touch_record",
            Description = "Per-run file touch history and repeated-touch analytics inputs.",
            IsDurableTruth = true,
            DefaultRetentionClass = "warm",
            ExampleRecordType = "file_touch_record"
        },
        new RamDataCategoryDefinitionRecord
        {
            CategoryKey = "state_satisfaction_record",
            Description = "Deterministic already-satisfied checks, skip decisions, and invalidation-aware fast-path records.",
            IsDurableTruth = true,
            DefaultRetentionClass = "warm",
            ExampleRecordType = "taskboard_skip_record"
        },
        new RamDataCategoryDefinitionRecord
        {
            CategoryKey = "retrieval_context",
            Description = "Deterministic retrieval queries, results, context packets, and index batches that inform bounded maintenance planning.",
            IsDurableTruth = true,
            DefaultRetentionClass = "warm",
            ExampleRecordType = "coder_context_packet"
        },
        new RamDataCategoryDefinitionRecord
        {
            CategoryKey = "artifact_reference",
            Description = "Reference-first links to generated files and key artifacts without payload duplication.",
            IsDurableTruth = true,
            DefaultRetentionClass = "warm",
            ExampleRecordType = "artifact_reference"
        },
        new RamDataCategoryDefinitionRecord
        {
            CategoryKey = "raw_transient_log",
            Description = "Raw imports, projections, live-run entries, and other debug-heavy transient material.",
            IsTransientDebugNoise = true,
            DefaultRetentionClass = "hot",
            ExampleRecordType = "taskboard_run_projection"
        },
        new RamDataCategoryDefinitionRecord
        {
            CategoryKey = "stale_or_superseded",
            Description = "Superseded or archival-only records retained for audit but not used as current truth.",
            IsArchiveOnly = true,
            DefaultRetentionClass = "cold",
            ExampleRecordType = "superseded_record"
        }
    ];

    private static readonly IReadOnlyList<RamRetentionRuleDefinitionRecord> RetentionRules =
    [
        new RamRetentionRuleDefinitionRecord
        {
            CategoryKey = "run_summary",
            RetentionClass = "warm",
            LifecycleRule = "Keep compact normalized truth and summaries long-term for retrieval and training-corpus preparation.",
            KeepFullPayload = true
        },
        new RamRetentionRuleDefinitionRecord
        {
            CategoryKey = "taskboard_metadata",
            RetentionClass = "warm",
            LifecycleRule = "Keep canonical taskboard source metadata and validation state for later replay and audit.",
            KeepFullPayload = true
        },
        new RamRetentionRuleDefinitionRecord
        {
            CategoryKey = "work_item_lineage",
            RetentionClass = "warm",
            LifecycleRule = "Keep lineage and blocker records because they explain how the run moved from one work item to the next.",
            KeepFullPayload = true
        },
        new RamRetentionRuleDefinitionRecord
        {
            CategoryKey = "lane_chain_decision_record",
            RetentionClass = "warm",
            LifecycleRule = "Keep lane and chain decisions compactly for later retrieval and policy debugging.",
            KeepFullPayload = true
        },
        new RamRetentionRuleDefinitionRecord
        {
            CategoryKey = "tool_execution_record",
            RetentionClass = "hot",
            LifecycleRule = "Keep recent full execution traces hot, then compact them into normalized run truth.",
            KeepFullPayload = true,
            EligibleForCompaction = true
        },
        new RamRetentionRuleDefinitionRecord
        {
            CategoryKey = "verification_outcome",
            RetentionClass = "warm",
            LifecycleRule = "Keep verification and build outcomes because they anchor terminal truth and repair history.",
            KeepFullPayload = true
        },
        new RamRetentionRuleDefinitionRecord
        {
            CategoryKey = "repair_context",
            RetentionClass = "warm",
            LifecycleRule = "Keep compact repair context and closures; archive large raw logs separately.",
            KeepFullPayload = true
        },
        new RamRetentionRuleDefinitionRecord
        {
            CategoryKey = "file_touch_record",
            RetentionClass = "warm",
            LifecycleRule = "Keep raw per-run touch history and compute rollups for repeated-touch analysis.",
            KeepFullPayload = true
        },
        new RamRetentionRuleDefinitionRecord
        {
            CategoryKey = "state_satisfaction_record",
            RetentionClass = "warm",
            LifecycleRule = "Keep deterministic skip decisions and invalidation-aware satisfaction checks for later replay, analytics, and training-corpus preparation.",
            KeepFullPayload = true
        },
        new RamRetentionRuleDefinitionRecord
        {
            CategoryKey = "retrieval_context",
            RetentionClass = "warm",
            LifecycleRule = "Keep retrieval queries, results, and context packets compactly so maintenance runs can be replayed and audited later.",
            KeepFullPayload = true
        },
        new RamRetentionRuleDefinitionRecord
        {
            CategoryKey = "artifact_reference",
            RetentionClass = "warm",
            LifecycleRule = "Keep reference metadata warm while leaving raw artifact bodies in place for debugging.",
            KeepFullPayload = false
        },
        new RamRetentionRuleDefinitionRecord
        {
            CategoryKey = "raw_transient_log",
            RetentionClass = "hot",
            LifecycleRule = "Keep short-term for active debugging, then compact or archive once normalized truth exists.",
            KeepFullPayload = true,
            EligibleForCompaction = true
        },
        new RamRetentionRuleDefinitionRecord
        {
            CategoryKey = "stale_or_superseded",
            RetentionClass = "cold",
            LifecycleRule = "Keep only for audit and archive; do not use as current truth.",
            EligibleForCompaction = true
        }
    ];

    public IReadOnlyList<RamDataCategoryDefinitionRecord> GetCategoryDefinitions()
    {
        return Categories;
    }

    public IReadOnlyList<RamRetentionRuleDefinitionRecord> GetRetentionRules()
    {
        return RetentionRules;
    }

    public string ResolveArtifactDataCategory(ArtifactRecord artifact)
    {
        var artifactType = Normalize(artifact.ArtifactType);
        var relativePath = Normalize(artifact.RelativePath);

        if (artifactType is "taskboard_run_summary" or "taskboard_normalized_run" or "taskboard_index_export" or "taskboard_corpus_export")
            return "run_summary";

        if (artifactType is "taskboard_import" or "taskboard_plan" or "taskboard_validation" or "taskboard_parsed" or "taskboard_activation_handoff")
            return "taskboard_metadata";

        if (artifactType is "taskboard_decomposition"
            or "taskboard_post_chain_reconciliation"
            or "taskboard_followup_work_item"
            or "taskboard_followup_resolution"
            or "taskboard_final_blocker_assignment"
            or "taskboard_run_state")
        {
            return "work_item_lineage";
        }

        if (artifactType is "taskboard_phrase_family_resolution"
            or "taskboard_command_normalization"
            or "taskboard_execution_goal"
            or "taskboard_execution_lane"
            or "taskboard_lane_coverage_map"
            or "taskboard_chain_contract")
        {
            return "lane_chain_decision_record";
        }

        if (artifactType is "tool_chain_record" or "tool_chain_summary" or "taskboard_chain_rejection")
            return "tool_execution_record";

        if (artifactType is "build_result"
            or "configure_result"
            or "verification_plan"
            or "verification_result"
            or "auto_validation_plan"
            or "auto_validation_result"
            or "repair_loop_closure"
            or "build_scope_block"
            or "execution_safety_result")
        {
            return "verification_outcome";
        }

        if (artifactType is "repair_context"
            or "repair_proposal"
            or "csharp_patch_contract"
            or "csharp_patch_plan"
            or "patch_draft"
            or "patch_apply_result"
            or "build_failure_summary"
            or "test_failure_summary")
        {
            return "repair_context";
        }

        if (artifactType is "retrieval_index_batch"
            or "retrieval_query_packet"
            or "retrieval_result"
            or "coder_context_packet"
            or "workspace_snapshot"
            or "workspace_project_graph"
            or "workspace_preparation_state"
            or "workspace_retrieval_catalog"
            or "workspace_retrieval_delta"
            or "workspace_retrieval_sync_result")
        {
            return "retrieval_context";
        }

        if (artifactType is "taskboard_raw" or "taskboard_run_projection" or "taskboard_live_run_entry")
            return "raw_transient_log";

        if (!string.IsNullOrWhiteSpace(relativePath) && !relativePath.StartsWith(".ram/", StringComparison.OrdinalIgnoreCase))
            return "artifact_reference";

        return "raw_transient_log";
    }

    public string ResolveArtifactRetentionClass(ArtifactRecord artifact)
    {
        var category = ResolveArtifactDataCategory(artifact);
        var rule = RetentionRules.FirstOrDefault(current =>
            string.Equals(current.CategoryKey, category, StringComparison.OrdinalIgnoreCase));
        return rule?.RetentionClass ?? "warm";
    }

    public string ResolveArtifactLifecycleState(ArtifactRecord artifact)
    {
        var category = ResolveArtifactDataCategory(artifact);
        if (string.Equals(category, "repair_context", StringComparison.OrdinalIgnoreCase))
            return "repair_context";
        if (string.Equals(category, "retrieval_context", StringComparison.OrdinalIgnoreCase))
            return "current";
        if (string.Equals(category, "raw_transient_log", StringComparison.OrdinalIgnoreCase))
            return "raw_log";
        if (string.Equals(category, "run_summary", StringComparison.OrdinalIgnoreCase))
            return "summary_linked";

        return "current";
    }

    public string ResolveRecencyLabel(DateTime createdUtc)
    {
        var age = DateTime.UtcNow - createdUtc.ToUniversalTime();
        if (age.TotalHours <= 12)
            return "current";
        if (age.TotalDays <= 14)
            return "recent";

        return "historical";
    }

    public string ResolveTrustLabel(string lifecycleState)
    {
        return Normalize(lifecycleState) switch
        {
            "superseded" or "raw_log" => "stale_superseded",
            "current" or "repair_context" or "summary_linked" => "current_truth",
            _ => "historical_truth"
        };
    }

    private static string Normalize(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant();
    }
}
