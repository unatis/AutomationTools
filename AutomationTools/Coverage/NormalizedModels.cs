namespace AutomationTools.Coverage;

public sealed record NormalizedEntity(
    string Id,
    string Type, // manual_test | automation_test
    string Title,
    string? Feature,
    string? Screen,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Assertions,
    IReadOnlyList<string> Behaviors,
    Dictionary<string, string> Metadata);

