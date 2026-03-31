using RAM.Models;

namespace RAM.Services;

public sealed class ResponseModeSelectionService
{
    private readonly ToolChainTemplateRegistry _toolChainTemplateRegistry = new();

    public ResponseModeSelectionResult Select(
        string prompt,
        BuilderRequestKind requestKind,
        ResolvedUserIntent? resolvedIntent)
    {
        if (resolvedIntent is not null)
            return SelectFromToolRequest(resolvedIntent.ToolRequest);

        if (requestKind == BuilderRequestKind.BuildRequest)
        {
            return new ResponseModeSelectionResult
            {
                Mode = ResponseMode.ModelAllowed,
                Reason = "Builder request detected, so model response mode remains allowed."
            };
        }

        var normalized = Normalize(prompt);
        if (LooksLikeChainRequest(normalized))
        {
            return new ResponseModeSelectionResult
            {
                Mode = ResponseMode.ChainRequired,
                Reason = "Operational request matched a controlled chain class, so free-form model text is not allowed."
            };
        }

        if (LooksLikeToolRequest(normalized, requestKind))
        {
            return new ResponseModeSelectionResult
            {
                Mode = ResponseMode.ToolRequired,
                Reason = "Operational request looks tool-backed, so RAM expects strict tool output if the model is consulted."
            };
        }

        return new ResponseModeSelectionResult
        {
            Mode = requestKind == BuilderRequestKind.ToolLikely
                ? ResponseMode.ModelOptional
                : ResponseMode.ModelAllowed,
            Reason = requestKind == BuilderRequestKind.ToolLikely
                ? "Prompt looks operational but was not resolved deterministically, so model output remains optional."
                : "Prompt is eligible for normal model response mode."
        };
    }

    private ResponseModeSelectionResult SelectFromToolRequest(ToolRequest request)
    {
        var template = !string.IsNullOrWhiteSpace(request.PreferredChainTemplateName)
            ? _toolChainTemplateRegistry.ResolveTemplateForName(request.PreferredChainTemplateName)
            : _toolChainTemplateRegistry.ResolveTemplate(request.ToolName);
        var mode = template.ChainType is ToolChainType.Repair
            or ToolChainType.Verification
            or ToolChainType.AutoValidation
            || template.MaxStepCount > 1
                ? ResponseMode.ChainRequired
                : ResponseMode.ToolRequired;

        return new ResponseModeSelectionResult
        {
            Mode = mode,
            Reason = $"Deterministic routing selected `{request.ToolName}`, so response mode is {FormatResponseMode(mode)}."
        };
    }

    private static bool LooksLikeChainRequest(string normalizedPrompt)
    {
        return normalizedPrompt.Contains("how should i fix this", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("suggest a fix", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("make a repair plan", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("plan the fix", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("show me the patch", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("preview the fix", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("apply the fix", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("verify the patch", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("did that fix it", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("detect build system", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("what build system is this", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("how do i build this repo", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("list build profiles", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("show build profiles", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeToolRequest(string normalizedPrompt, BuilderRequestKind requestKind)
    {
        if (requestKind == BuilderRequestKind.ToolLikely)
            return true;

        return normalizedPrompt.Contains("write file", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("write to", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("replace ", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("append ", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("read file", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("show file info", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("make a folder", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("create directory", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("search for files", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("search for ", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("git status", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("git diff", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("run build", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("run tests", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("dotnet build", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("dotnet test", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("run make", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("run ninja", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("configure cmake", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string prompt)
    {
        return (prompt ?? "").Trim().ToLowerInvariant();
    }

    private static string FormatResponseMode(ResponseMode mode)
    {
        return mode switch
        {
            ResponseMode.ToolRequired => "tool_required",
            ResponseMode.ChainRequired => "chain_required",
            ResponseMode.SummaryOnly => "summary_only",
            ResponseMode.ModelAllowed => "model_allowed",
            ResponseMode.ModelOptional => "model_optional",
            _ => "none"
        };
    }
}
