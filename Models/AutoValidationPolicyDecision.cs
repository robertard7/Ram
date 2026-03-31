namespace RAM.Models;

public sealed class AutoValidationPolicyDecision
{
    public AutoValidationPolicyMode Mode { get; set; } = AutoValidationPolicyMode.NotApplicable;
    public string Reason { get; set; } = "";
    public string SuggestedNextStep { get; set; } = "";
}
