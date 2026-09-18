namespace IncidentCompass.IntegrationTests;

/// <summary>
/// One language of an attack leg. Every off-topic and hard-negative query is replaced by the full text of
/// the candidate chunk the judge scored highest for it, and run through the real <c>memory_search</c>
/// with the query's real trigger signal. <c>ConfirmedItemCount</c> is the error measure: returned items
/// banded <c>medium</c> or better. <c>SkippedQueryCount</c> counts queries that were offered no
/// candidate at all, so there was no chunk to quote.
/// </summary>
public sealed record MemorySearchRelevanceJudgeBenchmarkAttackLanguage(
    string Language,
    int AttackQueryCount,
    int SkippedQueryCount,
    int ReturnedItemCount,
    int ConfirmedItemCount,
    int ConfirmedQueryCount,
    IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkAttackQuery> Queries);
