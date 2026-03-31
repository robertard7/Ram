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

    public async Task<List<List<float>>> GenerateEmbeddingsAsync(
        string endpoint,
        string model,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Endpoint is required.", nameof(endpoint));

        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        if (inputs is null || inputs.Count == 0)
            throw new ArgumentException("At least one input is required.", nameof(inputs));

        var baseUri = endpoint.TrimEnd('/');

        try
        {
            var embedPayload = new
            {
                model,
                input = inputs
            };

            using var embedContent = new StringContent(
                JsonSerializer.Serialize(embedPayload),
                Encoding.UTF8,
                "application/json");
            using var embedResponse = await Http.PostAsync($"{baseUri}/api/embed", embedContent, cancellationToken).ConfigureAwait(false);
            var embedBody = await embedResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!embedResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Ollama embed returned {(int)embedResponse.StatusCode} {embedResponse.ReasonPhrase}.{Environment.NewLine}{embedBody}");
            }

            using var embedDoc = JsonDocument.Parse(embedBody);
            if (embedDoc.RootElement.TryGetProperty("embeddings", out var embeddingsElement)
                && embeddingsElement.ValueKind == JsonValueKind.Array)
            {
                return embeddingsElement.EnumerateArray()
                    .Select(ParseEmbeddingVector)
                    .ToList();
            }
        }
        catch
        {
            // Fallback to the older single-prompt embeddings endpoint below.
        }

        var fallbackResults = new List<List<float>>();
        foreach (var input in inputs)
        {
            var fallbackPayload = new
            {
                model,
                prompt = input
            };

            using var fallbackContent = new StringContent(
                JsonSerializer.Serialize(fallbackPayload),
                Encoding.UTF8,
                "application/json");
            using var fallbackResponse = await Http.PostAsync($"{baseUri}/api/embeddings", fallbackContent, cancellationToken).ConfigureAwait(false);
            var fallbackBody = await fallbackResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!fallbackResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Ollama embeddings returned {(int)fallbackResponse.StatusCode} {fallbackResponse.ReasonPhrase}.{Environment.NewLine}{fallbackBody}");
            }

            using var fallbackDoc = JsonDocument.Parse(fallbackBody);
            if (!fallbackDoc.RootElement.TryGetProperty("embedding", out var embeddingElement)
                || embeddingElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Ollama embeddings response did not include an embedding vector.");
            }

            fallbackResults.Add(ParseEmbeddingVector(embeddingElement));
        }

        return fallbackResults;
    }

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
        using var response = await Http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

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
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
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

        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

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

    private static List<float> ParseEmbeddingVector(JsonElement element)
    {
        return element.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number)
                ? number
                : (float)0)
            .ToList();
    }
}
