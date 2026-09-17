using IncidentCompass.Application.Memory;

namespace IncidentCompass.IntegrationTests;

public static class MemoryRetrievalMetrics
{
    public static MemoryRetrievalEvaluation Evaluate(
        MemoryRetrievalBenchmarkCorpus corpus,
        IReadOnlyList<MemoryRetrievalQueryResult> results)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(results);

        var byQuery = results.ToDictionary(static result => result.QueryId, StringComparer.Ordinal);
        if (byQuery.Count != results.Count ||
            !corpus.Queries.Select(static query => query.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(byQuery.Keys))
        {
            throw new ArgumentException("Results must contain exactly one entry for every corpus query.", nameof(results));
        }

        var outcomes = corpus.Queries.Select(query => CreateOutcome(query, byQuery[query.Id], corpus.TopK)).ToArray();
        var positive = outcomes.Where(static outcome => outcome.RelevantChunkCount > 0).ToArray();
        var predictedNoMatchCount = outcomes.Count(static outcome => outcome.ReturnedChunkIds.Count == 0);
        var truePredictedNoMatchCount = outcomes.Count(static outcome =>
            outcome.RelevantChunkCount == 0 && outcome.ReturnedChunkIds.Count == 0);
        var offTopic = CategoryQueries(corpus, byQuery, MemoryRetrievalQueryCategory.OffTopic);
        var hardNegative = CategoryQueries(corpus, byQuery, MemoryRetrievalQueryCategory.HardNegative);

        return new MemoryRetrievalEvaluation(
            outcomes,
            new MemoryRetrievalMetricSummary(
                ChunkMacroRecallAt5: positive.Average(static outcome => outcome.ChunkRecallAt5),
                ChunkMicroRecallAt5: Ratio(
                    positive.Sum(static outcome => outcome.RelevantChunkHits),
                    positive.Sum(static outcome => outcome.RelevantChunkCount)),
                ItemMacroRecallAt5: positive.Average(static outcome => outcome.ItemRecallAt5),
                ItemMicroRecallAt5: Ratio(
                    positive.Sum(static outcome => outcome.RelevantItemHits),
                    positive.Sum(static outcome => outcome.RelevantItemCount)),
                MeanFirstRelevantChunkRank: positive.Average(static outcome => outcome.FirstRelevantChunkRank),
                NoMatchPrecision: predictedNoMatchCount == 0
                    ? 0
                    : (double)truePredictedNoMatchCount / predictedNoMatchCount,
                NoMatchFalsePositiveCount: outcomes.Count(static outcome =>
                    outcome.RelevantChunkCount == 0 && outcome.ReturnedChunkIds.Count > 0),
                PositiveQueryCount: positive.Length,
                TrueNoMatchQueryCount: outcomes.Count(static outcome => outcome.RelevantChunkCount == 0),
                PredictedNoMatchCount: predictedNoMatchCount,
                OffTopicQueryCount: offTopic.Length,
                OffTopicFalsePositiveCount: offTopic.Count(static result => result.Matches.Count > 0),
                HardNegativeQueryCount: hardNegative.Length,
                HardNegativeReturnedCount: hardNegative.Count(static result => result.Matches.Count > 0),
                HardNegativeConfirmedCount: hardNegative.Count(result =>
                    result.Matches.Take(corpus.TopK).Any(static match => IsConfirmed(match)))));
    }

    /// <summary>
    /// The results of the queries a corpus explicitly puts in <paramref name="category" />, each trimmed
    /// to the first <c>TopK</c> matches. A corpus that predates the category field names none, so every
    /// per-category count on such a corpus is zero and its recorded numbers stay comparable.
    /// </summary>
    private static MemoryRetrievalQueryResult[] CategoryQueries(
        MemoryRetrievalBenchmarkCorpus corpus,
        Dictionary<string, MemoryRetrievalQueryResult> byQuery,
        string category) =>
        corpus.Queries
            .Where(query => string.Equals(query.Category, category, StringComparison.Ordinal))
            .Select(query => byQuery[query.Id] with
            {
                Matches = byQuery[query.Id].Matches.Take(corpus.TopK).ToArray()
            })
            .ToArray();

    /// <summary>
    /// A returned match confirms a hard negative when the tool banded it <c>medium</c> or <c>high</c>:
    /// that is the tool claiming lexical support for a query nothing active answers. A <c>low</c> band
    /// is the vector-only fallback saying so itself, and a null band comes from a strategy that does not
    /// go through the tool, so neither can confirm.
    /// </summary>
    private static bool IsConfirmed(MemoryRetrievalMatch match) =>
        match.RetrievalConfidence is MemoryRetrievalConfidence.Medium or MemoryRetrievalConfidence.High;

    private static MemoryRetrievalQueryOutcome CreateOutcome(
        MemoryRetrievalBenchmarkQuery query,
        MemoryRetrievalQueryResult result,
        int topK)
    {
        var matches = result.Matches.Take(topK).ToArray();
        var returnedChunkIds = matches.Select(static match => match.ChunkId).ToArray();
        var returnedItemIds = matches.Select(static match => match.ItemId).Distinct().ToArray();
        var relevantChunks = query.RelevantChunkIds.ToHashSet();
        var relevantItems = query.RelevantItemIds.ToHashSet();
        var chunkHits = returnedChunkIds.Distinct().Count(relevantChunks.Contains);
        var itemHits = returnedItemIds.Count(relevantItems.Contains);
        var firstRankIndex = Array.FindIndex(returnedChunkIds, relevantChunks.Contains);

        return new MemoryRetrievalQueryOutcome(
            query.Id,
            returnedChunkIds,
            returnedItemIds,
            relevantChunks.Count,
            chunkHits,
            Ratio(chunkHits, relevantChunks.Count),
            relevantItems.Count,
            itemHits,
            Ratio(itemHits, relevantItems.Count),
            firstRankIndex < 0 ? topK + 1 : firstRankIndex + 1);
    }

    private static double Ratio(int numerator, int denominator) =>
        denominator == 0 ? 0 : (double)numerator / denominator;
}

public sealed record MemoryRetrievalEvaluation(
    IReadOnlyList<MemoryRetrievalQueryOutcome> Queries,
    MemoryRetrievalMetricSummary Metrics);

/// <summary>
/// The retrieval numbers for one run. The first ten fields are the version 1 metrics and are computed
/// exactly as they were when the versioned baseline was recorded: the no-match fields still count every
/// zero-relevance query, whatever category it belongs to. The per-category fields that follow are read
/// from the corpus's explicit categories only, so they are zero on a corpus that predates the field.
/// <para>
/// <c>OffTopicFalsePositiveCount</c> counts off-topic queries that returned at least one chunk.
/// <c>HardNegativeReturnedCount</c> counts hard negatives that returned at least one chunk and is a
/// diagnostic, not an error count: the active siblings of a retired procedure are legitimate
/// low-confidence context. <c>HardNegativeConfirmedCount</c> is the error measure, counting hard
/// negatives where at least one returned match was banded <c>medium</c> or <c>high</c>, which is the
/// tool asserting support for an answer the corpus no longer holds.
/// </para>
/// </summary>
public sealed record MemoryRetrievalMetricSummary(
    double ChunkMacroRecallAt5,
    double ChunkMicroRecallAt5,
    double ItemMacroRecallAt5,
    double ItemMicroRecallAt5,
    double MeanFirstRelevantChunkRank,
    double NoMatchPrecision,
    int NoMatchFalsePositiveCount,
    int PositiveQueryCount,
    int TrueNoMatchQueryCount,
    int PredictedNoMatchCount,
    int OffTopicQueryCount = 0,
    int OffTopicFalsePositiveCount = 0,
    int HardNegativeQueryCount = 0,
    int HardNegativeReturnedCount = 0,
    int HardNegativeConfirmedCount = 0);

public sealed record MemoryRetrievalQueryOutcome(
    string QueryId,
    IReadOnlyList<Guid> ReturnedChunkIds,
    IReadOnlyList<Guid> ReturnedItemIds,
    int RelevantChunkCount,
    int RelevantChunkHits,
    double ChunkRecallAt5,
    int RelevantItemCount,
    int RelevantItemHits,
    double ItemRecallAt5,
    int FirstRelevantChunkRank);

/// <summary>
/// One returned match. <paramref name="RetrievalConfidence" /> is the band the production tool reported
/// for it, and is null for a strategy that does not go through the tool. It is deliberately outside
/// <see cref="MemoryRetrievalQueryOutcome" />, which the versioned baseline is compared on.
/// </summary>
public sealed record MemoryRetrievalMatch(Guid ItemId, Guid ChunkId, string? RetrievalConfidence = null);

public sealed record MemoryRetrievalQueryResult(
    string QueryId,
    IReadOnlyList<MemoryRetrievalMatch> Matches);
