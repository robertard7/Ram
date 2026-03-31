using System.IO;
using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class NextActionSuggestionService
{
    private readonly ArtifactClassificationService _artifactClassificationService = new();

    public List<NextActionSuggestion> Suggest(
        string workspaceRoot,
        WorkspaceExecutionStateRecord state)
    {
        if (state is null)
            throw new ArgumentNullException(nameof(state));

        if (IsVerificationNewer(state))
            return SuggestFromVerification(state);

        if (IsFailureNewer(state))
            return SuggestFromFailure(workspaceRoot, state);

        return SuggestFromSuccess(state);
    }

    private static List<NextActionSuggestion> SuggestFromVerification(WorkspaceExecutionStateRecord state)
    {
        return state.LastVerificationOutcomeType switch
        {
            "verified_fixed" => SuggestFromVerifiedFixed(state),
            "partially_improved" => SuggestFromPartiallyImproved(state),
            "still_failing" => SuggestFromStillFailing(state),
            "verification_failed" => SuggestFromVerificationFailure(state),
            "not_verifiable" => SuggestFromNotVerifiable(state),
            _ => []
        };
    }

    private List<NextActionSuggestion> SuggestFromFailure(string workspaceRoot, WorkspaceExecutionStateRecord state)
    {
        return state.LastFailureOutcomeType switch
        {
            "test_failure" => SuggestFromTestFailure(workspaceRoot, state),
            "build_failure" => SuggestFromBuildFailure(workspaceRoot, state),
            "resolution_failure" => SuggestFromResolutionFailure(state),
            "execution_failure" or "timed_out" or "output_limit_exceeded" or "safety_blocked" => SuggestFromExecutionFailure(state),
            _ => []
        };
    }

    private List<NextActionSuggestion> SuggestFromTestFailure(string workspaceRoot, WorkspaceExecutionStateRecord state)
    {
        var suggestions = new List<NextActionSuggestion>();
        var parseResult = DeserializeParsedSection<DotnetTestParseResult>(state.LastFailureDataJson);

        if (parseResult?.FailingTests.Count > 0)
        {
            var firstFailure = parseResult.FailingTests[0];
            var sourcePath = FirstNonEmpty(
                firstFailure.ResolvedSourcePath,
                NormalizeWorkspacePath(workspaceRoot, firstFailure.SourceFilePath));
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                var startLine = Math.Max(firstFailure.SourceLine - 10, 1);
                suggestions.Add(new NextActionSuggestion
                {
                    Title = "Inspect the failing test file",
                    ToolName = "read_file_chunk",
                    TargetPath = sourcePath,
                    SuggestedPrompt = $"read lines {startLine} to {startLine + 39} from {sourcePath}",
                    Reason = $"The latest failure points at {sourcePath}:{firstFailure.SourceLine}."
                });
            }
            else if (firstFailure.CandidatePaths.Count > 1)
            {
                suggestions.Add(new NextActionSuggestion
                {
                    Title = "Open the repair context",
                    ToolName = "open_failure_context",
                    SuggestedPrompt = "show failing test file",
                    Reason = "RAM found multiple candidate files for the failing test, so use the repair context to review the ambiguity."
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(state.LastFailureTargetPath))
        {
            suggestions.Add(new NextActionSuggestion
            {
                Title = "Inspect the test target",
                ToolName = "inspect_project",
                TargetPath = state.LastFailureTargetPath,
                SuggestedPrompt = $"inspect {state.LastFailureTargetPath}",
                Reason = "Review the target project or solution that produced the failing test result."
            });
        }

        suggestions.Add(new NextActionSuggestion
        {
            Title = "Review recent changes",
            ToolName = "git_diff",
            TargetPath = "",
            SuggestedPrompt = "show git diff",
            Reason = "Compare the current workspace changes against the latest failure."
        });

        return suggestions.Take(3).ToList();
    }

    private List<NextActionSuggestion> SuggestFromBuildFailure(string workspaceRoot, WorkspaceExecutionStateRecord state)
    {
        var suggestions = new List<NextActionSuggestion>();
        var parseResult = DeserializeParsedSection<DotnetBuildParseResult>(state.LastFailureDataJson);

        if (parseResult?.TopErrors.Count > 0)
        {
            var firstError = parseResult.TopErrors[0];
            var sourcePath = firstError.InsideWorkspace
                ? NormalizeWorkspacePath(workspaceRoot, firstError.FilePath)
                : "";
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                var startLine = Math.Max(firstError.LineNumber - 10, 1);
                suggestions.Add(new NextActionSuggestion
                {
                    Title = "Inspect the first build error",
                    ToolName = "read_file_chunk",
                    TargetPath = sourcePath,
                    SuggestedPrompt = $"read lines {startLine} to {startLine + 39} from {sourcePath}",
                    Reason = $"The top build error points at {sourcePath}:{firstError.LineNumber}."
                });
            }
            else
            {
                suggestions.Add(new NextActionSuggestion
                {
                    Title = "Open the repair context",
                    ToolName = "open_failure_context",
                    SuggestedPrompt = "take me to the first error",
                    Reason = "RAM captured the build error but could not normalize it into a single in-workspace file automatically."
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(state.LastFailureTargetPath))
        {
            if (_artifactClassificationService.IsBuildOrTestTargetPath(state.LastFailureTargetPath))
            {
                suggestions.Add(new NextActionSuggestion
                {
                    Title = "Inspect the build target",
                    ToolName = "inspect_project",
                    TargetPath = state.LastFailureTargetPath,
                    SuggestedPrompt = $"inspect {state.LastFailureTargetPath}",
                    Reason = "Review the project or solution that failed to build."
                });
            }
            else
            {
                suggestions.Add(new NextActionSuggestion
                {
                    Title = "Review the build profile",
                    ToolName = "list_build_profiles",
                    SuggestedPrompt = "list build profiles",
                    Reason = "The latest failure came from a non-.NET build target, so check the selected workspace build profile."
                });
            }
        }

        suggestions.Add(new NextActionSuggestion
        {
            Title = "Review the diff",
            ToolName = "git_diff",
            TargetPath = "",
            SuggestedPrompt = "show git diff",
            Reason = "Recent changes are the fastest way to correlate the build failure."
        });

        return suggestions.Take(3).ToList();
    }

    private static List<NextActionSuggestion> SuggestFromResolutionFailure(WorkspaceExecutionStateRecord state)
    {
        var showBuildProfilesPrompt = string.Equals(state.LastDetectedBuildSystemType, "dotnet", StringComparison.OrdinalIgnoreCase)
            ? "list projects"
            : "list build profiles";
        var showBuildProfilesTool = string.Equals(state.LastDetectedBuildSystemType, "dotnet", StringComparison.OrdinalIgnoreCase)
            ? "list_projects"
            : "list_build_profiles";

        return
        [
            new NextActionSuggestion
            {
                Title = "Inspect available targets",
                ToolName = showBuildProfilesTool,
                SuggestedPrompt = showBuildProfilesPrompt,
                Reason = "The last build or test request failed before execution because RAM could not resolve a target."
            },
            new NextActionSuggestion
            {
                Title = "Show buildable targets",
                ToolName = "inspect_project",
                TargetPath = state.LastFailureTargetPath,
                SuggestedPrompt = string.IsNullOrWhiteSpace(state.LastFailureTargetPath)
                    ? "show build targets"
                    : $"inspect {state.LastFailureTargetPath}",
                Reason = "Use a specific solution or project path on the next build or test request."
            }
        ];
    }

    private static List<NextActionSuggestion> SuggestFromExecutionFailure(WorkspaceExecutionStateRecord state)
    {
        if (string.Equals(state.LastFailureOutcomeType, "safety_blocked_scope", StringComparison.OrdinalIgnoreCase))
        {
            var suggestions = new List<NextActionSuggestion>
            {
                new()
                {
                    Title = "Review safe build targets",
                    ToolName = "list_build_profiles",
                    SuggestedPrompt = "what safe build target can I run",
                    Reason = "The last native build was blocked before launch because RAM assessed the scope as too broad."
                },
                new()
                {
                    Title = "Inspect detected build profiles",
                    ToolName = "list_build_profiles",
                    SuggestedPrompt = "show build profiles",
                    Reason = "Use the detected build profiles to pick a narrower build directory or target."
                }
            };

            if (string.Equals(state.LastFailureToolName, "cmake_build", StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Insert(0, new NextActionSuggestion
                {
                    Title = "Configure CMake first",
                    ToolName = "cmake_configure",
                    SuggestedPrompt = "configure cmake",
                    Reason = "CMake workspaces should prefer a configure step before a narrower bounded build."
                });
            }

            return suggestions.Take(3).ToList();
        }

        if (state.LastFailureOutcomeType is "timed_out" or "output_limit_exceeded" or "safety_blocked")
        {
            var suggestions = new List<NextActionSuggestion>
            {
                new()
                {
                    Title = "Review the build profile",
                    ToolName = "list_build_profiles",
                    SuggestedPrompt = "list build profiles",
                    Reason = "The last run was stopped by RAM's execution safety policy, so confirm the narrowest build target first."
                },
                new()
                {
                    Title = "Review the current diff",
                    ToolName = "git_diff",
                    SuggestedPrompt = "show git diff",
                    Reason = "Inspect the current workspace changes before retrying a smaller or safer build step."
                }
            };

            if (string.Equals(state.LastFailureToolName, "cmake_build", StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Insert(0, new NextActionSuggestion
                {
                    Title = "Configure before rebuilding",
                    ToolName = "cmake_configure",
                    TargetPath = state.LastFailureTargetPath,
                    SuggestedPrompt = "configure cmake",
                    Reason = "If the previous CMake build was too broad or stale, reconfigure the build directory before the next bounded build."
                });
            }

            return suggestions.Take(3).ToList();
        }

        return
        [
            new NextActionSuggestion
            {
                Title = "Retry with the same target",
                ToolName = state.LastFailureToolName,
                TargetPath = state.LastFailureTargetPath,
                SuggestedPrompt = string.IsNullOrWhiteSpace(state.LastFailureTargetPath)
                    ? state.LastFailureToolName.Replace('_', ' ')
                    : $"{state.LastFailureToolName.Replace('_', ' ')} on {state.LastFailureTargetPath}",
                Reason = "The last run did not produce a normal build or test result."
            },
            new NextActionSuggestion
            {
                Title = "Review the workspace diff",
                ToolName = "git_diff",
                SuggestedPrompt = "show git diff",
                Reason = "Check whether local changes explain the execution failure."
            }
        ];
    }

    private static List<NextActionSuggestion> SuggestFromSuccess(WorkspaceExecutionStateRecord state)
    {
        if (string.Equals(state.LastSuccessToolName, "apply_patch_draft", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new NextActionSuggestion
                {
                    Title = "Verify the applied patch",
                    ToolName = "verify_patch_draft",
                    TargetPath = state.LastSuccessTargetPath,
                    SuggestedPrompt = "did that fix it",
                    Reason = "A local patch was applied successfully, so the next deterministic step is verification."
                },
                new NextActionSuggestion
                {
                    Title = "Review the current diff",
                    ToolName = "git_diff",
                    SuggestedPrompt = "show git diff",
                    Reason = "Inspect the exact patch that was applied before or after verification."
                }
            ];
        }

        if (string.Equals(state.LastSuccessToolName, "dotnet_build", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new NextActionSuggestion
                {
                    Title = "Run tests next",
                    ToolName = "dotnet_test",
                    TargetPath = state.LastSuccessTargetPath,
                    SuggestedPrompt = string.IsNullOrWhiteSpace(state.LastSuccessTargetPath)
                        ? "run tests"
                        : $"run dotnet test on {state.LastSuccessTargetPath}",
                    Reason = "The latest build succeeded, so the next local validation step is testing."
                },
                new NextActionSuggestion
                {
                    Title = "Review current changes",
                    ToolName = "git_diff",
                    SuggestedPrompt = "show git diff",
                    Reason = "Check what changed before moving on."
                }
            ];
        }

        if (state.LastSuccessToolName is "cmake_build" or "make_build" or "ninja_build" or "run_build_script")
        {
            return
            [
                new NextActionSuggestion
                {
                    Title = "Review the build profile",
                    ToolName = "list_build_profiles",
                    SuggestedPrompt = "list build profiles",
                    Reason = "The latest build succeeded, so confirm the selected build family and target before the next change."
                },
                new NextActionSuggestion
                {
                    Title = "Review the current diff",
                    ToolName = "git_diff",
                    SuggestedPrompt = "show git diff",
                    Reason = "Inspect the current workspace changes before moving on."
                }
            ];
        }

        if (string.Equals(state.LastSuccessToolName, "dotnet_test", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new NextActionSuggestion
                {
                    Title = "Review the current diff",
                    ToolName = "git_diff",
                    SuggestedPrompt = "show git diff",
                    Reason = "Tests passed, so review the current diff before the next change or commit."
                },
                new NextActionSuggestion
                {
                    Title = "Check workspace status",
                    ToolName = "git_status",
                    SuggestedPrompt = "show git status",
                    Reason = "Confirm the workspace state after the successful test run."
                }
            ];
        }

        return [];
    }

    private static List<NextActionSuggestion> SuggestFromVerifiedFixed(WorkspaceExecutionStateRecord state)
    {
        if (string.Equals(state.LastVerificationToolName, "dotnet_build", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new NextActionSuggestion
                {
                    Title = "Run tests next",
                    ToolName = "dotnet_test",
                    TargetPath = state.LastVerificationTargetPath,
                    SuggestedPrompt = string.IsNullOrWhiteSpace(state.LastVerificationTargetPath)
                        ? "run tests"
                        : $"run dotnet test on {state.LastVerificationTargetPath}",
                    Reason = "The patch fixed the build target, so the next deterministic validation step is testing."
                },
                new NextActionSuggestion
                {
                    Title = "Review the current diff",
                    ToolName = "git_diff",
                    SuggestedPrompt = "show git diff",
                    Reason = "Confirm the exact code change that verified successfully."
                }
            ];
        }

        return
        [
            new NextActionSuggestion
            {
                Title = "Review the current diff",
                ToolName = "git_diff",
                SuggestedPrompt = "show git diff",
                Reason = "The latest verification passed, so review the exact patch before moving on."
            },
            new NextActionSuggestion
            {
                Title = "Check workspace status",
                ToolName = "git_status",
                SuggestedPrompt = "show git status",
                Reason = "Confirm the workspace state after the verified repair."
            }
        ];
    }

    private static List<NextActionSuggestion> SuggestFromPartiallyImproved(WorkspaceExecutionStateRecord state)
    {
        return
        [
            new NextActionSuggestion
            {
                Title = "Open the remaining failure context",
                ToolName = "open_failure_context",
                SuggestedPrompt = "show me the broken file",
                Reason = "The patch helped, but RAM still has a remaining deterministic failure target to inspect."
            },
            new NextActionSuggestion
            {
                Title = "Plan the next repair",
                ToolName = "plan_repair",
                SuggestedPrompt = "how should I fix this",
                Reason = "Use the reduced failure set to draft the next small repair proposal."
            }
        ];
    }

    private static List<NextActionSuggestion> SuggestFromStillFailing(WorkspaceExecutionStateRecord state)
    {
        return
        [
            new NextActionSuggestion
            {
                Title = "Open the current failure file",
                ToolName = "open_failure_context",
                SuggestedPrompt = "open what broke",
                Reason = "The latest verification still fails, so inspect the top remaining failure target."
            },
            new NextActionSuggestion
            {
                Title = "Draft a new repair plan",
                ToolName = "plan_repair",
                SuggestedPrompt = "make a repair plan",
                Reason = "Use the current remaining failure state to generate the next bounded repair plan."
            }
        ];
    }

    private static List<NextActionSuggestion> SuggestFromVerificationFailure(WorkspaceExecutionStateRecord state)
    {
        return
        [
            new NextActionSuggestion
            {
                Title = "Inspect the verification target",
                ToolName = "inspect_project",
                TargetPath = state.LastVerificationTargetPath,
                SuggestedPrompt = string.IsNullOrWhiteSpace(state.LastVerificationTargetPath)
                    ? "show build targets"
                    : $"inspect {state.LastVerificationTargetPath}",
                Reason = "The verification command itself failed, so confirm the target before retrying."
            },
            new NextActionSuggestion
            {
                Title = "Review the current diff",
                ToolName = "git_diff",
                SuggestedPrompt = "show git diff",
                Reason = "Check whether local changes or target selection explain the failed verification run."
            }
        ];
    }

    private static List<NextActionSuggestion> SuggestFromNotVerifiable(WorkspaceExecutionStateRecord state)
    {
        return
        [
            new NextActionSuggestion
            {
                Title = "Inspect the edited file",
                ToolName = "read_file_chunk",
                TargetPath = state.LastSuccessTargetPath,
                SuggestedPrompt = string.IsNullOrWhiteSpace(state.LastSuccessTargetPath)
                    ? "show me the first 40 lines of the current file"
                    : $"show me the first 40 lines of {state.LastSuccessTargetPath}",
                Reason = "RAM could not justify a safe build or test verification target for the applied patch."
            },
            new NextActionSuggestion
            {
                Title = "Review the current diff",
                ToolName = "git_diff",
                SuggestedPrompt = "show git diff",
                Reason = "Use the diff as the next manual verification step when no safe executable check is available."
            }
        ];
    }

    private static T? Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }

    private static bool IsVerificationNewer(WorkspaceExecutionStateRecord state)
    {
        if (string.IsNullOrWhiteSpace(state.LastVerificationUtc))
            return false;

        var verificationUtc = ParseUtc(state.LastVerificationUtc);
        return verificationUtc >= ParseUtc(state.LastFailureUtc)
            && verificationUtc >= ParseUtc(state.LastSuccessUtc);
    }

    private static T? DeserializeParsedSection<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("parsed", out var parsedElement))
                return parsedElement.Deserialize<T>();
        }
        catch
        {
            // Fall back to direct deserialization.
        }

        return Deserialize<T>(json);
    }

    private static string NormalizeWorkspacePath(string workspaceRoot, string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(sourceFilePath))
            return "";

        var fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        var fullPath = Path.IsPathRooted(sourceFilePath)
            ? Path.GetFullPath(sourceFilePath)
            : Path.GetFullPath(Path.Combine(workspaceRoot, sourceFilePath));

        var workspacePrefix = fullWorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!string.Equals(fullPath, fullWorkspaceRoot, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return Path.GetRelativePath(fullWorkspaceRoot, fullPath).Replace('\\', '/');
    }

    private static bool IsFailureNewer(WorkspaceExecutionStateRecord state)
    {
        if (string.IsNullOrWhiteSpace(state.LastFailureUtc))
            return false;

        if (string.IsNullOrWhiteSpace(state.LastSuccessUtc))
            return true;

        return ParseUtc(state.LastFailureUtc) >= ParseUtc(state.LastSuccessUtc);
    }

    private static DateTime ParseUtc(string value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed
            : DateTime.MinValue;
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
