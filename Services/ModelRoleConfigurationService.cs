using RAM.Models;

namespace RAM.Services;

public sealed class ModelRoleConfigurationService
{
    public const string DefaultIntakeModel = "phi3:mini";
    public const string DefaultCoderModel = "qwen2.5:7b-instruct";
    public const string DefaultEmbedderModel = "nomic-embed-text";
    public const string DefaultEmbedderBackend = "qdrant";
    public const string DefaultQdrantEndpoint = "http://localhost:6333";
    public const string DefaultQdrantCollection = "ram";

    private static readonly IReadOnlyList<string> EmbedderBackends =
    [
        DefaultEmbedderBackend
    ];

    public void Normalize(AppSettings settings)
    {
        if (settings is null)
            throw new ArgumentNullException(nameof(settings));

        settings.Endpoint = FirstNonEmpty(settings.Endpoint, "http://localhost:11434");
        settings.CoderModel = FirstNonEmpty(settings.CoderModel, settings.Model, DefaultCoderModel);
        settings.Model = FirstNonEmpty(settings.Model, settings.CoderModel, DefaultCoderModel);
        settings.IntakeModel = FirstNonEmpty(settings.IntakeModel, DefaultIntakeModel);
        settings.EmbedderModel = FirstNonEmpty(settings.EmbedderModel, DefaultEmbedderModel);
        settings.EmbedderBackend = FirstNonEmpty(settings.EmbedderBackend, DefaultEmbedderBackend);
        settings.QdrantEndpoint = FirstNonEmpty(settings.QdrantEndpoint, DefaultQdrantEndpoint);
        settings.QdrantCollection = FirstNonEmpty(settings.QdrantCollection, DefaultQdrantCollection);
    }

    public ModelRoleConfigurationState BuildState(AppSettings settings, IReadOnlyList<OllamaModelInfo>? availableModels)
    {
        Normalize(settings);

        var models = availableModels?.Select(current => new OllamaModelInfo
        {
            Name = current.Name
        }).ToList() ?? [];

        return new ModelRoleConfigurationState
        {
            AvailableModels = models,
            AvailableEmbedderBackends = [.. EmbedderBackends],
            IntakeModel = ResolveSelection(models, settings.IntakeModel),
            CoderModel = ResolveSelection(models, settings.CoderModel),
            EmbedderModel = ResolveSelection(models, settings.EmbedderModel),
            EmbedderBackend = ResolveEmbedderBackend(settings.EmbedderBackend),
            QdrantEndpoint = settings.QdrantEndpoint,
            QdrantCollection = settings.QdrantCollection
        };
    }

    public IReadOnlyList<string> GetEmbedderBackends()
    {
        return EmbedderBackends;
    }

    public string ResolveEmbedderBackend(string? value)
    {
        var requested = FirstNonEmpty(value, DefaultEmbedderBackend);
        return EmbedderBackends.FirstOrDefault(current =>
                   string.Equals(current, requested, StringComparison.OrdinalIgnoreCase))
               ?? DefaultEmbedderBackend;
    }

    private static string ResolveSelection(IReadOnlyList<OllamaModelInfo> availableModels, string preferred)
    {
        if (availableModels.Count == 0)
            return preferred;

        var match = availableModels.FirstOrDefault(current =>
            string.Equals(current.Name, preferred, StringComparison.OrdinalIgnoreCase));
        return match?.Name ?? preferred;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }
}
