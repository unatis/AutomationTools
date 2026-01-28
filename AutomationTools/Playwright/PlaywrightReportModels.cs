namespace AutomationTools.Playwright;

public sealed class PlaywrightReport
{
    public List<PlaywrightSuite> Suites { get; set; } = new();
}

public sealed class PlaywrightSuite
{
    public string? Title { get; set; }
    public string? File { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public List<PlaywrightSuite> Suites { get; set; } = new();
    public List<PlaywrightSpec> Specs { get; set; } = new();
}

public sealed class PlaywrightSpec
{
    public string? Title { get; set; }
    public string? File { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public List<PlaywrightTest> Tests { get; set; } = new();
}

public sealed class PlaywrightTest
{
    public string? ExpectedStatus { get; set; }
    public string? ProjectName { get; set; }
    public List<PlaywrightResult> Results { get; set; } = new();
}

public sealed class PlaywrightResult
{
    public string? Status { get; set; }
    public int Duration { get; set; }
    public PlaywrightError? Error { get; set; }
    public List<PlaywrightError> Errors { get; set; } = new();
    public List<PlaywrightStep> Steps { get; set; } = new();
    public List<PlaywrightAttachment> Attachments { get; set; } = new();
    public string? StartTime { get; set; }
}

public sealed class PlaywrightStep
{
    public string? Title { get; set; }
    public List<PlaywrightStep> Steps { get; set; } = new();
}

public sealed class PlaywrightError
{
    public string? Message { get; set; }
    public string? Stack { get; set; }
    public PlaywrightLocation? Location { get; set; }
    public string? Snippet { get; set; }
}

public sealed class PlaywrightLocation
{
    public string? File { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
}

public sealed class PlaywrightAttachment
{
    public string? Name { get; set; }
    public string? ContentType { get; set; }
    public string? Path { get; set; }
}
