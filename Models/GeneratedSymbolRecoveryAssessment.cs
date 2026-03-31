namespace RAM.Models;

public sealed class GeneratedSymbolRecoveryAssessment
{
    public static GeneratedSymbolRecoveryAssessment None { get; } = new();

    public string ReferencedSymbolName { get; set; } = "";
    public string ReferencedMemberName { get; set; } = "";
    public string Status { get; set; } = "";
    public string Summary { get; set; } = "";
    public string CandidatePath { get; set; } = "";
    public string CandidateNamespace { get; set; } = "";
    public bool CandidateVisibleWithoutEdit { get; set; }
}
