using System.Text.Json.Serialization;

namespace RAM.Models;

public sealed class PatchApplyResultRecord
{
    [JsonPropertyName("applied_utc")]
    public string AppliedUtc { get; set; } = "";

    public PatchDraftRecord Draft { get; set; } = new();

    [JsonPropertyName("apply_output")]
    public string ApplyOutput { get; set; } = "";

    [JsonPropertyName("patch_contract_id")]
    public string PatchContractId { get; set; } = "";

    [JsonPropertyName("patch_contract_artifact_relative_path")]
    public string PatchContractArtifactRelativePath { get; set; } = "";

    [JsonPropertyName("patch_plan_id")]
    public string PatchPlanId { get; set; } = "";

    [JsonPropertyName("patch_plan_artifact_relative_path")]
    public string PatchPlanArtifactRelativePath { get; set; } = "";
}
