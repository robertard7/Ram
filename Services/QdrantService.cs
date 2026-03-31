using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RAM.Models;

namespace RAM.Services;

public sealed class QdrantService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    public async Task<RamRetrievalBackendStatusRecord> TestConnectionAsync(
        string endpoint,
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        var status = new RamRetrievalBackendStatusRecord
        {
            BackendType = "qdrant",
            Endpoint = NormalizeEndpoint(endpoint),
            CollectionName = collectionName ?? ""
        };

        if (string.IsNullOrWhiteSpace(status.Endpoint))
        {
            status.StatusSummary = "Qdrant endpoint is not configured.";
            return status;
        }

        try
        {
            using var response = await Http.GetAsync(BuildUrl(status.Endpoint, "collections"), cancellationToken).ConfigureAwait(false);
            status.ConnectionOk = response.IsSuccessStatusCode;
            if (!status.ConnectionOk)
            {
                status.StatusSummary = $"Qdrant returned {(int)response.StatusCode} {response.ReasonPhrase}.";
                return status;
            }

            if (string.IsNullOrWhiteSpace(collectionName))
            {
                status.CollectionReady = false;
                status.StatusSummary = "Qdrant connection OK; collection is not configured.";
                return status;
            }

            using var collectionResponse = await Http.GetAsync(
                BuildUrl(status.Endpoint, $"collections/{Uri.EscapeDataString(collectionName)}"),
                cancellationToken).ConfigureAwait(false);
            status.CollectionReady = collectionResponse.IsSuccessStatusCode;
            status.StatusSummary = status.CollectionReady
                ? $"Qdrant connected; collection `{collectionName}` is ready."
                : $"Qdrant connected; collection `{collectionName}` is not ready.";
            return status;
        }
        catch (Exception ex)
        {
            status.StatusSummary = $"Qdrant connection failed: {ex.Message}";
            return status;
        }
    }

    public async Task EnsureCollectionAsync(
        string endpoint,
        string collectionName,
        int vectorSize,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Qdrant endpoint is required.", nameof(endpoint));

        if (string.IsNullOrWhiteSpace(collectionName))
            throw new ArgumentException("Qdrant collection is required.", nameof(collectionName));

        if (vectorSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(vectorSize), "Vector size must be positive.");

        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        using var collectionResponse = await Http.GetAsync(
            BuildUrl(normalizedEndpoint, $"collections/{Uri.EscapeDataString(collectionName)}"),
            cancellationToken).ConfigureAwait(false);
        if (collectionResponse.IsSuccessStatusCode)
            return;

        if (collectionResponse.StatusCode != HttpStatusCode.NotFound)
        {
            var responseBody = await collectionResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Qdrant collection check returned {(int)collectionResponse.StatusCode} {collectionResponse.ReasonPhrase}.{Environment.NewLine}{responseBody}");
        }

        var payload = new
        {
            vectors = new
            {
                size = vectorSize,
                distance = "Cosine"
            }
        };

        using var requestContent = BuildJsonContent(payload);
        using var createResponse = await Http.PutAsync(
            BuildUrl(normalizedEndpoint, $"collections/{Uri.EscapeDataString(collectionName)}"),
            requestContent,
            cancellationToken).ConfigureAwait(false);
        var createBody = await createResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!createResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Qdrant collection creation returned {(int)createResponse.StatusCode} {createResponse.ReasonPhrase}.{Environment.NewLine}{createBody}");
        }
    }

    public async Task UpsertAsync(
        string endpoint,
        string collectionName,
        IReadOnlyList<QdrantPointRecord> points,
        CancellationToken cancellationToken = default)
    {
        if (points.Count == 0)
            return;

        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        var payload = new
        {
            points = points.Select(point => new
            {
                id = point.Id,
                vector = point.Vector,
                payload = point.Payload
            }).ToList()
        };

        using var requestContent = BuildJsonContent(payload);
        using var response = await Http.PutAsync(
            BuildUrl(normalizedEndpoint, $"collections/{Uri.EscapeDataString(collectionName)}/points"),
            requestContent,
            cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Qdrant upsert returned {(int)response.StatusCode} {response.ReasonPhrase}.{Environment.NewLine}{responseBody}");
        }
    }

    public async Task DeleteAsync(
        string endpoint,
        string collectionName,
        IReadOnlyList<string> pointIds,
        CancellationToken cancellationToken = default)
    {
        if (pointIds.Count == 0)
            return;

        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        var payload = new
        {
            points = pointIds
        };

        using var requestContent = BuildJsonContent(payload);
        using var response = await Http.PostAsync(
            BuildUrl(normalizedEndpoint, $"collections/{Uri.EscapeDataString(collectionName)}/points/delete"),
            requestContent,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Qdrant delete returned {(int)response.StatusCode} {response.ReasonPhrase}.{Environment.NewLine}{responseBody}");
        }
    }

    public async Task<List<QdrantSearchHitRecord>> SearchAsync(
        string endpoint,
        string collectionName,
        IReadOnlyList<float> vector,
        int limit,
        Dictionary<string, object?>? filterPayload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Qdrant endpoint is required.", nameof(endpoint));

        if (string.IsNullOrWhiteSpace(collectionName))
            throw new ArgumentException("Qdrant collection is required.", nameof(collectionName));

        if (vector.Count == 0)
            throw new ArgumentException("Query vector is required.", nameof(vector));

        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        var must = new List<object>();
        if (filterPayload is not null)
        {
            foreach (var pair in filterPayload)
            {
                if (pair.Value is null)
                    continue;

                must.Add(new
                {
                    key = pair.Key,
                    match = new
                    {
                        value = pair.Value
                    }
                });
            }
        }

        var payload = new
        {
            vector,
            limit = Math.Max(limit, 1),
            with_payload = true,
            filter = must.Count == 0 ? null : new { must }
        };

        using var requestContent = BuildJsonContent(payload);
        using var response = await Http.PostAsync(
            BuildUrl(normalizedEndpoint, $"collections/{Uri.EscapeDataString(collectionName)}/points/search"),
            requestContent,
            cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Qdrant search returned {(int)response.StatusCode} {response.ReasonPhrase}.{Environment.NewLine}{responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("result", out var resultElement)
            || resultElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var hits = new List<QdrantSearchHitRecord>();
        foreach (var item in resultElement.EnumerateArray())
        {
            var payloadElement = item.TryGetProperty("payload", out var currentPayload)
                ? currentPayload
                : default;
            hits.Add(new QdrantSearchHitRecord
            {
                Id = item.TryGetProperty("id", out var idElement)
                    ? idElement.ToString()
                    : "",
                Score = item.TryGetProperty("score", out var scoreElement) && scoreElement.TryGetDouble(out var score)
                    ? score
                    : 0d,
                Payload = payloadElement.ValueKind == JsonValueKind.Undefined
                    ? []
                    : JsonSerializer.Deserialize<Dictionary<string, object?>>(payloadElement.GetRawText()) ?? []
            });
        }

        return hits;
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        return (endpoint ?? "").Trim().TrimEnd('/');
    }

    private static string BuildUrl(string endpoint, string suffix)
    {
        return $"{NormalizeEndpoint(endpoint)}/{suffix.TrimStart('/')}";
    }

    private static StringContent BuildJsonContent(object payload)
    {
        return new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
    }
}

public sealed class QdrantPointRecord
{
    public string Id { get; set; } = "";
    public List<float> Vector { get; set; } = [];
    public Dictionary<string, object?> Payload { get; set; } = [];
}

public sealed class QdrantSearchHitRecord
{
    public string Id { get; set; } = "";
    public double Score { get; set; }
    public Dictionary<string, object?> Payload { get; set; } = [];
}
