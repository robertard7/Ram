namespace RAM.Models;

public enum ToolChainStopReason
{
    GoalCompleted,
    PolicyBlockedNextStep,
    ManualOnly,
    SafetyBlocked,
    ScopeBlocked,
    ToolFailed,
    InvalidModelStep,
    ChainLimitReached,
    NoFurtherStepAllowed,
    Unknown
}
