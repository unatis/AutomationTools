namespace AutomationTools.Ai;

public sealed record GroqChatRequest(
    string Model,
    double Temperature,
    IReadOnlyList<GroqChatMessage> Messages);

public sealed record GroqChatMessage(string Role, string Content);

public sealed class GroqChatResponse
{
    public List<GroqChatChoice> Choices { get; set; } = new();
    public GroqUsage? Usage { get; set; }
}

public sealed class GroqChatChoice
{
    public GroqChatMessage? Message { get; set; }
}
