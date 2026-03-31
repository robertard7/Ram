using RAM.Models;

namespace RAM.Services;

public sealed class FailurePathHarnessService
{
    public IReadOnlyList<FailurePathPromptStep> GetCanonicalFailurePath(bool includeApplyStep = true, bool includeVerifyStep = true)
    {
        var steps = new List<FailurePathPromptStep>
        {
            new()
            {
                Prompt = "run build",
                RequiredState = "known failing workspace target",
                ExpectedArtifactTypesAfterStep = ["build_result", "build_failure_summary", "repair_context"]
            },
            new()
            {
                Prompt = "what broke",
                RequiredState = "recorded failure state",
                ExpectedArtifactTypesAfterStep = ["build_failure_summary", "repair_context"]
            },
            new()
            {
                Prompt = "show me the broken file",
                RequiredState = "repair context with file target",
                ExpectedArtifactTypesAfterStep = ["repair_context"]
            },
            new()
            {
                Prompt = "how should I fix this",
                RequiredState = "recorded failure state",
                ExpectedArtifactTypesAfterStep = ["repair_proposal"]
            },
            new()
            {
                Prompt = "show me the patch",
                RequiredState = "repair proposal",
                ExpectedArtifactTypesAfterStep = ["patch_draft"]
            }
        };

        if (includeApplyStep)
        {
            steps.Add(new FailurePathPromptStep
            {
                Prompt = "apply the fix",
                RequiredState = "safe patch draft",
                ExpectedArtifactTypesAfterStep = ["patch_apply_result"]
            });
        }

        if (includeVerifyStep)
        {
            steps.Add(new FailurePathPromptStep
            {
                Prompt = "did that fix it",
                RequiredState = "applied patch draft",
                ExpectedArtifactTypesAfterStep = ["verification_plan", "verification_result"]
            });
        }

        steps.Add(new FailurePathPromptStep
        {
            Prompt = "what should I do next",
            RequiredState = "latest success, failure, safety abort, or verification state",
            ExpectedArtifactTypesAfterStep = []
        });

        return steps;
    }
}
