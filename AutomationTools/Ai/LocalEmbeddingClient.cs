using System.Text;
using System.Text.Json;

namespace AutomationTools.Ai;

public sealed class LocalEmbeddingClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly AiOptions _options;

    public LocalEmbeddingClient(HttpClient http, AiOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        if (!string.Equals(_options.EmbeddingProvider, "local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Embedding provider is not configured for local embeddings.");

        if (string.IsNullOrWhiteSpace(_options.EmbeddingModel))
            throw new InvalidOperationException("Embedding model is not configured.");

        if (_http.BaseAddress is null)
            throw new InvalidOperationException("Embedding base URL is not configured.");

        var payload = new EmbeddingRequest(_options.EmbeddingModel!, inputs);
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        using var req = new HttpRequestMessage(HttpMethod.Post, "embeddings")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Embedding error: {(int)resp.StatusCode} {resp.ReasonPhrase} - {body}");

        var parsed = JsonSerializer.Deserialize<EmbeddingResponse>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (parsed?.Data is null || parsed.Data.Count == 0)
            return Array.Empty<float[]>();

        return parsed.Data.Select(d => d.Embedding.Select(v => (float)v).ToArray()).ToList();
    }

    public static void ConfigureHttpClient(HttpClient http, AiOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.EmbeddingBaseUrl))
            http.BaseAddress = new Uri(options.EmbeddingBaseUrl, UriKind.Absolute);
    }

    private sealed record EmbeddingRequest(string Model, IReadOnlyList<string> Input);

    private sealed record EmbeddingResponse(IReadOnlyList<EmbeddingData> Data);

    private sealed record EmbeddingData(IReadOnlyList<double> Embedding);
}
