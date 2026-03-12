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

public sealed record RuleMatch(
    string SourceId,
    string TargetId,
    string Rule,
    double Score);

public sealed record VectorMatch(
    string SourceId,
    string TargetId,
    double Score);

public sealed record CandidateEntity(
    NormalizedEntity Entity,
    string MatchOrigin, // rule | embedding | rule+embedding
    double Score);

public sealed record LlmCandidatePayload(
    string Id,
    string Type,
    string MatchOrigin,
    double Score,
    string Title,
    string? Feature,
    string? Screen,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Assertions,
    IReadOnlyList<string> Behaviors);

public sealed record LlmSourcePayload(
    string Id,
    string Type,
    string Title,
    string? Feature,
    string? Screen,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Assertions,
    IReadOnlyList<string> Behaviors);

public sealed record LlmComparePayload(
    LlmSourcePayload Source,
    IReadOnlyList<LlmCandidatePayload> Candidates);

public sealed record CoverageDecision(
    string SourceId,
    string? BestMatchId,
    string Coverage, // full | partial | none
    double Confidence,
    IReadOnlyList<int> CoveredSourceSteps,
    IReadOnlyList<int> MissingSourceSteps,
    IReadOnlyList<string> Notes);

public sealed record CoveragePipelineResult(
    NormalizedEntity Source,
    IReadOnlyList<CandidateEntity> Candidates,
    IReadOnlyList<RuleMatch> RuleMatches,
    IReadOnlyList<VectorMatch> VectorMatches,
    CoverageDecision? Decision);
