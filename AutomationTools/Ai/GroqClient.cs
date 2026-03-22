using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AutomationTools.Ai;

public sealed class GroqClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly AiOptions _options;

    public GroqClient(HttpClient http, AiOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<string> ChatAsync(string userPrompt, string? systemPrompt, CancellationToken ct)
    {
        if (!string.Equals(_options.Provider, "groq", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AI provider is not configured for Groq.");

        if (string.IsNullOrWhiteSpace(_options.Model))
            throw new InvalidOperationException("AI model is not configured.");

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
            throw new InvalidOperationException($"Groq error: {(int)resp.StatusCode} {resp.ReasonPhrase} - {body}");

        var parsed = JsonSerializer.Deserialize<GroqChatResponse>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        var content = parsed?.Choices.FirstOrDefault()?.Message?.Content;
        return content ?? string.Empty;
    }

    public async Task<GroqChatResult> ChatWithUsageAsync(string userPrompt, string? systemPrompt, CancellationToken ct)
    {
        if (!string.Equals(_options.Provider, "groq", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AI provider is not configured for Groq.");

        if (string.IsNullOrWhiteSpace(_options.Model))
            throw new InvalidOperationException("AI model is not configured.");

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
            throw new InvalidOperationException($"Groq error: {(int)resp.StatusCode} {resp.ReasonPhrase} - {body}");

        var parsed = JsonSerializer.Deserialize<GroqChatResponse>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        var content = parsed?.Choices.FirstOrDefault()?.Message?.Content ?? string.Empty;
        var usage = parsed?.Usage;
        return new GroqChatResult(content, usage?.PromptTokens ?? 0, usage?.CompletionTokens ?? 0, usage?.TotalTokens ?? 0);
    }

    public static void ConfigureHttpClient(HttpClient http, string? apiKey)
    {
        http.BaseAddress = new Uri("https://api.groq.com/openai/v1/");
        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }
}

public sealed record GroqChatResult(string Content, int PromptTokens, int CompletionTokens, int TotalTokens);

public sealed class GroqUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
}
