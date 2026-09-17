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
                PredictedNoMatchCount: predictedNoMatchCount));
    }

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
    int PredictedNoMatchCount);

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
