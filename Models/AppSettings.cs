namespace RAM.Models;

public sealed class AppSettings
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5:7b-instruct";
    public string IntakeModel { get; set; } = "";
    public string CoderModel { get; set; } = "";
    public string EmbedderModel { get; set; } = "";
    public string EmbedderBackend { get; set; } = "";
    public string QdrantEndpoint { get; set; } = "";
    public string QdrantCollection { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public bool EnableAdvisoryAgents { get; set; } = true;
    public bool EnableSummaryAgent { get; set; } = true;
    public bool EnableSuggestionAgent { get; set; } = true;
    public bool EnableBuildProfileAgent { get; set; } = true;
    public bool EnablePhraseFamilyAgent { get; set; } = true;
    public bool EnableTemplateSelectorAgent { get; set; } = true;
    public bool EnableForensicsAgent { get; set; } = true;
    public string SummaryAgentModel { get; set; } = "";
    public string SuggestionAgentModel { get; set; } = "";
    public string BuildProfileAgentModel { get; set; } = "";
    public string PhraseFamilyAgentModel { get; set; } = "";
    public string TemplateSelectorAgentModel { get; set; } = "";
    public string ForensicsAgentModel { get; set; } = "";
    public int AgentTimeoutSeconds { get; set; } = 10;
    public bool AutoActivateValidatedTaskboardWhenNoActivePlan { get; set; } = true;
    public bool ConfirmBeforeReplacingActivePlan { get; set; } = true;
    public bool ShowArchivedTaskboards { get; set; }
    public int TaskboardActionMessageDedupeWindowSeconds { get; set; } = 8;
}
