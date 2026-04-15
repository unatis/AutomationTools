namespace AutomationTools.Coverage;

public sealed record CoverageEmbeddingSource(
    string Id,
    string Text);

public sealed record CoverageEmbeddingItem(
    string Id,
    string Text,
    float[] Vector);

public sealed record CoverageEmbeddingFile(
    string Model,
    IReadOnlyList<CoverageEmbeddingItem> Items);

public sealed record CoverageEmbeddingMatch(
    int WorkItemId,
    string AdoTitle,
    string AdoSuite,
    string? AllureTitle,
    string? AllureFullName,
    string? AllureUuid,
    double Score,
    double TitleScore,
    double CategoryScore,
    double StepsScore,
    string MatchedBy);

public sealed record CoverageEmbeddingUnmatchedAdo(
    int WorkItemId,
    string AdoTitle,
    string AdoSuite);

public sealed record CoverageEmbeddingUnmatchedAllure(
    string Title,
    string? FullName,
    string? Uuid);

public sealed record CoverageEmbeddingCandidate(
    int WorkItemId,
    string AdoTitle,
    string AdoSuite,
    string? AllureTitle,
    string? AllureFullName,
    string? AllureUuid,
    double Score,
    double TitleScore,
    double CategoryScore,
    double StepsScore,
    int Rank);

public sealed record CoverageEmbeddingReport(
    int PlanId,
    int? SuiteId,
    string TeamId,
    int TotalAdoTests,
    int TotalAllureTests,
    int MatchedTests,
    double CoveragePercent,
    string Model,
    double MinScore,
    int TopK,
    IReadOnlyList<CoverageEmbeddingMatch> Matches,
    IReadOnlyList<CoverageEmbeddingUnmatchedAdo> UnmatchedAdo,
    IReadOnlyList<CoverageEmbeddingUnmatchedAllure> UnmatchedAllure,
    IReadOnlyList<CoverageEmbeddingCandidate> Candidates);

public sealed record CoverageLlmMatchReview(
    int WorkItemId,
    string AdoTitle,
    string? AllureTitle,
    string Decision,
    double Confidence,
    string Reason);

public sealed record CoverageLlmGapExplanation(
    string Kind,
    int? WorkItemId,
    string? AdoTitle,
    string? AllureTitle,
    string? AllureUuid,
    string Reason);

public sealed record CoverageLlmMissingTest(
    string Kind,
    int? WorkItemId,
    string Title,
    IReadOnlyList<string> Steps);

public sealed record CoverageEmbeddingLlmReport(
    int PlanId,
    int? SuiteId,
    string TeamId,
    string Model,
    CoverageLlmTokenUsage TokenUsage,
    IReadOnlyList<CoverageLlmMatchReview> MatchReviews,
    IReadOnlyList<CoverageLlmGapExplanation> GapExplanations,
    IReadOnlyList<CoverageLlmMissingTest> MissingTests);

public sealed record CoverageEmbeddingLlmCoverageReport(
    int PlanId,
    int? SuiteId,
    string TeamId,
    string Model,
    IReadOnlyList<CoverageEmbeddingMatch> Matches,
    IReadOnlyList<CoverageEmbeddingUnmatchedAdo> UnmatchedAdo,
    IReadOnlyList<CoverageEmbeddingUnmatchedAllure> UnmatchedAllure);

public sealed record CoverageDuplicateItem(
    string Id,
    string Title,
    string DuplicateOfId,
    string Kind,
    double Score);

public sealed record CoverageDuplicateReport(
    IReadOnlyList<CoverageDuplicateItem> AdoDuplicates,
    IReadOnlyList<CoverageDuplicateItem> AllureDuplicates);

public sealed record CoverageLlmTokenUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);
