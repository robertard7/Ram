namespace RAM.Models;

public sealed class CommandCanonicalizationRecord
{
    public string NormalizationId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string PlanImportId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string WorkItemTitle { get; set; } = "";
    public string RawPhraseText { get; set; } = "";
    public string NormalizedPhraseText { get; set; } = "";
    public string NormalizedOperationKind { get; set; } = "";
    public string NormalizedTargetPath { get; set; } = "";
    public string NormalizedProjectName { get; set; } = "";
    public string NormalizedTemplateHint { get; set; } = "";
    public string TargetRoleHint { get; set; } = "";
    public Dictionary<string, string> NormalizedArguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string NormalizedArgumentTrace { get; set; } = "";
    public string NormalizationTrace { get; set; } = "";
    public string Summary { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}
