namespace RAM.Models;

public sealed class WorkspaceBuildResolution
{
    public bool Success { get; private set; }
    public string RequestedTarget { get; private set; } = "";
    public string NormalizedTarget { get; private set; } = "";
    public string Message { get; private set; } = "";
    public string FailureKind { get; private set; } = "";
    public string ReasonCode { get; private set; } = "";
    public bool PrerequisiteRequired { get; private set; }
    public WorkspaceBuildItem? Item { get; private set; }
    public List<WorkspaceBuildItem> Candidates { get; private set; } = [];

    public static WorkspaceBuildResolution SuccessResult(
        WorkspaceBuildItem item,
        string requestedTarget,
        string normalizedTarget,
        string message)
    {
        return new WorkspaceBuildResolution
        {
            Success = true,
            RequestedTarget = requestedTarget ?? "",
            NormalizedTarget = normalizedTarget ?? "",
            Message = message ?? "",
            Item = item
        };
    }

    public static WorkspaceBuildResolution FailureResult(
        string requestedTarget,
        string normalizedTarget,
        string message,
        IEnumerable<WorkspaceBuildItem>? candidates = null,
        string failureKind = "",
        string reasonCode = "",
        bool prerequisiteRequired = false)
    {
        return new WorkspaceBuildResolution
        {
            Success = false,
            RequestedTarget = requestedTarget ?? "",
            NormalizedTarget = normalizedTarget ?? "",
            Message = message ?? "",
            FailureKind = failureKind ?? "",
            ReasonCode = reasonCode ?? "",
            PrerequisiteRequired = prerequisiteRequired,
            Candidates = candidates?.ToList() ?? []
        };
    }
}
