using System.Text;
using RAM.Models;

namespace RAM.Services;

public sealed class ToolRegistryService
{
    private readonly List<ToolDefinition> _tools =
    [
        new()
        {
            Name = "list_folder",
            Description = "List folders and files for a workspace path.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path"
        },
        new()
        {
            Name = "read_file",
            Description = "Read a text file inside the workspace.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path"
        },
        new()
        {
            Name = "save_output",
            Description = "Save text output to a workspace file and persist it as an artifact.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, content"
        },
        new()
        {
            Name = "show_artifacts",
            Description = "List recent saved artifacts for the current workspace.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "none"
        },
        new()
        {
            Name = "show_memory",
            Description = "List recent memory summaries for the current workspace.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "none"
        },
        new()
        {
            Name = "list_projects",
            Description = "List build-relevant workspace files such as solutions, projects, and Directory.Build files.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "kind"
        },
        new()
        {
            Name = "detect_build_system",
            Description = "Detect supported workspace build systems such as dotnet, cmake, make, ninja, or repo-local scripts.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "none"
        },
        new()
        {
            Name = "list_build_profiles",
            Description = "List detected build profiles for the current workspace and show which one RAM would use plus the current safe live-build guidance.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "none"
        },
        new()
        {
            Name = "inspect_project",
            Description = "Inspect a solution or project by relative path or unique name and show local build and test targets.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path"
        },
        new()
        {
            Name = "search_files",
            Description = "Find workspace file names by substring and return a bounded list.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "pattern"
        },
        new()
        {
            Name = "search_text",
            Description = "Search workspace file text and return bounded path:line snippets.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "pattern"
        },
        new()
        {
            Name = "file_info",
            Description = "Show bounded metadata for a workspace file without reading the full content.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path"
        },
        new()
        {
            Name = "read_file_chunk",
            Description = "Read a bounded line range from a workspace text file.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, start_line, line_count"
        },
        new()
        {
            Name = "create_file",
            Description = "Create a new workspace file. Fails if the file already exists.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, content"
        },
        new()
        {
            Name = "write_file",
            Description = "Create or overwrite a workspace file with new text.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, content, file_role, pattern, implementation_depth, followthrough_mode, target_project, namespace, class_name, base_types, interfaces, constructor_dependencies, required_usings, supporting_surfaces, completion_contract, modification_intent, feature_name, verification_requirements, registration_surface, test_update_scope, namespace_constraints, dependency_update_requirements, preserve_constraints, validation"
        },
        new()
        {
            Name = "append_file",
            Description = "Append text to an existing workspace file.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, content"
        },
        new()
        {
            Name = "replace_in_file",
            Description = "Replace exact old_text with new_text in an existing workspace file without regex.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, old_text, new_text"
        },
        new()
        {
            Name = "make_dir",
            Description = "Create a workspace directory if it does not exist.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path"
        },
        new()
        {
            Name = "create_dotnet_solution",
            Description = "Create a workspace-scoped .NET solution with a deterministic solution name.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "solution_name, working_directory, timeout_seconds"
        },
        new()
        {
            Name = "create_dotnet_project",
            Description = "Create a workspace-scoped .NET project from a deterministic template and output path.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "template, project_name, output_path, solution_path, role, target_framework, template_switches, working_directory, timeout_seconds"
        },
        new()
        {
            Name = "add_project_to_solution",
            Description = "Add a workspace project to a workspace solution using deterministic relative paths only.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "solution_path, project_path, working_directory, timeout_seconds"
        },
        new()
        {
            Name = "add_dotnet_project_reference",
            Description = "Add a workspace project reference using deterministic relative project paths only.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "project_path, reference_path, working_directory, timeout_seconds"
        },
        new()
        {
            Name = "create_dotnet_page_view",
            Description = "Create or overwrite a bounded .NET page or view file inside the workspace from deterministic content.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, content, file_role, pattern, implementation_depth, followthrough_mode, target_project, namespace, class_name, required_usings, supporting_surfaces, completion_contract, modification_intent, feature_name, verification_requirements, registration_surface, test_update_scope, namespace_constraints, dependency_update_requirements, preserve_constraints, validation"
        },
        new()
        {
            Name = "create_dotnet_viewmodel",
            Description = "Create or overwrite a bounded .NET viewmodel file inside the workspace from deterministic content.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, content, file_role, pattern, implementation_depth, followthrough_mode, target_project, namespace, class_name, base_types, interfaces, constructor_dependencies, required_usings, supporting_surfaces, completion_contract, modification_intent, feature_name, verification_requirements, registration_surface, test_update_scope, namespace_constraints, dependency_update_requirements, preserve_constraints, validation"
        },
        new()
        {
            Name = "register_navigation",
            Description = "Create or overwrite a deterministic navigation registration file inside the workspace.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, content"
        },
        new()
        {
            Name = "register_di_service",
            Description = "Create or overwrite a deterministic dependency-registration file inside the workspace.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, content"
        },
        new()
        {
            Name = "initialize_sqlite_storage_boundary",
            Description = "Create or overwrite bounded workspace files for a SQLite storage boundary without running external commands.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, content"
        },
        new()
        {
            Name = "create_cmake_project",
            Description = "Create or overwrite a deterministic CMake project file inside the workspace.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, content"
        },
        new()
        {
            Name = "create_cpp_source_file",
            Description = "Create or overwrite a deterministic C++ source file inside the workspace.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, content"
        },
        new()
        {
            Name = "create_cpp_header_file",
            Description = "Create or overwrite a deterministic C++ header file inside the workspace.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, content"
        },
        new()
        {
            Name = "create_c_source_file",
            Description = "Create or overwrite a deterministic C source file inside the workspace.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, content"
        },
        new()
        {
            Name = "create_c_header_file",
            Description = "Create or overwrite a deterministic C header file inside the workspace.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, content"
        },
        new()
        {
            Name = "run_command",
            Description = "Run a bounded workspace command. Supports only dotnet, git, echo, dir, or ls.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "command, arguments, working_directory, timeout_seconds"
        },
        new()
        {
            Name = "git_status",
            Description = "Run git status for the workspace or a workspace subdirectory.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "working_directory, timeout_seconds"
        },
        new()
        {
            Name = "git_diff",
            Description = "Run git diff and return a bounded diff for the workspace or one path.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, working_directory, timeout_seconds"
        },
        new()
        {
            Name = "dotnet_build",
            Description = "Run dotnet build for the workspace or a target project or solution.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "project, configuration, working_directory, timeout_seconds"
        },
        new()
        {
            Name = "dotnet_test",
            Description = "Run dotnet test for the workspace or a target project or solution.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "project, filter, working_directory, timeout_seconds"
        },
        new()
        {
            Name = "cmake_configure",
            Description = "Run bounded CMake configure inside the workspace using deterministic source and build directories.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "source_dir, build_dir, generator, configuration, timeout_seconds"
        },
        new()
        {
            Name = "cmake_build",
            Description = "Run bounded CMake build only for a narrow build directory or explicit target. Broad default native builds are blocked.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "build_dir, target, configuration, timeout_seconds"
        },
        new()
        {
            Name = "make_build",
            Description = "Run bounded make only for a narrow workspace directory or explicit target. Broad default native builds are blocked.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "directory, target, timeout_seconds"
        },
        new()
        {
            Name = "ninja_build",
            Description = "Run bounded ninja only for a narrow build directory or explicit target. Broad default native builds are blocked.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "directory, target, timeout_seconds"
        },
        new()
        {
            Name = "run_build_script",
            Description = "Run a detected repo-local build script only when RAM can justify a narrow safe script scope. Broad script runs are blocked.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path, script_arguments, timeout_seconds"
        },
        new()
        {
            Name = "ctest_run",
            Description = "Run bounded CTest only for a narrow workspace build directory or explicit configuration.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "directory, configuration, timeout_seconds"
        },
        new()
        {
            Name = "open_failure_context",
            Description = "Open the best-known file and line from the latest build or test failure using stored repair context.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "scope"
        },
        new()
        {
            Name = "plan_repair",
            Description = "Build a bounded repair proposal from the latest failure context and the best-known target file.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "scope, path"
        },
        new()
        {
            Name = "preview_patch_draft",
            Description = "Preview a file-scoped patch draft from the latest repair proposal or current failure context without modifying files.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "scope, path"
        },
        new()
        {
            Name = "apply_patch_draft",
            Description = "Apply the latest safe file-scoped patch draft only when the saved excerpt still matches the current file.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path"
        },
        new()
        {
            Name = "verify_patch_draft",
            Description = "Verify the latest applied patch draft by rerunning the narrowest safe build or test target and comparing before versus after.",
            RiskLevel = ToolRiskLevel.Safe,
            ArgumentsDescription = "path"
        }
    ];

    public IReadOnlyList<ToolDefinition> GetAvailableTools()
    {
        return _tools;
    }

    public bool HasTool(string name)
    {
        return GetToolDefinition(name) is not null;
    }

    public ToolDefinition? GetToolDefinition(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return _tools.FirstOrDefault(x =>
            string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public string BuildPromptToolBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Available tools (workspace sandbox only):");

        foreach (var tool in _tools)
        {
            sb.Append("- ");
            sb.Append(tool.Name);
            sb.Append(": ");
            sb.Append(tool.Description);
            sb.Append(" Args: ");
            sb.Append(tool.ArgumentsDescription);
            sb.AppendLine(".");
        }

        sb.AppendLine("All listed tools run only inside the active workspace. Prefer specific tools before run_command.");
        return sb.ToString().TrimEnd();
    }
}
