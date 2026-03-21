using System.Text.Json.Serialization;

namespace RAM.Models;

public sealed class OllamaTagsResponse
{
    [JsonPropertyName("models")]
    public List<OllamaTagModel> Models { get; set; } = [];
}

public sealed class OllamaTagModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}