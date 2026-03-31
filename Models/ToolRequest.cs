namespace RAM.Models;

public sealed class ToolRequest
{
    public string ToolName { get; set; } = "";
    public string PreferredChainTemplateName { get; set; } = "";
    public Dictionary<string, string> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Reason { get; set; } = "";
    public ExecutionSourceType ExecutionSourceType { get; set; } = ExecutionSourceType.Unknown;
    public string ExecutionSourceName { get; set; } = "";
    public bool IsAutomaticTrigger { get; set; }
    public bool ExecutionAllowed { get; set; } = true;
    public string ExecutionPolicyMode { get; set; } = "";
    public string ExecutionScopeRiskClassification { get; set; } = "";
    public string ExecutionBuildFamily { get; set; } = "";
    public string TaskboardRunStateId { get; set; } = "";
    public string TaskboardPlanImportId { get; set; } = "";
    public string TaskboardPlanTitle { get; set; } = "";
    public string TaskboardBatchId { get; set; } = "";
    public string TaskboardBatchTitle { get; set; } = "";
    public string TaskboardWorkItemId { get; set; } = "";
    public string TaskboardWorkItemTitle { get; set; } = "";

    public ToolRequest Clone()
    {
        var clone = new ToolRequest
        {
            ToolName = ToolName,
            PreferredChainTemplateName = PreferredChainTemplateName,
            Reason = Reason,
            ExecutionSourceType = ExecutionSourceType,
            ExecutionSourceName = ExecutionSourceName,
            IsAutomaticTrigger = IsAutomaticTrigger,
            ExecutionAllowed = ExecutionAllowed,
            ExecutionPolicyMode = ExecutionPolicyMode,
            ExecutionScopeRiskClassification = ExecutionScopeRiskClassification,
            ExecutionBuildFamily = ExecutionBuildFamily,
            TaskboardRunStateId = TaskboardRunStateId,
            TaskboardPlanImportId = TaskboardPlanImportId,
            TaskboardPlanTitle = TaskboardPlanTitle,
            TaskboardBatchId = TaskboardBatchId,
            TaskboardBatchTitle = TaskboardBatchTitle,
            TaskboardWorkItemId = TaskboardWorkItemId,
            TaskboardWorkItemTitle = TaskboardWorkItemTitle
        };

        foreach (var argument in Arguments)
        {
            clone.Arguments[argument.Key] = argument.Value;
        }

        return clone;
    }

    public bool TryGetArgument(string key, out string value)
    {
        if (Arguments.TryGetValue(key, out var rawValue) && !string.IsNullOrWhiteSpace(rawValue))
        {
            value = rawValue;
            return true;
        }

        value = "";
        return false;
    }
}
