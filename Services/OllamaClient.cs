using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RAM.Models;

namespace RAM.Services;

public sealed class OllamaClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    public async Task<string> GenerateAsync(string endpoint, string model, string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Endpoint is required.", nameof(endpoint));

        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt is required.", nameof(prompt));

        var baseUri = endpoint.TrimEnd('/');
        var url = $"{baseUri}/api/generate";

        var payload = new
        {
            model,
            prompt,
            stream = false
        };

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(url, content, cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}.{Environment.NewLine}{responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);

        if (doc.RootElement.TryGetProperty("response", out var responseElement))
        {
            return responseElement.GetString() ?? "";
        }

        return responseBody;
    }

    public async Task<bool> TestConnectionAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        var baseUri = endpoint.TrimEnd('/');
        var url = $"{baseUri}/api/tags";

        try
        {
            using var response = await Http.GetAsync(url, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<OllamaModelInfo>> GetModelsAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Endpoint is required.", nameof(endpoint));

        var baseUri = endpoint.TrimEnd('/');
        var url = $"{baseUri}/api/tags";

        using var response = await Http.GetAsync(url, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}.{Environment.NewLine}{responseBody}");
        }

        var tags = JsonSerializer.Deserialize<OllamaTagsResponse>(responseBody);

        if (tags?.Models is null || tags.Models.Count == 0)
            return new List<OllamaModelInfo>();

        return tags.Models
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new OllamaModelInfo { Name = x.Name })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}