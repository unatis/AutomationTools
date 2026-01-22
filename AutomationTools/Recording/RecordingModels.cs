using System.Text.Json.Serialization;
using System.Text.Json;

namespace AutomationTools.Recording;

public sealed class RecordingPayload
{
    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("exportedAt")]
    public long? ExportedAt { get; set; }

    [JsonPropertyName("test")]
    public RecordingTest? Test { get; set; }
}

public sealed class RecordingTest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("startUrl")]
    public string? StartUrl { get; set; }

    [JsonPropertyName("createdAt")]
    public long? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public long? UpdatedAt { get; set; }

    [JsonPropertyName("steps")]
    public List<RecordingStep>? Steps { get; set; }
}

public sealed class RecordingStep
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("ts")]
    public long? Ts { get; set; }

    [JsonPropertyName("locators")]
    public RecordingLocators? Locators { get; set; }

    [JsonPropertyName("meta")]
    public JsonElement? Meta { get; set; }

    // input/select
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("optionText")]
    public string? OptionText { get; set; }

    [JsonPropertyName("committedBy")]
    public string? CommittedBy { get; set; }

    // scroll
    [JsonPropertyName("x")]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    public double? Y { get; set; }

    // keydown
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("ctrl")]
    public bool? Ctrl { get; set; }

    [JsonPropertyName("alt")]
    public bool? Alt { get; set; }

    [JsonPropertyName("shift")]
    public bool? Shift { get; set; }

    [JsonPropertyName("repeat")]
    public bool? Repeat { get; set; }
}

public sealed class RecordingLocators
{
    [JsonPropertyName("aria")]
    public string? Aria { get; set; }

    [JsonPropertyName("css")]
    public string? Css { get; set; }

    [JsonPropertyName("xpath")]
    public string? XPath { get; set; }
}


