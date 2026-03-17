namespace AutomationTools.Ai;

public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public string? Provider { get; set; }
    public string? Model { get; set; }
    public double Temperature { get; set; } = 0.2;
    public string? EmbeddingProvider { get; set; }
    public string? EmbeddingModel { get; set; }
    public string? EmbeddingBaseUrl { get; set; }
    public string? LlmBaseUrl { get; set; }
}
