namespace RAM.Models;

public sealed class ModelRoleConfigurationState
{
    public List<OllamaModelInfo> AvailableModels { get; set; } = [];
    public List<string> AvailableEmbedderBackends { get; set; } = [];
    public string IntakeModel { get; set; } = "";
    public string CoderModel { get; set; } = "";
    public string EmbedderModel { get; set; } = "";
    public string EmbedderBackend { get; set; } = "";
    public string QdrantEndpoint { get; set; } = "";
    public string QdrantCollection { get; set; } = "";
}
