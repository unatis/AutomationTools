using System.Text;
using System.Text.Json;

namespace AutomationTools.Ai;

public sealed class LocalLlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly AiOptions _options;

    public LocalLlmClient(HttpClient http, AiOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<string> ChatAsync(string userPrompt, string? systemPrompt, CancellationToken ct)
    {
        EnsureConfigured();

        var messages = new List<GroqChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new GroqChatMessage("system", systemPrompt));

        messages.Add(new GroqChatMessage("user", userPrompt));

        var payload = new GroqChatRequest(_options.Model!, _options.Temperature, messages);
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Local LLM error: {(int)resp.StatusCode} {resp.ReasonPhrase} - {body}");

        var parsed = JsonSerializer.Deserialize<GroqChatResponse>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        var content = parsed?.Choices.FirstOrDefault()?.Message?.Content;
        return content ?? string.Empty;
    }

    public async Task<GroqChatResult> ChatWithUsageAsync(string userPrompt, string? systemPrompt, CancellationToken ct)
    {
        var content = await ChatAsync(userPrompt, systemPrompt, ct);
        return new GroqChatResult(content, 0, 0, 0);
    }

    public static void ConfigureHttpClient(HttpClient http, AiOptions options)
    {
        var baseUrl = options.LlmBaseUrl ?? options.EmbeddingBaseUrl;
        if (!string.IsNullOrWhiteSpace(baseUrl))
            http.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
    }

    private void EnsureConfigured()
    {
        if (!string.Equals(_options.Provider, "local", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_options.Provider, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("AI provider is not configured for local LLM.");
        }

        if (string.IsNullOrWhiteSpace(_options.Model))
            throw new InvalidOperationException("AI model is not configured.");

        if (_http.BaseAddress is null)
            throw new InvalidOperationException("LLM base URL is not configured.");
    }
}
