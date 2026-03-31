using RAM.Models;

namespace RAM.Services;

public sealed class ToolChainTemplateRegistry
{
    private readonly List<ToolChainTemplate> _templates;
    private readonly Dictionary<string, ToolChainTemplate> _templatesByName;

    public ToolChainTemplateRegistry()
    {
        _templates =
        [
            BuildAutoValidationTemplate(),
            BuildFileEditTemplate(),
            BuildRepairPreviewTemplate(),
            BuildRepairExecutionTemplate(),
            BuildRepairSingleStepTemplate(),
            BuildBuildProfileTemplate(),
            BuildWorkspaceBuildVerifyTemplate(),
            BuildWorkspaceTestVerifyTemplate(),
            BuildWorkspaceNativeBuildVerifyTemplate(),
            BuildDotnetProjectAttachTemplate(),
            BuildDotnetSolutionScaffoldTemplate(),
            BuildDotnetDesktopShellScaffoldTemplate(),
            BuildDotnetShellPageSetTemplate(),
            BuildDotnetDomainContractsScaffoldTemplate(),
            BuildDotnetPageAndViewmodelTemplate(),
            BuildDotnetNavigationWireupTemplate(),
            BuildDotnetShellRegistrationWireupTemplate(),
            BuildDotnetRepositoryScaffoldTemplate(),
            BuildDotnetSqliteStorageBootstrapTemplate(),
            BuildDotnetCheckRunnerTemplate(),
            BuildDotnetFindingsPipelineTemplate(),
            BuildCMakeProjectBootstrapTemplate(),
            BuildCppWin32ShellTemplate(),
            BuildCppWin32ShellPageSetTemplate(),
            BuildCppConsoleScaffoldTemplate(),
            BuildCppLibraryScaffoldTemplate(),
            BuildCMakeTargetAttachTemplate(),
            BuildBuildExecutionTemplate(),
            BuildArtifactInspectionTemplate()
        ];
        foreach (var template in _templates)
            EnsureStepGraph(template);
        _templatesByName = _templates.ToDictionary(
            template => template.Name,
            template => template,
            StringComparer.OrdinalIgnoreCase);
    }

    public ToolChainTemplate ResolveTemplate(string toolName)
    {
        var normalized = Normalize(toolName);
        return _templates.FirstOrDefault(template => template.StartingTools.Contains(normalized))
            ?? new ToolChainTemplate
            {
                Name = "custom_controlled",
                ChainType = ToolChainType.CustomControlled,
                MaxStepCount = 1,
                ModelSummaryAllowed = true,
                StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalized }
            };
    }

    public ToolChainTemplate ResolveTemplateForName(string templateName)
    {
        if (!string.IsNullOrWhiteSpace(templateName)
            && _templatesByName.TryGetValue(templateName, out var template))
        {
            return template;
        }

        return new ToolChainTemplate
        {
            Name = FirstNonEmpty(templateName, "custom_controlled"),
            ChainType = ToolChainType.CustomControlled,
            MaxStepCount = 1,
            ModelSummaryAllowed = true
        };
    }

    public bool HasTemplate(string templateName)
    {
        return !string.IsNullOrWhiteSpace(templateName)
            && _templatesByName.ContainsKey(templateName);
    }

    public bool IsStartingToolAllowed(ToolChainTemplate template, string toolName)
    {
        return ValidateStep(template, new ToolRequest { ToolName = toolName }, null).Allowed;
    }

    public bool IsTransitionAllowed(ToolChainTemplate template, string previousTool, string nextTool)
    {
        return ValidateStep(
            template,
            new ToolRequest { ToolName = nextTool },
            string.IsNullOrWhiteSpace(previousTool) ? null : new ToolRequest { ToolName = previousTool }).Allowed;
    }

    public ChainTemplateValidationResult ValidateStep(ToolChainTemplate template, ToolRequest request, ToolRequest? previousRequest)
    {
        if (template is null)
        {
            return new ChainTemplateValidationResult
            {
                Allowed = false,
                TemplateName = "(none)",
                AttemptedToolId = Normalize(request.ToolName),
                AttemptedStepId = Normalize(request.ToolName),
                BlockerCode = ChainTemplateValidationBlockerCode.TemplateMissingStepDefinition,
                MismatchOrigin = ChainTemplateMismatchOrigin.TemplateDefinition,
                Message = "Controlled chain blocked: no chain template was available for validation."
            };
        }

        EnsureStepGraph(template);
        var attemptedToolId = Normalize(request.ToolName);
        var attemptedStepId = ResolveStepId(template, attemptedToolId);
        var previousStepId = previousRequest is null ? "" : ResolveStepId(template, Normalize(previousRequest.ToolName));
        var allowedNext = ResolveAllowedNextStepIds(template, previousStepId);

        if (string.IsNullOrWhiteSpace(attemptedStepId))
        {
            var blockerCode = request.ExecutionSourceName.StartsWith("taskboard_auto_run", StringComparison.OrdinalIgnoreCase)
                ? ChainTemplateValidationBlockerCode.DecompositionEmittedUnknownStep
                : ChainTemplateValidationBlockerCode.TemplateMissingStepDefinition;
            var origin = request.ExecutionSourceName.StartsWith("taskboard_auto_run", StringComparison.OrdinalIgnoreCase)
                ? ChainTemplateMismatchOrigin.DecompositionOutput
                : ChainTemplateMismatchOrigin.TemplateDefinition;
            var missingStepMessage = string.Equals(template.Name, "dotnet.check_runner_scaffold.v1", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attemptedToolId, "write_file", StringComparison.OrdinalIgnoreCase)
                    ? $"Controlled chain blocked: write-generation work was routed into prerequisite-only chain `{template.Name}`, which only supports `create_dotnet_project -> add_project_to_solution -> dotnet_test`; rerouting to a writable scaffold/write path is required. allowed_next=[{FormatList(allowedNext)}] current_state={FirstNonEmpty(previousStepId, "start")}."
                    : $"Controlled chain blocked: template `{template.Name}` does not declare a step for tool `{request.ToolName}`. allowed_next=[{FormatList(allowedNext)}] current_state={FirstNonEmpty(previousStepId, "start")}.";
            return BuildValidationFailure(
                template,
                attemptedToolId,
                previousStepId,
                allowedNext,
                blockerCode,
                origin,
                missingStepMessage);
        }

        if (string.IsNullOrWhiteSpace(previousStepId))
        {
            if (template.StepGraph.StartStepIds.Contains(attemptedStepId))
                return BuildValidationSuccess(template, attemptedStepId, previousStepId, allowedNext);

            return BuildValidationFailure(
                template,
                attemptedStepId,
                previousStepId,
                allowedNext,
                ChainTemplateValidationBlockerCode.StartStepNotAllowed,
                ChainTemplateMismatchOrigin.TemplateDefinition,
                BuildStartStepNotAllowedMessage(template, attemptedStepId));
        }

        if (string.Equals(previousStepId, attemptedStepId, StringComparison.OrdinalIgnoreCase)
            && IsRepeatable(template, attemptedStepId))
        {
            return BuildValidationSuccess(template, attemptedStepId, previousStepId, allowedNext);
        }

        if (allowedNext.Contains(attemptedStepId, StringComparer.OrdinalIgnoreCase))
            return BuildValidationSuccess(template, attemptedStepId, previousStepId, allowedNext);

        return BuildValidationFailure(
            template,
            attemptedStepId,
            previousStepId,
            allowedNext,
            ChainTemplateValidationBlockerCode.TemplateTransitionNotAllowed,
            ChainTemplateMismatchOrigin.TemplateDefinition,
            $"Controlled chain blocked: template `{template.Name}` rejected attempted_step=`{attemptedStepId}` after last_step=`{previousStepId}`. allowed_next=[{FormatList(allowedNext)}].");
    }

    private static ToolChainTemplate BuildAutoValidationTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "auto_validation_single_step",
            ChainType = ToolChainType.AutoValidation,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "write_file",
                "replace_in_file",
                "save_output",
                "apply_patch_draft"
            }
        };
    }

    private static ToolChainTemplate BuildFileEditTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "file_edit_single_step",
            ChainType = ToolChainType.FileEdit,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_file",
                "append_file",
                "make_dir",
                "read_file",
                "read_file_chunk",
                "file_info"
            }
        };
    }

    private static ToolChainTemplate BuildRepairPreviewTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "repair_preview_chain",
            ChainType = ToolChainType.Repair,
            MaxStepCount = 2,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "plan_repair",
                "preview_patch_draft"
            },
            AllowedTransitions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan_repair"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "preview_patch_draft"
                }
            }
        };
    }

    private static ToolChainTemplate BuildRepairExecutionTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "repair_execution_chain",
            ChainType = ToolChainType.Repair,
            MaxStepCount = 4,
            ModelSummaryAllowed = true,
            StepGraph = BuildStepGraph(
                startStepIds:
                [
                    "plan_repair"
                ],
                stepDefinitions:
                [
                    ("plan_repair", "plan_repair", "Create a bounded repair proposal from the recorded failure."),
                    ("preview_patch_draft", "preview_patch_draft", "Preview the bounded repair patch draft."),
                    ("apply_patch_draft", "apply_patch_draft", "Apply the locally safe repair patch draft."),
                    ("verify_patch_draft", "verify_patch_draft", "Rerun bounded verification after the repair is applied.")
                ],
                transitions:
                [
                    ("plan_repair", "preview_patch_draft"),
                    ("preview_patch_draft", "apply_patch_draft"),
                    ("apply_patch_draft", "verify_patch_draft")
                ],
                optionalStepIds:
                [
                    "preview_patch_draft",
                    "apply_patch_draft"
                ],
                terminalStepIds:
                [
                    "verify_patch_draft"
                ]),
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "plan_repair"
            }
        };
    }

    private static ToolChainTemplate BuildRepairSingleStepTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "repair_single_step",
            ChainType = ToolChainType.Repair,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "open_failure_context",
                "plan_repair",
                "apply_patch_draft",
                "verify_patch_draft"
            }
        };
    }

    private static ToolChainTemplate BuildBuildProfileTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "build_profile_chain",
            ChainType = ToolChainType.Build,
            MaxStepCount = 2,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "detect_build_system",
                "list_build_profiles"
            },
            AllowedTransitions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["detect_build_system"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "list_build_profiles"
                }
            }
        };
    }

    private static ToolChainTemplate BuildBuildExecutionTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "build_execution_single_step",
            ChainType = ToolChainType.Build,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "list_projects",
                "inspect_project",
                "git_status",
                "git_diff",
                "run_command",
                "create_dotnet_solution",
                "create_dotnet_project",
                "add_project_to_solution",
                "add_dotnet_project_reference",
                "create_dotnet_page_view",
                "create_dotnet_viewmodel",
                "register_navigation",
                "register_di_service",
                "initialize_sqlite_storage_boundary",
                "create_cmake_project",
                "create_cpp_source_file",
                "create_cpp_header_file",
                "create_c_source_file",
                "create_c_header_file",
                "dotnet_build",
                "dotnet_test",
                "cmake_configure",
                "cmake_build",
                "ctest_run",
                "make_build",
                "ninja_build",
                "run_build_script"
            }
        };
    }

    private static ToolChainTemplate BuildWorkspaceBuildVerifyTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "workspace.build_verify.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 2,
            ModelSummaryAllowed = true,
            StepGraph = BuildStepGraph(
                startStepIds:
                [
                    "dotnet_build"
                ],
                stepDefinitions:
                [
                    ("dotnet_build", "dotnet_build", "Run the deterministic .NET build target for the current workspace."),
                    ("dotnet_test", "dotnet_test", "Run the deterministic .NET test target after a successful build when one exists.")
                ],
                transitions:
                [
                    ("dotnet_build", "dotnet_test")
                ],
                optionalStepIds:
                [
                    "dotnet_test"
                ],
                terminalStepIds:
                [
                    "dotnet_build",
                    "dotnet_test"
                ]),
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "dotnet_build"
            },
            AllowedTransitions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["dotnet_build"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "dotnet_test"
                }
            }
        };
    }

    private static ToolChainTemplate BuildWorkspaceTestVerifyTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "workspace.test_verify.v1",
            ChainType = ToolChainType.Verification,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StepGraph = BuildStepGraph(
                startStepIds:
                [
                    "dotnet_test"
                ],
                stepDefinitions:
                [
                    ("dotnet_test", "dotnet_test", "Run the deterministic .NET test target for the current workspace.")
                ],
                transitions:
                [
                ],
                terminalStepIds:
                [
                    "dotnet_test"
                ]),
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "dotnet_test"
            }
        };
    }

    private static ToolChainTemplate BuildWorkspaceNativeBuildVerifyTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "workspace.native_build_verify.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "cmake_configure",
                "cmake_build",
                "ctest_run",
                "make_build",
                "ninja_build",
                "run_build_script"
            }
        };
    }

    private static ToolChainTemplate BuildDotnetProjectAttachTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "dotnet.project_attach.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 3,
            ModelSummaryAllowed = true,
            StepGraph = BuildStepGraph(
                startStepIds:
                [
                    "create_dotnet_project",
                    "add_project_to_solution",
                    "add_dotnet_project_reference"
                ],
                stepDefinitions:
                [
                    ("create_dotnet_project", "create_dotnet_project", "Create the missing project before attaching it to the solution."),
                    ("add_project_to_solution", "add_project_to_solution", "Attach the project to the solution."),
                    ("add_dotnet_project_reference", "add_dotnet_project_reference", "Attach the next deterministic project reference.")
                ],
                transitions:
                [
                    ("create_dotnet_project", "add_project_to_solution"),
                    ("add_project_to_solution", "add_dotnet_project_reference")
                ],
                optionalStepIds:
                [
                    "create_dotnet_project",
                    "add_project_to_solution"
                ],
                terminalStepIds:
                [
                    "add_project_to_solution",
                    "add_dotnet_project_reference"
                ]),
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_dotnet_project",
                "add_project_to_solution",
                "add_dotnet_project_reference"
            }
        };
    }

    private static ToolChainTemplate BuildDotnetSolutionScaffoldTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "dotnet.solution_scaffold.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 3,
            ModelSummaryAllowed = true,
            StepGraph = BuildStepGraph(
                startStepIds:
                [
                    "create_dotnet_solution",
                    "create_dotnet_project",
                    "add_project_to_solution"
                ],
                stepDefinitions:
                [
                    ("create_dotnet_solution", "create_dotnet_solution", "Create the workspace solution when missing."),
                    ("create_dotnet_project", "create_dotnet_project", "Create the primary desktop project."),
                    ("add_project_to_solution", "add_project_to_solution", "Attach the project to the solution.")
                ],
                transitions:
                [
                    ("create_dotnet_solution", "create_dotnet_project"),
                    ("create_dotnet_solution", "add_project_to_solution"),
                    ("create_dotnet_project", "add_project_to_solution")
                ],
                optionalStepIds:
                [
                    "create_dotnet_solution",
                    "create_dotnet_project"
                ],
                terminalStepIds:
                [
                    "add_project_to_solution",
                    "create_dotnet_project"
                ]),
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_dotnet_solution",
                "create_dotnet_project",
                "add_project_to_solution"
            }
        };
    }

    private static ToolChainTemplate BuildDotnetDesktopShellScaffoldTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "dotnet.desktop_shell_scaffold.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_dotnet_page_view",
                "create_dotnet_viewmodel"
            }
        };
    }

    private static ToolChainTemplate BuildDotnetShellPageSetTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "dotnet.shell_page_set_scaffold.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StepGraph = BuildStepGraph(
                startStepIds:
                [
                    "create_dotnet_solution",
                    "create_dotnet_project",
                    "add_project_to_solution",
                    "make_dir",
                    "create_dotnet_page_view",
                    "register_navigation",
                    "create_dotnet_viewmodel",
                    "dotnet_build"
                ],
                stepDefinitions:
                [
                    ("create_dotnet_solution", "create_dotnet_solution", "Create the grouped-shell solution when missing."),
                    ("create_dotnet_project", "create_dotnet_project", "Create the grouped-shell desktop project when missing."),
                    ("add_project_to_solution", "add_project_to_solution", "Attach the desktop project to the solution."),
                    ("make_dir", "make_dir", "Create required grouped-shell workspace folders."),
                    ("create_dotnet_page_view", "create_dotnet_page_view", "Create one grouped-shell page view."),
                    ("register_navigation", "register_navigation", "Write grouped-shell navigation artifacts."),
                    ("create_dotnet_viewmodel", "create_dotnet_viewmodel", "Write grouped-shell app state and viewmodel artifacts."),
                    ("dotnet_build", "dotnet_build", "Validate the grouped-shell build.")
                ],
                transitions:
                [
                    ("create_dotnet_solution", "create_dotnet_project"),
                    ("create_dotnet_solution", "add_project_to_solution"),
                    ("create_dotnet_solution", "create_dotnet_page_view"),
                    ("create_dotnet_solution", "make_dir"),
                    ("create_dotnet_project", "add_project_to_solution"),
                    ("create_dotnet_project", "create_dotnet_page_view"),
                    ("create_dotnet_project", "make_dir"),
                    ("add_project_to_solution", "create_dotnet_page_view"),
                    ("add_project_to_solution", "make_dir"),
                    ("create_dotnet_page_view", "create_dotnet_page_view"),
                    ("create_dotnet_page_view", "make_dir"),
                    ("create_dotnet_page_view", "register_navigation"),
                    ("create_dotnet_page_view", "create_dotnet_viewmodel"),
                    ("create_dotnet_page_view", "dotnet_build"),
                    ("make_dir", "register_navigation"),
                    ("make_dir", "create_dotnet_viewmodel"),
                    ("make_dir", "dotnet_build"),
                    ("register_navigation", "create_dotnet_viewmodel"),
                    ("register_navigation", "dotnet_build"),
                    ("create_dotnet_viewmodel", "dotnet_build")
                ],
                repeatableStepIds:
                [
                    "create_dotnet_page_view"
                ],
                optionalStepIds:
                [
                    "create_dotnet_solution",
                    "create_dotnet_project",
                    "add_project_to_solution",
                    "make_dir",
                    "register_navigation",
                    "create_dotnet_viewmodel"
                ],
                terminalStepIds:
                [
                    "dotnet_build"
                ]),
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_dotnet_page_view",
                "create_dotnet_viewmodel",
                "register_navigation",
                "make_dir",
                "create_dotnet_solution",
                "create_dotnet_project",
                "add_project_to_solution",
                "dotnet_build"
            }
        };
    }

    private static ToolChainTemplate BuildDotnetPageAndViewmodelTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "dotnet.page_and_viewmodel_scaffold.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_dotnet_page_view",
                "create_dotnet_viewmodel"
            }
        };
    }

    private static ToolChainTemplate BuildDotnetDomainContractsScaffoldTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "dotnet.domain_contracts_scaffold.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 8,
            ModelSummaryAllowed = true,
            StepGraph = BuildStepGraph(
                startStepIds:
                [
                    "create_dotnet_project",
                    "add_project_to_solution",
                    "add_dotnet_project_reference",
                    "make_dir",
                    "write_file",
                    "dotnet_build"
                ],
                stepDefinitions:
                [
                    ("create_dotnet_project", "create_dotnet_project", "Create the core contracts library project."),
                    ("add_project_to_solution", "add_project_to_solution", "Attach the core contracts library to the solution."),
                    ("add_dotnet_project_reference", "add_dotnet_project_reference", "Wire the deterministic app-to-core project reference."),
                    ("make_dir", "make_dir", "Create the core contracts/models directories."),
                    ("write_file", "write_file", "Write the core contracts and domain model files."),
                    ("dotnet_build", "dotnet_build", "Run deterministic build verification for the core contracts library."),
                    ("dotnet_test", "dotnet_test", "Run deterministic tests after the core contracts library builds.")
                ],
                transitions:
                [
                    ("create_dotnet_project", "add_project_to_solution"),
                    ("add_project_to_solution", "add_dotnet_project_reference"),
                    ("add_project_to_solution", "make_dir"),
                    ("add_project_to_solution", "write_file"),
                    ("add_project_to_solution", "dotnet_build"),
                    ("add_dotnet_project_reference", "make_dir"),
                    ("add_dotnet_project_reference", "write_file"),
                    ("add_dotnet_project_reference", "dotnet_build"),
                    ("make_dir", "make_dir"),
                    ("make_dir", "write_file"),
                    ("make_dir", "dotnet_build"),
                    ("write_file", "write_file"),
                    ("write_file", "dotnet_build"),
                    ("dotnet_build", "dotnet_test")
                ],
                repeatableStepIds:
                [
                    "make_dir",
                    "write_file"
                ],
                optionalStepIds:
                [
                    "create_dotnet_project",
                    "add_project_to_solution",
                    "add_dotnet_project_reference",
                    "make_dir",
                    "write_file",
                    "dotnet_test"
                ],
                terminalStepIds:
                [
                    "write_file",
                    "dotnet_build",
                    "dotnet_test"
                ]),
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_dotnet_project",
                "add_project_to_solution",
                "add_dotnet_project_reference",
                "write_file",
                "make_dir",
                "dotnet_build"
            }
        };
    }

    private static ToolChainTemplate BuildDotnetNavigationWireupTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "dotnet.navigation_wireup.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 3,
            ModelSummaryAllowed = true,
            StepGraph = BuildStepGraph(
                startStepIds:
                [
                    "make_dir",
                    "register_navigation",
                    "create_dotnet_viewmodel"
                ],
                stepDefinitions:
                [
                    ("make_dir", "make_dir", "Create navigation/state directories."),
                    ("register_navigation", "register_navigation", "Write navigation artifacts."),
                    ("create_dotnet_viewmodel", "create_dotnet_viewmodel", "Write app state and shell viewmodels.")
                ],
                transitions:
                [
                    ("make_dir", "register_navigation"),
                    ("make_dir", "create_dotnet_viewmodel"),
                    ("register_navigation", "create_dotnet_viewmodel")
                ],
                optionalStepIds:
                [
                    "make_dir",
                    "register_navigation",
                    "create_dotnet_viewmodel"
                ],
                terminalStepIds:
                [
                    "register_navigation",
                    "create_dotnet_viewmodel"
                ]),
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "make_dir",
                "register_navigation",
                "create_dotnet_viewmodel",
                "register_di_service"
            }
        };
    }

    private static ToolChainTemplate BuildDotnetShellRegistrationWireupTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "dotnet.shell_registration_wireup.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 4,
            ModelSummaryAllowed = true,
            StepGraph = BuildStepGraph(
                startStepIds:
                [
                    "make_dir",
                    "register_navigation",
                    "create_dotnet_viewmodel"
                ],
                stepDefinitions:
                [
                    ("make_dir", "make_dir", "Create grouped-shell state directories."),
                    ("register_navigation", "register_navigation", "Write grouped-shell navigation registration."),
                    ("create_dotnet_viewmodel", "create_dotnet_viewmodel", "Write grouped-shell shell viewmodel artifacts.")
                ],
                transitions:
                [
                    ("make_dir", "register_navigation"),
                    ("make_dir", "create_dotnet_viewmodel"),
                    ("register_navigation", "create_dotnet_viewmodel"),
                    ("create_dotnet_viewmodel", "create_dotnet_viewmodel")
                ],
                repeatableStepIds:
                [
                    "create_dotnet_viewmodel"
                ],
                optionalStepIds:
                [
                    "make_dir",
                    "register_navigation",
                    "create_dotnet_viewmodel"
                ],
                terminalStepIds:
                [
                    "register_navigation",
                    "create_dotnet_viewmodel"
                ]),
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "make_dir",
                "register_navigation",
                "create_dotnet_viewmodel"
            }
        };
    }

    private static ToolChainTemplate BuildDotnetRepositoryScaffoldTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "dotnet.repository_scaffold.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 7,
            ModelSummaryAllowed = true,
            StepGraph = BuildStepGraph(
                startStepIds:
                [
                    "create_dotnet_project",
                    "add_project_to_solution",
                    "add_dotnet_project_reference",
                    "make_dir",
                    "write_file",
                    "dotnet_build"
                ],
                stepDefinitions:
                [
                    ("create_dotnet_project", "create_dotnet_project", "Create the repository/core library project."),
                    ("add_project_to_solution", "add_project_to_solution", "Attach the repository/core library project."),
                    ("add_dotnet_project_reference", "add_dotnet_project_reference", "Add repository project references."),
                    ("make_dir", "make_dir", "Create repository contract/model directories."),
                    ("write_file", "write_file", "Write repository contracts and models."),
                    ("dotnet_build", "dotnet_build", "Run deterministic build verification for repository wiring."),
                    ("dotnet_test", "dotnet_test", "Run deterministic tests after repository wiring builds.")
                ],
                transitions:
                [
                    ("create_dotnet_project", "add_project_to_solution"),
                    ("add_project_to_solution", "add_dotnet_project_reference"),
                    ("add_project_to_solution", "make_dir"),
                    ("add_project_to_solution", "write_file"),
                    ("add_project_to_solution", "dotnet_build"),
                    ("add_dotnet_project_reference", "make_dir"),
                    ("add_dotnet_project_reference", "write_file"),
                    ("add_dotnet_project_reference", "dotnet_build"),
                    ("make_dir", "make_dir"),
                    ("make_dir", "write_file"),
                    ("make_dir", "dotnet_build"),
                    ("write_file", "write_file"),
                    ("write_file", "dotnet_build"),
                    ("dotnet_build", "dotnet_test")
                ],
                repeatableStepIds:
                [
                    "make_dir",
                    "write_file"
                ],
                optionalStepIds:
                [
                    "create_dotnet_project",
                    "add_project_to_solution",
                    "add_dotnet_project_reference",
                    "make_dir",
                    "write_file",
                    "dotnet_test"
                ],
                terminalStepIds:
                [
                    "write_file",
                    "dotnet_build",
                    "dotnet_test"
                ]),
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_dotnet_project",
                "add_project_to_solution",
                "add_dotnet_project_reference",
                "make_dir",
                "write_file",
                "dotnet_build"
            }
        };
    }

    private static ToolChainTemplate BuildDotnetSqliteStorageBootstrapTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "dotnet.sqlite_storage_bootstrap.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 3,
            ModelSummaryAllowed = true,
            StepGraph = BuildStepGraph(
                startStepIds:
                [
                    "make_dir",
                    "initialize_sqlite_storage_boundary",
                    "register_di_service"
                ],
                stepDefinitions:
                [
                    ("make_dir", "make_dir", "Create storage directories."),
                    ("initialize_sqlite_storage_boundary", "initialize_sqlite_storage_boundary", "Write storage boundary artifacts."),
                    ("register_di_service", "register_di_service", "Register storage implementation.")
                ],
                transitions:
                [
                    ("make_dir", "initialize_sqlite_storage_boundary"),
                    ("make_dir", "register_di_service"),
                    ("initialize_sqlite_storage_boundary", "register_di_service")
                ],
                optionalStepIds:
                [
                    "make_dir",
                    "register_di_service"
                ],
                terminalStepIds:
                [
                    "initialize_sqlite_storage_boundary",
                    "register_di_service"
                ]),
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "make_dir",
                "initialize_sqlite_storage_boundary",
                "register_di_service"
            }
        };
    }

    private static ToolChainTemplate BuildDotnetCheckRunnerTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "dotnet.check_runner_scaffold.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 3,
            ModelSummaryAllowed = true,
            StepGraph = BuildStepGraph(
                startStepIds:
                [
                    "create_dotnet_project",
                    "add_project_to_solution",
                    "dotnet_test"
                ],
                stepDefinitions:
                [
                    ("create_dotnet_project", "create_dotnet_project", "Create the check-runner test project."),
                    ("add_project_to_solution", "add_project_to_solution", "Attach the check-runner project to the solution."),
                    ("dotnet_test", "dotnet_test", "Run the check-runner tests.")
                ],
                transitions:
                [
                    ("create_dotnet_project", "add_project_to_solution"),
                    ("create_dotnet_project", "dotnet_test"),
                    ("add_project_to_solution", "dotnet_test")
                ],
                optionalStepIds:
                [
                    "create_dotnet_project",
                    "add_project_to_solution"
                ],
                terminalStepIds:
                [
                    "dotnet_test"
                ]),
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_dotnet_project",
                "add_project_to_solution",
                "dotnet_test"
            }
        };
    }

    private static ToolChainTemplate BuildDotnetFindingsPipelineTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "dotnet.findings_pipeline_bootstrap.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StepGraph = BuildStepGraph(
                startStepIds:
                [
                    "create_dotnet_project",
                    "add_project_to_solution",
                    "write_file",
                    "dotnet_test"
                ],
                stepDefinitions:
                [
                    ("create_dotnet_project", "create_dotnet_project", "Create the findings/check project when missing."),
                    ("add_project_to_solution", "add_project_to_solution", "Attach the findings/check project."),
                    ("write_file", "write_file", "Write findings pipeline scaffolds."),
                    ("dotnet_test", "dotnet_test", "Run findings/check verification tests.")
                ],
                transitions:
                [
                    ("create_dotnet_project", "add_project_to_solution"),
                    ("add_project_to_solution", "write_file"),
                    ("write_file", "write_file"),
                    ("write_file", "dotnet_test")
                ],
                repeatableStepIds:
                [
                    "write_file"
                ],
                optionalStepIds:
                [
                    "create_dotnet_project",
                    "add_project_to_solution",
                    "write_file"
                ],
                terminalStepIds:
                [
                    "dotnet_test",
                    "write_file"
                ]),
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_dotnet_project",
                "add_project_to_solution",
                "write_file",
                "dotnet_test"
            }
        };
    }

    private static ToolChainTemplate BuildCMakeProjectBootstrapTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "cmake.project_bootstrap.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_cmake_project",
                "cmake_configure"
            }
        };
    }

    private static ToolChainTemplate BuildCppWin32ShellTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "cpp.win32_shell_scaffold.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_cpp_header_file",
                "create_cpp_source_file"
            }
        };
    }

    private static ToolChainTemplate BuildCppWin32ShellPageSetTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "cpp.win32_shell_page_set.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StepGraph = BuildStepGraph(
                startStepIds:
                [
                    "make_dir",
                    "create_cpp_header_file",
                    "cmake_configure",
                    "cmake_build"
                ],
                stepDefinitions:
                [
                    ("make_dir", "make_dir", "Create grouped-shell native include/state directories."),
                    ("create_cpp_header_file", "create_cpp_header_file", "Create grouped-shell native panel placeholders."),
                    ("cmake_configure", "cmake_configure", "Configure the native grouped-shell build."),
                    ("cmake_build", "cmake_build", "Validate the native grouped-shell build.")
                ],
                transitions:
                [
                    ("make_dir", "create_cpp_header_file"),
                    ("make_dir", "cmake_configure"),
                    ("create_cpp_header_file", "create_cpp_header_file"),
                    ("create_cpp_header_file", "cmake_configure"),
                    ("cmake_configure", "cmake_build")
                ],
                repeatableStepIds:
                [
                    "create_cpp_header_file"
                ],
                optionalStepIds:
                [
                    "make_dir",
                    "create_cpp_header_file",
                    "cmake_configure"
                ],
                terminalStepIds:
                [
                    "cmake_build"
                ]),
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "make_dir",
                "create_cpp_header_file",
                "cmake_configure",
                "cmake_build"
            }
        };
    }

    private static ToolChainTemplate BuildCppConsoleScaffoldTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "cpp.console_scaffold.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_c_source_file",
                "create_cpp_source_file"
            }
        };
    }

    private static ToolChainTemplate BuildCppLibraryScaffoldTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "cpp.library_scaffold.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_cpp_header_file",
                "create_cpp_source_file"
            }
        };
    }

    private static ToolChainTemplate BuildCMakeTargetAttachTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "cmake.target_attach.v1",
            ChainType = ToolChainType.Build,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create_cmake_project",
                "create_cpp_source_file",
                "create_cpp_header_file"
            }
        };
    }

    private static ToolChainTemplate BuildArtifactInspectionTemplate()
    {
        return new ToolChainTemplate
        {
            Name = "artifact_inspection_single_step",
            ChainType = ToolChainType.ArtifactInspection,
            MaxStepCount = 1,
            ModelSummaryAllowed = true,
            StartingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "show_artifacts",
                "show_memory"
            }
        };
    }

    private static string Normalize(string toolName)
    {
        return (toolName ?? "").Trim().ToLowerInvariant();
    }

    private static void EnsureStepGraph(ToolChainTemplate template)
    {
        template.StepGraph ??= new ChainTemplateStepGraph();
        if (template.StepGraph.StepDefinitions.Count == 0)
            template.StepGraph = BuildLegacyStepGraph(template.StartingTools, template.AllowedTransitions);

        template.StartingTools = new HashSet<string>(
            template.StepGraph.StartStepIds
                .Select(stepId => ResolveToolId(template.StepGraph, stepId))
                .Where(toolId => !string.IsNullOrWhiteSpace(toolId)),
            StringComparer.OrdinalIgnoreCase);

        template.AllowedTransitions = template.StepGraph.AllowedTransitions
            .GroupBy(transition => Normalize(transition.FromStepId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(transition => ResolveToolId(template.StepGraph, transition.ToStepId))
                    .Where(toolId => !string.IsNullOrWhiteSpace(toolId))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    }

    private static ChainTemplateStepGraph BuildLegacyStepGraph(
        HashSet<string> startingTools,
        Dictionary<string, HashSet<string>> allowedTransitions)
    {
        var stepIds = startingTools
            .Concat(allowedTransitions.Keys)
            .Concat(allowedTransitions.Values.SelectMany(values => values))
            .Where(stepId => !string.IsNullOrWhiteSpace(stepId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return BuildStepGraph(
            [.. startingTools],
            [.. stepIds.Select(stepId => (stepId, stepId, $"Controlled step `{stepId}`."))],
            [.. allowedTransitions.SelectMany(pair => pair.Value.Select(next => (pair.Key, next)))],
            optionalStepIds: [.. stepIds],
            terminalStepIds: stepIds.Count == 0 ? [] : [.. stepIds]);
    }

    private static ChainTemplateStepGraph BuildStepGraph(
        IEnumerable<string> startStepIds,
        IEnumerable<(string StepId, string ToolId, string Description)> stepDefinitions,
        IEnumerable<(string FromStepId, string ToStepId)> transitions,
        IEnumerable<string>? repeatableStepIds = null,
        IEnumerable<string>? optionalStepIds = null,
        IEnumerable<string>? terminalStepIds = null,
        IEnumerable<string>? implicitStepIds = null)
    {
        var graph = new ChainTemplateStepGraph
        {
            StartStepIds = new HashSet<string>(startStepIds.Select(Normalize), StringComparer.OrdinalIgnoreCase),
            OptionalStepIds = new HashSet<string>((optionalStepIds ?? []).Select(Normalize), StringComparer.OrdinalIgnoreCase),
            TerminalStepIds = new HashSet<string>((terminalStepIds ?? []).Select(Normalize), StringComparer.OrdinalIgnoreCase),
            ImplicitStepIds = new HashSet<string>((implicitStepIds ?? []).Select(Normalize), StringComparer.OrdinalIgnoreCase)
        };

        foreach (var definition in stepDefinitions)
        {
            graph.StepDefinitions.Add(new ChainTemplateStepDefinition
            {
                StepId = Normalize(definition.StepId),
                ToolId = Normalize(definition.ToolId),
                Description = definition.Description ?? ""
            });
        }

        foreach (var transition in transitions)
        {
            graph.AllowedTransitions.Add(new ChainTemplateAllowedTransition
            {
                FromStepId = Normalize(transition.FromStepId),
                ToStepId = Normalize(transition.ToStepId)
            });
        }

        foreach (var stepId in repeatableStepIds ?? [])
        {
            graph.RepeatabilityRules.Add(new ChainTemplateRepeatabilityRule
            {
                StepId = Normalize(stepId),
                IsRepeatable = true
            });
        }

        return graph;
    }

    private static string ResolveStepId(ToolChainTemplate template, string attemptedToolId)
    {
        return template.StepGraph.StepDefinitions
            .FirstOrDefault(definition =>
                string.Equals(definition.ToolId, attemptedToolId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(definition.StepId, attemptedToolId, StringComparison.OrdinalIgnoreCase))
            ?.StepId ?? "";
    }

    private static string ResolveToolId(ChainTemplateStepGraph graph, string stepId)
    {
        return graph.StepDefinitions
            .FirstOrDefault(definition => string.Equals(definition.StepId, Normalize(stepId), StringComparison.OrdinalIgnoreCase))
            ?.ToolId ?? Normalize(stepId);
    }

    private static List<string> ResolveAllowedNextStepIds(ToolChainTemplate template, string previousStepId)
    {
        if (string.IsNullOrWhiteSpace(previousStepId))
            return [.. template.StepGraph.StartStepIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)];

        var allowed = template.StepGraph.AllowedTransitions
            .Where(transition => string.Equals(transition.FromStepId, previousStepId, StringComparison.OrdinalIgnoreCase))
            .Select(transition => transition.ToStepId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (IsRepeatable(template, previousStepId)
            && !allowed.Contains(previousStepId, StringComparer.OrdinalIgnoreCase))
        {
            allowed.Add(previousStepId);
        }

        return allowed
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsRepeatable(ToolChainTemplate template, string stepId)
    {
        return template.StepGraph.RepeatabilityRules.Any(rule =>
            rule.IsRepeatable
            && string.Equals(rule.StepId, stepId, StringComparison.OrdinalIgnoreCase));
    }

    private static ChainTemplateValidationResult BuildValidationSuccess(
        ToolChainTemplate template,
        string attemptedStepId,
        string previousStepId,
        IReadOnlyList<string> allowedNextStepIds)
    {
        return new ChainTemplateValidationResult
        {
            Allowed = true,
            TemplateName = template.Name,
            AttemptedToolId = ResolveToolId(template.StepGraph, attemptedStepId),
            AttemptedStepId = attemptedStepId,
            LastAcceptedStepId = previousStepId,
            AllowedNextStepIds = [.. allowedNextStepIds]
        };
    }

    private static ChainTemplateValidationResult BuildValidationFailure(
        ToolChainTemplate template,
        string attemptedStepId,
        string previousStepId,
        IReadOnlyList<string> allowedNextStepIds,
        ChainTemplateValidationBlockerCode blockerCode,
        ChainTemplateMismatchOrigin mismatchOrigin,
        string message)
    {
        return new ChainTemplateValidationResult
        {
            Allowed = false,
            TemplateName = template.Name,
            AttemptedToolId = ResolveToolId(template.StepGraph, attemptedStepId),
            AttemptedStepId = attemptedStepId,
            LastAcceptedStepId = previousStepId,
            AllowedNextStepIds = [.. allowedNextStepIds],
            BlockerCode = blockerCode,
            MismatchOrigin = mismatchOrigin,
            Message = message
        };
    }

    private static string BuildStartStepNotAllowedMessage(ToolChainTemplate template, string attemptedStepId)
    {
        if (string.Equals(template.Name, "workspace.build_verify.v1", StringComparison.OrdinalIgnoreCase)
            && string.Equals(attemptedStepId, "dotnet_test", StringComparison.OrdinalIgnoreCase))
        {
            return $"Controlled chain blocked: direct test work was routed to build-verify template `{template.Name}`, but that template only allows start step `dotnet_build`; rerouting to a direct dotnet_test path is required. allowed_start_steps=[{FormatList(template.StepGraph.StartStepIds)}].";
        }

        return $"Controlled chain blocked: template `{template.Name}` does not allow start step `{attemptedStepId}`. allowed_start_steps=[{FormatList(template.StepGraph.StartStepIds)}].";
    }

    private static string FormatList(IEnumerable<string> values)
    {
        var list = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        return list.Count == 0 ? "(none)" : string.Join(", ", list);
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
