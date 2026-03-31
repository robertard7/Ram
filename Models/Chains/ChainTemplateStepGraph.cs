namespace RAM.Models;

public enum ChainTemplateValidationBlockerCode
{
    None,
    StartStepNotAllowed,
    TemplateMissingStepDefinition,
    TemplateTransitionNotAllowed,
    DecompositionEmittedUnknownStep,
    ControllerNormalizedToUndeclaredStep,
    RepeatabilityRuleViolation
}

public enum ChainTemplateMismatchOrigin
{
    None,
    TemplateDefinition,
    DecompositionOutput,
    ControllerNormalization,
    Alignment
}

public sealed class ChainTemplateStepGraph
{
    public List<ChainTemplateStepDefinition> StepDefinitions { get; set; } = [];
    public HashSet<string> StartStepIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ChainTemplateAllowedTransition> AllowedTransitions { get; set; } = [];
    public List<ChainTemplateRepeatabilityRule> RepeatabilityRules { get; set; } = [];
    public HashSet<string> OptionalStepIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> TerminalStepIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ImplicitStepIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ChainTemplateStepDefinition
{
    public string StepId { get; set; } = "";
    public string ToolId { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class ChainTemplateAllowedTransition
{
    public string FromStepId { get; set; } = "";
    public string ToStepId { get; set; } = "";
}

public sealed class ChainTemplateRepeatabilityRule
{
    public string StepId { get; set; } = "";
    public bool IsRepeatable { get; set; }
    public int? MaxOccurrences { get; set; }
}

public sealed class ChainTemplateValidationResult
{
    public bool Allowed { get; set; }
    public string TemplateName { get; set; } = "";
    public string AttemptedStepId { get; set; } = "";
    public string AttemptedToolId { get; set; } = "";
    public string LastAcceptedStepId { get; set; } = "";
    public List<string> AllowedNextStepIds { get; set; } = [];
    public ChainTemplateValidationBlockerCode BlockerCode { get; set; } = ChainTemplateValidationBlockerCode.None;
    public ChainTemplateMismatchOrigin MismatchOrigin { get; set; } = ChainTemplateMismatchOrigin.None;
    public string Message { get; set; } = "";
}
