using RAM.Models;

namespace RAM.Services;

public sealed class ExecutionGateService
{
    private static readonly HashSet<string> NonExecutableModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "manual_only",
        "not_applicable",
        "scope_blocked",
        "safety_blocked",
        "blocked_unknown"
    };

    public ExecutionGateDecision Evaluate(ExecutionGateRequest request)
    {
        if (request is null)
            return Block(new ExecutionGateDecision(), "Execution gate: blocked unknown execution source; no external command launched.");

        var decision = new ExecutionGateDecision
        {
            SourceType = request.SourceType,
            SourceName = request.SourceName,
            CommandFamily = request.CommandFamily,
            BuildFamily = request.BuildFamily,
            PolicyMode = request.PolicyMode,
            ScopeRiskClassification = request.ScopeRiskClassification,
            IsAutomaticTrigger = request.IsAutomaticTrigger
        };

        if (request.SourceType == ExecutionSourceType.Unknown)
        {
            return Block(
                decision,
                "Execution gate: blocked unknown execution source; no external command launched.");
        }

        if (request.IsAutomaticTrigger)
        {
            if (string.IsNullOrWhiteSpace(request.PolicyMode))
            {
                return Block(
                    decision,
                    $"Execution gate: blocked {FormatSource(request.SourceType)} for {DisplayValue(request.SourceName)} because policy mode is missing.");
            }

            if (!request.ExecutionAllowed)
            {
                return Block(
                    decision,
                    $"Execution gate: blocked {FormatSource(request.SourceType)} for {DisplayValue(request.SourceName)} because execution is not allowed by the current plan.");
            }

            if (NonExecutableModes.Contains(request.PolicyMode))
            {
                return Block(
                    decision,
                    $"Execution gate: blocked {FormatSource(request.SourceType)} for {DisplayValue(request.SourceName)} because policy mode is {request.PolicyMode}.");
            }
        }
        else if (!request.ExecutionAllowed)
        {
            return Block(
                decision,
                $"Execution gate: blocked {FormatSource(request.SourceType)} for {DisplayValue(request.SourceName)} because execution was not explicitly approved.");
        }

        decision.IsAllowed = true;
        decision.Summary =
            $"allowed {FormatSource(request.SourceType)} for {DisplayValue(request.SourceName)} "
            + $"(command family={DisplayValue(request.CommandFamily)}, build family={DisplayValue(request.BuildFamily)}, policy={DisplayValue(request.PolicyMode)}).";
        return decision;
    }

    private static ExecutionGateDecision Block(ExecutionGateDecision decision, string reason)
    {
        decision.IsAllowed = false;
        decision.BlockedReason = reason;
        decision.Summary = reason;
        return decision;
    }

    private static string FormatSource(ExecutionSourceType sourceType)
    {
        return sourceType switch
        {
            ExecutionSourceType.ManualUserRequest => "manual_user_request",
            ExecutionSourceType.AutoValidation => "auto_validation",
            ExecutionSourceType.Verification => "verification",
            ExecutionSourceType.BuildTool => "build_tool",
            _ => "unknown"
        };
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
