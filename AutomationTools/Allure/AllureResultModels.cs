using System.Text.Json.Serialization;

namespace AutomationTools.Allure;

public sealed class AllureResult
{
    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("steps")]
    public List<AllureStep>? Steps { get; set; }

    [JsonPropertyName("labels")]
    public List<AllureLabel>? Labels { get; set; }
}

public sealed class AllureStep
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("steps")]
    public List<AllureStep>? Steps { get; set; }

    [JsonPropertyName("parameters")]
    public List<AllureParam>? Parameters { get; set; }
}

public sealed class AllureParam
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

public sealed class AllureLabel
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}



