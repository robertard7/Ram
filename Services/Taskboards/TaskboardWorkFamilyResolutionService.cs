using RAM.Models;

namespace RAM.Services;

public sealed class TaskboardWorkFamilyResolutionService
{
    private static readonly Dictionary<string, string> OperationFamilyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["create_solution"] = "solution_scaffold",
        ["create_project"] = "solution_scaffold",
        ["create_test_project"] = "check_runner",
        ["create_core_library"] = "core_domain_models_contracts",
        ["add_project_to_solution"] = "solution_scaffold",
        ["attach_test_project"] = "check_runner",
        ["attach_core_library"] = "core_domain_models_contracts",
        ["add_domain_reference"] = "core_domain_models_contracts",
        ["add_project_reference"] = "solution_scaffold",
        ["shell_section_create"] = "ui_shell_sections",
        ["shell_behavior_extend"] = "ui_shell_sections",
        ["shell_layout_update"] = "ui_shell_sections",
        ["write_shell_layout"] = "ui_shell_sections",
        ["write_page"] = "ui_shell_sections",
        ["write_dashboard_panel"] = "ui_shell_sections",
        ["write_findings_panel"] = "ui_shell_sections",
        ["write_history_panel"] = "ui_shell_sections",
        ["write_settings_panel"] = "ui_shell_sections",
        ["write_app_window_header"] = "ui_shell_sections",
        ["write_app_window_source"] = "ui_shell_sections",
        ["write_main_cpp"] = "ui_shell_sections",
        ["write_navigation_item"] = "ui_wiring",
        ["write_shell_registration"] = "ui_wiring",
        ["write_navigation_header"] = "ui_wiring",
        ["write_app_state"] = "app_state_wiring",
        ["write_app_state_header"] = "app_state_wiring",
        ["write_shell_viewmodel"] = "viewmodel_scaffold",
        ["write_storage_contract"] = "storage_bootstrap",
        ["write_storage_impl"] = "storage_bootstrap",
        ["inspect_context_artifacts"] = "maintenance_context",
        ["write_repository_contract"] = "repository_scaffold",
        ["write_repository_impl"] = "repository_scaffold",
        ["write_storage_header"] = "storage_bootstrap",
        ["write_storage_source"] = "storage_bootstrap",
        ["write_contract_file"] = "core_domain_models_contracts",
        ["write_domain_model_file"] = "core_domain_models_contracts",
        ["write_contract_header"] = "repository_scaffold",
        ["write_domain_model_header"] = "repository_scaffold",
        ["write_check_registry"] = "findings_pipeline",
        ["write_snapshot_builder"] = "findings_pipeline",
        ["write_findings_normalizer"] = "findings_pipeline",
        ["run_test_project"] = "check_runner",
        ["build_solution"] = "build_verify",
        ["build_native_workspace"] = "build_verify",
        ["configure_cmake"] = "build_verify",
        ["inspect_solution_wiring"] = "build_repair",
        ["inspect_project_reference_graph"] = "build_repair",
        ["repair_project_attachment"] = "build_repair",
        ["repair_generated_build_targets"] = "build_repair",
        ["make_state_dir"] = "app_state_wiring",
        ["make_storage_dir"] = "storage_bootstrap",
        ["make_contracts_dir"] = "core_domain_models_contracts",
        ["make_models_dir"] = "core_domain_models_contracts",
        ["make_src_dir"] = "native_project_bootstrap",
        ["make_include_dir"] = "native_project_bootstrap",
        ["write_cmake_lists"] = "native_project_bootstrap"
    };

    private static readonly Dictionary<string, string> PhraseFamilyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["solution_scaffold"] = "solution_scaffold",
        ["project_scaffold"] = "solution_scaffold",
        ["native_project_bootstrap"] = "native_project_bootstrap",
        ["cmake_bootstrap"] = "native_project_bootstrap",
        ["build_first_ui_shell"] = "ui_shell_sections",
        ["ui_shell_sections"] = "ui_shell_sections",
        ["add_navigation_app_state"] = "app_state_wiring",
        ["setup_storage_layer"] = "storage_bootstrap",
        ["core_domain_models_contracts"] = "core_domain_models_contracts",
        ["repository_scaffold"] = "repository_scaffold",
        ["maintenance_context"] = "maintenance_context",
        ["add_settings_page"] = "ui_shell_sections",
        ["add_history_log_view"] = "ui_shell_sections",
        ["wire_dashboard"] = "ui_shell_sections",
        ["check_runner"] = "check_runner",
        ["findings_pipeline"] = "findings_pipeline",
        ["build_verify"] = "build_verify",
        ["build_failure_repair"] = "build_repair",
        ["solution_graph_repair"] = "build_repair"
    };

    private static readonly Dictionary<string, string> TemplateFamilyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dotnet.shell_page_set_scaffold.v1"] = "ui_shell_sections",
        ["dotnet.desktop_shell_scaffold.v1"] = "ui_shell_sections",
        ["dotnet.navigation_wireup.v1"] = "app_state_wiring",
        ["dotnet.shell_registration_wireup.v1"] = "ui_wiring",
        ["dotnet.page_and_viewmodel_scaffold.v1"] = "viewmodel_scaffold",
        ["dotnet.repository_scaffold.v1"] = "repository_scaffold",
        ["dotnet.findings_pipeline_bootstrap.v1"] = "findings_pipeline",
        ["dotnet.sqlite_storage_bootstrap.v1"] = "storage_bootstrap",
        ["dotnet.domain_contracts_scaffold.v1"] = "core_domain_models_contracts",
        ["artifact_inspection_single_step"] = "maintenance_context",
        ["dotnet.check_runner_scaffold.v1"] = "check_runner",
        ["workspace.build_verify.v1"] = "build_verify",
        ["repair_preview_chain"] = "build_repair",
        ["repair_execution_chain"] = "build_repair",
        ["repair_single_step"] = "build_repair",
        ["cmake.project_bootstrap.v1"] = "native_project_bootstrap",
        ["cpp.win32_shell_scaffold.v1"] = "ui_shell_sections",
        ["cpp.win32_shell_page_set.v1"] = "ui_shell_sections",
        ["cpp.library_scaffold.v1"] = "repository_scaffold",
        ["workspace.native_build_verify.v1"] = "build_verify"
    };

    public TaskboardWorkFamilyResolution Resolve(TaskboardRunWorkItem workItem)
    {
        var candidates = new List<string>();
        var explicitWorkFamily = NormalizeValue(workItem.WorkFamily);
        var operationKind = NormalizeValue(workItem.OperationKind);
        var phraseFamily = NormalizeValue(workItem.PhraseFamily);
        var templateId = NormalizeValue(workItem.TemplateId);
        var stackFamily = NormalizeValue(workItem.TargetStack);

        if (ShouldNormalizeTestSupportWriteFamily(operationKind, explicitWorkFamily))
        {
            candidates.Add("findings_pipeline");
            return BuildResolution(
                "findings_pipeline",
                TaskboardWorkFamilyResolutionSource.OperationKind,
                stackFamily,
                phraseFamily,
                operationKind,
                candidates,
                $"Normalized stale work family `{explicitWorkFamily}` to `findings_pipeline` for test-support write operation `{operationKind}`.");
        }

        if (ShouldNormalizeDomainContractsFamily(workItem, operationKind, explicitWorkFamily, phraseFamily, templateId))
        {
            candidates.Add("core_domain_models_contracts");
            return BuildResolution(
                "core_domain_models_contracts",
                TaskboardWorkFamilyResolutionSource.OperationKind,
                stackFamily,
                phraseFamily,
                operationKind,
                candidates,
                "Normalized secondary-project core/contracts work to `core_domain_models_contracts` so create -> attach -> reference continuity keeps the authoritative core-library identity.");
        }

        if (!string.IsNullOrWhiteSpace(explicitWorkFamily))
        {
            candidates.Add(explicitWorkFamily);
            return BuildResolution(explicitWorkFamily, TaskboardWorkFamilyResolutionSource.OperationKind, stackFamily, phraseFamily, operationKind, candidates, $"Resolved work family `{explicitWorkFamily}` from existing runtime work item state.");
        }

        if (!string.IsNullOrWhiteSpace(operationKind)
            && OperationFamilyMap.TryGetValue(operationKind, out var operationFamily))
        {
            candidates.Add(operationFamily);
        }

        if (!string.IsNullOrWhiteSpace(phraseFamily)
            && PhraseFamilyMap.TryGetValue(phraseFamily, out var phraseFamilyId)
            && !candidates.Contains(phraseFamilyId, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(phraseFamilyId);
        }

        if (!string.IsNullOrWhiteSpace(templateId)
            && TemplateFamilyMap.TryGetValue(templateId, out var templateFamily)
            && !candidates.Contains(templateFamily, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(templateFamily);
        }

        if (candidates.Count == 0)
        {
            var fallback = ResolveStackFallbackFamily(stackFamily, workItem);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                candidates.Add(fallback);
                return BuildResolution(fallback, TaskboardWorkFamilyResolutionSource.StackFallback, stackFamily, phraseFamily, operationKind, candidates, $"Resolved work family `{fallback}` from stack-aware fallback.");
            }

            return new TaskboardWorkFamilyResolution
            {
                StackFamily = stackFamily,
                PhraseFamily = phraseFamily,
                OperationKind = operationKind
            };
        }

        if (!string.IsNullOrWhiteSpace(operationKind) && OperationFamilyMap.TryGetValue(operationKind, out var directOperationFamily))
        {
            return BuildResolution(directOperationFamily, TaskboardWorkFamilyResolutionSource.OperationKind, stackFamily, phraseFamily, operationKind, candidates, $"Resolved work family `{directOperationFamily}` from operation kind `{operationKind}`.");
        }

        if (!string.IsNullOrWhiteSpace(phraseFamily) && PhraseFamilyMap.TryGetValue(phraseFamily, out var directPhraseFamily))
        {
            return BuildResolution(directPhraseFamily, TaskboardWorkFamilyResolutionSource.PhraseFamily, stackFamily, phraseFamily, operationKind, candidates, $"Resolved work family `{directPhraseFamily}` from phrase family `{phraseFamily}`.");
        }

        return BuildResolution(candidates[0], TaskboardWorkFamilyResolutionSource.TemplateId, stackFamily, phraseFamily, operationKind, candidates, $"Resolved work family `{candidates[0]}` from template `{templateId}`.");
    }

    private static TaskboardWorkFamilyResolution BuildResolution(
        string familyId,
        TaskboardWorkFamilyResolutionSource source,
        string stackFamily,
        string phraseFamily,
        string operationKind,
        List<string> candidates,
        string reason)
    {
        return new TaskboardWorkFamilyResolution
        {
            FamilyId = familyId,
            DisplayName = FormatDisplayName(familyId),
            Source = source,
            StackFamily = stackFamily,
            PhraseFamily = phraseFamily,
            OperationKind = operationKind,
            Reason = reason,
            CandidateFamilies = [.. candidates]
        };
    }

    private static string ResolveStackFallbackFamily(string stackFamily, TaskboardRunWorkItem workItem)
    {
        var title = $"{workItem.Title} {workItem.Summary} {workItem.PromptText}".Trim();
        if (string.IsNullOrWhiteSpace(title))
            return "";

        var actionableFollowupPhraseFamily = TaskboardStructuralHeadingService.ResolveActionableFollowupPhraseFamily(
            workItem.Title,
            workItem.Summary,
            workItem.PromptText);
        if (!string.IsNullOrWhiteSpace(actionableFollowupPhraseFamily))
        {
            return actionableFollowupPhraseFamily switch
            {
                "add_navigation_app_state" => "app_state_wiring",
                "setup_storage_layer" => "storage_bootstrap",
                "repository_scaffold" => "repository_scaffold",
                _ => "ui_shell_sections"
            };
        }

        if (title.Contains("storage", StringComparison.OrdinalIgnoreCase)
            || title.Contains("sqlite", StringComparison.OrdinalIgnoreCase)
            || title.Contains("repository", StringComparison.OrdinalIgnoreCase))
        {
            return "storage_bootstrap";
        }

        if (title.Contains("viewmodel", StringComparison.OrdinalIgnoreCase)
            || title.Contains("app state", StringComparison.OrdinalIgnoreCase))
        {
            return "app_state_wiring";
        }

        if (title.Contains("dashboard", StringComparison.OrdinalIgnoreCase)
            || title.Contains("findings", StringComparison.OrdinalIgnoreCase)
            || title.Contains("history", StringComparison.OrdinalIgnoreCase)
            || title.Contains("settings", StringComparison.OrdinalIgnoreCase)
            || title.Contains("shell", StringComparison.OrdinalIgnoreCase))
        {
            return "ui_shell_sections";
        }

        if (title.Contains("check runner", StringComparison.OrdinalIgnoreCase)
            || title.Contains("findings pipeline", StringComparison.OrdinalIgnoreCase)
            || title.Contains("snapshot", StringComparison.OrdinalIgnoreCase))
        {
            return "check_runner";
        }

        if ((title.Contains("evidence of success", StringComparison.OrdinalIgnoreCase)
                || title.Contains("proof of completion", StringComparison.OrdinalIgnoreCase)
                || title.Contains("validation evidence", StringComparison.OrdinalIgnoreCase)
                || title.Contains("acceptance evidence", StringComparison.OrdinalIgnoreCase))
            && (title.Contains("build", StringComparison.OrdinalIgnoreCase)
                || title.Contains("validate", StringComparison.OrdinalIgnoreCase)
                || title.Contains("verify", StringComparison.OrdinalIgnoreCase)
                || title.Contains("artifact", StringComparison.OrdinalIgnoreCase)
                || title.Contains(".csproj", StringComparison.OrdinalIgnoreCase)
                || title.Contains(".sln", StringComparison.OrdinalIgnoreCase)))
        {
            return "build_verify";
        }

        if ((title.Contains("required checkpoints", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required checkpoint", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required rule", StringComparison.OrdinalIgnoreCase))
            && (title.Contains("post-mutation verification", StringComparison.OrdinalIgnoreCase)
                || title.Contains("post mutation verification", StringComparison.OrdinalIgnoreCase)
                || title.Contains("after every patch/update pass", StringComparison.OrdinalIgnoreCase)
                || title.Contains("after every patch update pass", StringComparison.OrdinalIgnoreCase)
                || title.Contains("solution still builds", StringComparison.OrdinalIgnoreCase)
                || title.Contains("desktop app still launches", StringComparison.OrdinalIgnoreCase)
                || title.Contains("shell still loads", StringComparison.OrdinalIgnoreCase)
                || title.Contains("warnings are tracked", StringComparison.OrdinalIgnoreCase)))
        {
            return "build_verify";
        }

        if ((title.Contains("required context", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required baseline context", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required prior run data", StringComparison.OrdinalIgnoreCase)
                || title.Contains("maintenance context packet", StringComparison.OrdinalIgnoreCase)
                || title.Contains("context packet", StringComparison.OrdinalIgnoreCase)
                || title.Contains("existing project intake context", StringComparison.OrdinalIgnoreCase)
                || title.Contains("existing-project intake context", StringComparison.OrdinalIgnoreCase)
                || title.Contains("reuse prior run data", StringComparison.OrdinalIgnoreCase)
                || title.Contains("baseline context", StringComparison.OrdinalIgnoreCase))
            && (title.Contains("summary", StringComparison.OrdinalIgnoreCase)
                || title.Contains("artifact", StringComparison.OrdinalIgnoreCase)
                || title.Contains("baseline", StringComparison.OrdinalIgnoreCase)
                || title.Contains("prior run", StringComparison.OrdinalIgnoreCase)
                || title.Contains("existing project", StringComparison.OrdinalIgnoreCase)
                || title.Contains("workspace", StringComparison.OrdinalIgnoreCase)
                || title.Contains("build target", StringComparison.OrdinalIgnoreCase)
                || title.Contains("verification", StringComparison.OrdinalIgnoreCase)
                || title.Contains(".sln", StringComparison.OrdinalIgnoreCase)
                || title.Contains(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            return "maintenance_context";
        }

        if ((title.Contains("required behavior", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required behaviors", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required outputs", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required output", StringComparison.OrdinalIgnoreCase))
            && (title.Contains("file touch", StringComparison.OrdinalIgnoreCase)
                || title.Contains("skip with proof", StringComparison.OrdinalIgnoreCase)
                || title.Contains("normalized run", StringComparison.OrdinalIgnoreCase)
                || title.Contains("terminal run summary", StringComparison.OrdinalIgnoreCase)
                || title.Contains("patch/update artifacts", StringComparison.OrdinalIgnoreCase)
                || title.Contains("patch update artifacts", StringComparison.OrdinalIgnoreCase)
                || title.Contains("warning metrics", StringComparison.OrdinalIgnoreCase)))
        {
            return "maintenance_context";
        }

        if ((title.Contains("allowed feature classes for this project", StringComparison.OrdinalIgnoreCase)
                || title.Contains("allowed feature classes", StringComparison.OrdinalIgnoreCase)
                || title.Contains("allowed feature class", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required flow", StringComparison.OrdinalIgnoreCase))
            && (title.Contains("feature update", StringComparison.OrdinalIgnoreCase)
                || title.Contains("patch repair", StringComparison.OrdinalIgnoreCase)
                || title.Contains("baseline", StringComparison.OrdinalIgnoreCase)
                || title.Contains("bounded", StringComparison.OrdinalIgnoreCase)
                || title.Contains("dashboard", StringComparison.OrdinalIgnoreCase)
                || title.Contains("findings", StringComparison.OrdinalIgnoreCase)
                || title.Contains("history", StringComparison.OrdinalIgnoreCase)
                || title.Contains("settings", StringComparison.OrdinalIgnoreCase)
                || title.Contains("notification", StringComparison.OrdinalIgnoreCase)
                || title.Contains("remediation", StringComparison.OrdinalIgnoreCase)
                || title.Contains("summary", StringComparison.OrdinalIgnoreCase)
                || title.Contains("artifact", StringComparison.OrdinalIgnoreCase)
                || title.Contains("project", StringComparison.OrdinalIgnoreCase)))
        {
            return "maintenance_context";
        }

        if ((title.Contains("required result artifacts", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required result artifact", StringComparison.OrdinalIgnoreCase)
                || title.Contains("result artifacts", StringComparison.OrdinalIgnoreCase)
                || title.Contains("result artifact", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required output artifacts", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required output artifact", StringComparison.OrdinalIgnoreCase)
                || title.Contains("output artifacts", StringComparison.OrdinalIgnoreCase)
                || title.Contains("output artifact", StringComparison.OrdinalIgnoreCase)
                || title.Contains("generated outputs", StringComparison.OrdinalIgnoreCase)
                || title.Contains("generated output", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required deliverables", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required deliverable", StringComparison.OrdinalIgnoreCase)
                || title.Contains("completion artifacts", StringComparison.OrdinalIgnoreCase)
                || title.Contains("completion artifact", StringComparison.OrdinalIgnoreCase)
                || title.Contains("result package", StringComparison.OrdinalIgnoreCase)
                || title.Contains("result packages", StringComparison.OrdinalIgnoreCase)
                || title.Contains("result proof", StringComparison.OrdinalIgnoreCase)
                || title.Contains("result proofs", StringComparison.OrdinalIgnoreCase))
            && (title.Contains("artifact", StringComparison.OrdinalIgnoreCase)
                || title.Contains("result", StringComparison.OrdinalIgnoreCase)
                || title.Contains("output", StringComparison.OrdinalIgnoreCase)
                || title.Contains("deliverable", StringComparison.OrdinalIgnoreCase)
                || title.Contains("completion", StringComparison.OrdinalIgnoreCase)
                || title.Contains("package", StringComparison.OrdinalIgnoreCase)
                || title.Contains("proof", StringComparison.OrdinalIgnoreCase)
                || title.Contains("build", StringComparison.OrdinalIgnoreCase)
                || title.Contains("validate", StringComparison.OrdinalIgnoreCase)
                || title.Contains("verify", StringComparison.OrdinalIgnoreCase)
                || title.Contains(".csproj", StringComparison.OrdinalIgnoreCase)
                || title.Contains(".sln", StringComparison.OrdinalIgnoreCase)
                || title.Contains("workspace", StringComparison.OrdinalIgnoreCase)
                || title.Contains("solution", StringComparison.OrdinalIgnoreCase)
                || title.Contains("project", StringComparison.OrdinalIgnoreCase)))
        {
            return "build_verify";
        }

        if ((title.Contains("repair circular build dependency", StringComparison.OrdinalIgnoreCase)
                || title.Contains("repair build failure", StringComparison.OrdinalIgnoreCase)
                || title.Contains("project graph repair", StringComparison.OrdinalIgnoreCase)
                || title.Contains("solution graph repair", StringComparison.OrdinalIgnoreCase))
            && (title.Contains("build", StringComparison.OrdinalIgnoreCase)
                || title.Contains("project", StringComparison.OrdinalIgnoreCase)
                || title.Contains("solution", StringComparison.OrdinalIgnoreCase)
                || title.Contains("dependency", StringComparison.OrdinalIgnoreCase)
                || title.Contains("msb4006", StringComparison.OrdinalIgnoreCase)))
        {
            return "build_repair";
        }

        if ((title.Contains("required remediation types", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required remediation type", StringComparison.OrdinalIgnoreCase)
                || title.Contains("remediation types", StringComparison.OrdinalIgnoreCase)
                || title.Contains("remediation models", StringComparison.OrdinalIgnoreCase)
                || title.Contains("remediation contracts", StringComparison.OrdinalIgnoreCase))
            && (title.Contains("type", StringComparison.OrdinalIgnoreCase)
                || title.Contains("model", StringComparison.OrdinalIgnoreCase)
                || title.Contains("contract", StringComparison.OrdinalIgnoreCase)
                || title.Contains("record", StringComparison.OrdinalIgnoreCase)
                || title.Contains("core", StringComparison.OrdinalIgnoreCase)
                || title.Contains("src\\", StringComparison.OrdinalIgnoreCase)
                || title.Contains("/src/", StringComparison.OrdinalIgnoreCase)))
        {
            return "repository_scaffold";
        }

        if ((title.Contains("required rules", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required rule", StringComparison.OrdinalIgnoreCase)
                || title.Contains("rule specification", StringComparison.OrdinalIgnoreCase)
                || title.Contains("rule specifications", StringComparison.OrdinalIgnoreCase)
                || title.Contains("policy contract", StringComparison.OrdinalIgnoreCase)
                || title.Contains("policy contracts", StringComparison.OrdinalIgnoreCase)
                || title.Contains("guidance rules", StringComparison.OrdinalIgnoreCase))
            && (title.Contains("rule", StringComparison.OrdinalIgnoreCase)
                || title.Contains("rules", StringComparison.OrdinalIgnoreCase)
                || title.Contains("policy", StringComparison.OrdinalIgnoreCase)
                || title.Contains("spec", StringComparison.OrdinalIgnoreCase)
                || title.Contains("contract", StringComparison.OrdinalIgnoreCase)
                || title.Contains("record", StringComparison.OrdinalIgnoreCase)
                || title.Contains("core", StringComparison.OrdinalIgnoreCase)
                || title.Contains("src\\", StringComparison.OrdinalIgnoreCase)
                || title.Contains("/src/", StringComparison.OrdinalIgnoreCase)))
        {
            return "repository_scaffold";
        }

        if ((title.Contains("required future placeholders only", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required future placeholder only", StringComparison.OrdinalIgnoreCase)
                || title.Contains("future placeholders only", StringComparison.OrdinalIgnoreCase)
                || title.Contains("future placeholder only", StringComparison.OrdinalIgnoreCase)
                || title.Contains("template-only future work", StringComparison.OrdinalIgnoreCase)
                || title.Contains("template only future work", StringComparison.OrdinalIgnoreCase)
                || title.Contains("stub-only future work", StringComparison.OrdinalIgnoreCase)
                || title.Contains("stub only future work", StringComparison.OrdinalIgnoreCase)
                || title.Contains("deferred-spec-only work", StringComparison.OrdinalIgnoreCase)
                || title.Contains("deferred spec only work", StringComparison.OrdinalIgnoreCase))
            && (title.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
                || title.Contains("template", StringComparison.OrdinalIgnoreCase)
                || title.Contains("stub", StringComparison.OrdinalIgnoreCase)
                || title.Contains("deferred", StringComparison.OrdinalIgnoreCase)
                || title.Contains("spec", StringComparison.OrdinalIgnoreCase)
                || title.Contains("contract", StringComparison.OrdinalIgnoreCase)
                || title.Contains("model", StringComparison.OrdinalIgnoreCase)
                || title.Contains("record", StringComparison.OrdinalIgnoreCase)
                || title.Contains("guidance", StringComparison.OrdinalIgnoreCase)
                || title.Contains("rules", StringComparison.OrdinalIgnoreCase)
                || title.Contains("remediation", StringComparison.OrdinalIgnoreCase)
                || title.Contains("notification", StringComparison.OrdinalIgnoreCase)
                || title.Contains("core", StringComparison.OrdinalIgnoreCase)
                || title.Contains("src\\", StringComparison.OrdinalIgnoreCase)
                || title.Contains("/src/", StringComparison.OrdinalIgnoreCase)))
        {
            return "repository_scaffold";
        }

        if ((title.Contains("required notification events", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required notification event", StringComparison.OrdinalIgnoreCase))
            && (title.Contains("notification", StringComparison.OrdinalIgnoreCase)
                || title.Contains("event", StringComparison.OrdinalIgnoreCase)
                || title.Contains("state", StringComparison.OrdinalIgnoreCase)
                || title.Contains("guidance", StringComparison.OrdinalIgnoreCase)
                || title.Contains("rules", StringComparison.OrdinalIgnoreCase)
                || title.Contains("remediation", StringComparison.OrdinalIgnoreCase)
                || title.Contains("src\\", StringComparison.OrdinalIgnoreCase)
                || title.Contains("/src/", StringComparison.OrdinalIgnoreCase)))
        {
            return "app_state_wiring";
        }

        if ((title.Contains("required handlers", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required handler", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required policies", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required policy", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required guards", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required guard", StringComparison.OrdinalIgnoreCase)
                || title.Contains("required wiring", StringComparison.OrdinalIgnoreCase))
            && (title.Contains("handler", StringComparison.OrdinalIgnoreCase)
                || title.Contains("policy", StringComparison.OrdinalIgnoreCase)
                || title.Contains("guard", StringComparison.OrdinalIgnoreCase)
                || title.Contains("wiring", StringComparison.OrdinalIgnoreCase)
                || title.Contains("registration", StringComparison.OrdinalIgnoreCase)
                || title.Contains("guidance", StringComparison.OrdinalIgnoreCase)
                || title.Contains("rules", StringComparison.OrdinalIgnoreCase)
                || title.Contains("remediation", StringComparison.OrdinalIgnoreCase)
                || title.Contains("src\\", StringComparison.OrdinalIgnoreCase)
                || title.Contains("/src/", StringComparison.OrdinalIgnoreCase)))
        {
            return "ui_wiring";
        }

        if (title.Contains("build verify", StringComparison.OrdinalIgnoreCase)
            || title.Contains("build", StringComparison.OrdinalIgnoreCase))
        {
            return "build_verify";
        }

        if (string.Equals(stackFamily, "native_cpp_desktop", StringComparison.OrdinalIgnoreCase))
            return "native_project_bootstrap";

        return "";
    }

    private static string FormatDisplayName(string familyId)
    {
        return familyId switch
        {
            "solution_scaffold" => "Solution scaffold",
            "ui_shell_sections" => "Grouped shell/page scaffold",
            "ui_wiring" => "UI wiring",
            "app_state_wiring" => "App-state wiring",
            "viewmodel_scaffold" => "Viewmodel scaffold",
            "storage_bootstrap" => "Storage bootstrap",
            "core_domain_models_contracts" => "Core domain/contracts scaffold",
            "repository_scaffold" => "Repository scaffold",
            "check_runner" => "Check runner",
            "findings_pipeline" => "Findings pipeline",
            "build_verify" => "Build verify",
            "build_repair" => "Build repair",
            "native_project_bootstrap" => "Native bootstrap",
            _ => familyId.Replace('_', ' ').Trim()
        };
    }

    private static string NormalizeValue(string value)
    {
        return (value ?? "").Trim().ToLowerInvariant();
    }

    private static bool ShouldNormalizeTestSupportWriteFamily(string operationKind, string explicitWorkFamily)
    {
        if (!string.Equals(explicitWorkFamily, "check_runner", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(operationKind, "write_check_registry", StringComparison.OrdinalIgnoreCase)
            || string.Equals(operationKind, "write_snapshot_builder", StringComparison.OrdinalIgnoreCase)
            || string.Equals(operationKind, "write_findings_normalizer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldNormalizeDomainContractsFamily(
        TaskboardRunWorkItem workItem,
        string operationKind,
        string explicitWorkFamily,
        string phraseFamily,
        string templateId)
    {
        if (LooksLikePlainSiblingProjectSetup(workItem, operationKind))
            return false;

        if (string.Equals(phraseFamily, "core_domain_models_contracts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(templateId, "dotnet.domain_contracts_scaffold.v1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (operationKind is "create_core_library" or "attach_core_library" or "add_domain_reference" or "make_contracts_dir" or "make_models_dir" or "write_contract_file" or "write_domain_model_file")
            return true;

        if (!string.IsNullOrWhiteSpace(explicitWorkFamily)
            && !string.Equals(explicitWorkFamily, "repository_scaffold", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(explicitWorkFamily, "solution_scaffold", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var context = NormalizeValue(BuildDomainContractsContextText(workItem));
        if (string.IsNullOrWhiteSpace(context))
            return false;

        if (ContainsAny(context, "repository", "snapshot", "sqlite", "storage", "settingsstore", "isnapshotrepository"))
            return false;

        return ContainsAny(context, ".core", "core", "contracts", "models", "checkdefinition", "findingrecord", "corelibrary", "contractslibrary");
    }

    private static bool LooksLikePlainSiblingProjectSetup(TaskboardRunWorkItem workItem, string operationKind)
    {
        if (operationKind is not ("create_project" or "add_project_to_solution" or "add_project_reference"))
            return false;

        var context = NormalizeValue(BuildDomainContractsContextText(workItem));
        if (string.IsNullOrWhiteSpace(context))
            return false;

        var hasSiblingProjectIdentity = ContainsAny(
            context,
            ".core",
            ".storage",
            ".services",
            ".tests",
            ".contracts",
            ".repository");
        if (!hasSiblingProjectIdentity)
            return false;

        return operationKind switch
        {
            "create_project" => ContainsAny(
                context,
                "create dotnet project",
                "create project",
                "create class library project",
                "create test project",
                "classlib",
                "class library",
                "xunit",
                "wpf"),
            "add_project_to_solution" => ContainsAny(context, "add project", "attach project", "include project", "to solution"),
            "add_project_reference" => ContainsAny(context, "add reference from", "add dotnet project reference", "add project reference"),
            _ => false
        };
    }

    private static string BuildDomainContractsContextText(TaskboardRunWorkItem workItem)
    {
        return string.Join(" ",
            workItem.Title,
            workItem.Summary,
            workItem.PromptText,
            workItem.ExpectedArtifact,
            ReadArgument(workItem.DirectToolRequest, "path"),
            ReadArgument(workItem.DirectToolRequest, "project_path"),
            ReadArgument(workItem.DirectToolRequest, "reference_path"),
            ReadArgument(workItem.DirectToolRequest, "solution_path"),
            ReadArgument(workItem.DirectToolRequest, "output_path"),
            ReadArgument(workItem.DirectToolRequest, "project_name"));
    }

    private static string ReadArgument(ToolRequest? request, string key)
    {
        if (request is null)
            return "";

        return request.Arguments.TryGetValue(key, out var value)
            ? value ?? ""
            : "";
    }

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
