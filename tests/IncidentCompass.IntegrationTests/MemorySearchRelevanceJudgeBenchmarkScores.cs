using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Memory;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Scores every (query, candidate) pair of the benchmark once against the model's query and once against
/// the fault query, and derives the boundaries the floor and the confirm score have to fall between.
/// </summary>
/// <remarks>
/// The capture pass makes the same calls <c>MemorySearchTool</c> makes, with the same candidate count
/// and floor, for one reason the tool cannot serve: it needs the chunk identity beside each score, and
/// the tool's own output is already cut to <c>TopK</c> and carries no score for a candidate it dropped.
/// The strongest off-topic score is a property of the whole candidate set, not of the five items that
/// survived it. The fault query is built by the production <see cref="MemoryFaultQuery" /> from the
/// production trigger signal, so the tool's own confirmation call asks the judge the identical string
/// and is answered from the cache. The pass is not what decides anything; it fills the judge's cache and
/// records the boundaries, and every number the acceptance bar and the sweep read still comes from the
/// real tool.
/// </remarks>
internal static class MemorySearchRelevanceJudgeBenchmarkScores
{
    public const string PooledLanguage = "all";

    public static async Task<IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkPair>> CaptureAsync(
        string language,
        MemoryRetrievalBenchmarkCorpus corpus,
        IEmbeddingClient embeddingClient,
        IMemoryRepository repository,
        IMemoryRelevanceJudge judge,
        string routeModel,
        string? routeProviderId,
        int candidateCount,
        double minScore,
        CancellationToken cancellationToken)
    {
        var pairs = new List<MemorySearchRelevanceJudgeBenchmarkPair>();
        foreach (var query in corpus.Queries)
        {
            var relevantChunks = query.RelevantChunkIds.ToHashSet();
            var embedding = await embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest(
                    query.Text, routeModel, "relevance-judge-benchmark", EmbeddingInputKind.Query, routeProviderId),
                cancellationToken);
            var candidates = await repository.SearchAsync(
                new MemorySearchRequest(
                    corpus.TenantId,
                    embedding.Provider,
                    embedding.Model,
                    embedding.Vector.Count,
                    embedding.Vector,
                    candidateCount,
                    minScore),
                cancellationToken);
            var texts = candidates.Select(static candidate => candidate.Text).ToArray();
            var scores = await judge.ScoreAsync(query.Text, texts, cancellationToken);
            var faultQuery = MemoryFaultQuery.For(MemoryRetrievalBenchmarkTriggerSignal.For(corpus.TenantId, query));
            IReadOnlyList<float>? faultScores = faultQuery is null
                ? null
                : await judge.ScoreAsync(faultQuery, texts, cancellationToken);
            var category = MemoryRetrievalQueryCategory.Of(query);
            var offered = candidates.Select(static candidate => candidate.ChunkId).ToHashSet();
            for (var index = 0; index < candidates.Count; index++)
            {
                pairs.Add(new MemorySearchRelevanceJudgeBenchmarkPair(
                    language,
                    query.Id,
                    category,
                    candidates[index].ChunkId,
                    scores[index],
                    relevantChunks.Contains(candidates[index].ChunkId),
                    faultScores?[index]));
            }

            pairs.AddRange(relevantChunks
                .Where(chunkId => !offered.Contains(chunkId))
                .Select(chunkId => new MemorySearchRelevanceJudgeBenchmarkPair(
                    language, query.Id, category, chunkId, Score: null, IsRelevant: true)));
        }

        return pairs;
    }

    public static MemorySearchRelevanceJudgeBenchmarkBoundary Summarize(
        string language,
        IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkPair> pairs)
    {
        var offTopic = pairs
            .Where(static pair => pair.Category == MemoryRetrievalQueryCategory.OffTopic && pair.Score is not null)
            .ToArray();
        var relevant = pairs
            .Where(static pair => pair.IsRelevant && pair.Score is not null)
            .ToArray();
        var strongestOffTopic = offTopic
            .OrderByDescending(static pair => pair.Score!.Value)
            .FirstOrDefault();
        var weakestRelevant = relevant
            .OrderBy(static pair => pair.Score!.Value)
            .FirstOrDefault();
        var width = strongestOffTopic is not null && weakestRelevant is not null
            ? weakestRelevant.Score!.Value - strongestOffTopic.Score!.Value
            : (double?)null;

        return new MemorySearchRelevanceJudgeBenchmarkBoundary(
            language,
            strongestOffTopic?.Score,
            strongestOffTopic?.QueryId,
            strongestOffTopic?.ChunkId.ToString(),
            offTopic.Length,
            weakestRelevant?.Score,
            weakestRelevant?.QueryId,
            weakestRelevant?.ChunkId.ToString(),
            relevant.Length,
            pairs.Count(static pair => pair.IsRelevant && pair.Score is null),
            width,
            width > 0);
    }

    /// <summary>
    /// The two fault scores the confirm score has to fall between. A candidate of an off-topic or
    /// hard-negative query describes no fault the corpus answers, so a confirm score at or below the
    /// strongest of those fault scores can confirm one of them if it is returned; a labelled relevant
    /// chunk of a positive query describes its fault, so a confirm score above the weakest of those
    /// leaves that answer unconfirmed.
    /// </summary>
    public static MemorySearchRelevanceJudgeBenchmarkConfirmBoundary SummarizeConfirm(
        string language,
        IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkPair> pairs)
    {
        var negative = pairs
            .Where(static pair => pair.FaultScore is not null &&
                pair.Category is MemoryRetrievalQueryCategory.OffTopic or MemoryRetrievalQueryCategory.HardNegative)
            .ToArray();
        var relevant = pairs
            .Where(static pair => pair.IsRelevant && pair.FaultScore is not null)
            .ToArray();
        var strongestNegative = negative
            .OrderByDescending(static pair => pair.FaultScore!.Value)
            .FirstOrDefault();
        var weakestRelevant = relevant
            .OrderBy(static pair => pair.FaultScore!.Value)
            .FirstOrDefault();
        var width = strongestNegative is not null && weakestRelevant is not null
            ? weakestRelevant.FaultScore!.Value - strongestNegative.FaultScore!.Value
            : (double?)null;

        return new MemorySearchRelevanceJudgeBenchmarkConfirmBoundary(
            language,
            strongestNegative?.FaultScore,
            strongestNegative?.QueryId,
            strongestNegative?.ChunkId.ToString(),
            negative.Length,
            weakestRelevant?.FaultScore,
            weakestRelevant?.QueryId,
            weakestRelevant?.ChunkId.ToString(),
            relevant.Length,
            width,
            width > 0);
    }
}

/// <summary>
/// One scored (query, candidate chunk) pair. <c>Score</c> is the judge's score for the model's query and
/// <c>FaultScore</c> its score for the fault query built from the query's trigger signal. Both are null
/// for a labelled relevant chunk the vector search never offered the judge, which is recorded so a
/// recall shortfall no threshold can fix is visible as the retriever's and not the judge's.
/// </summary>
internal sealed record MemorySearchRelevanceJudgeBenchmarkPair(
    string Language,
    string QueryId,
    string Category,
    Guid ChunkId,
    double? Score,
    bool IsRelevant,
    double? FaultScore = null);

/// <summary>
/// Scores each (query, candidate) pair once and answers every later request for it from memory, so a
/// threshold sweep costs no model time. The benchmark is sequential, so this is deliberately not
/// thread-safe.
/// </summary>
internal sealed class RecordingMemoryRelevanceJudge(IMemoryRelevanceJudge inner) : IMemoryRelevanceJudge
{
    private readonly Dictionary<(string Query, string Candidate), float> scores = [];

    /// <summary>Calls forwarded to the real judge. Reset between the capture pass and the sweep.</summary>
    public int ModelCalls { get; private set; }

    public int ScoredPairs => scores.Count;

    public void ResetModelCalls() => ModelCalls = 0;

    public async Task<IReadOnlyList<float>> ScoreAsync(
        string query,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        var missing = candidates
            .Where(candidate => !scores.ContainsKey((query, candidate)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            ModelCalls++;
            var fresh = await inner.ScoreAsync(query, missing, cancellationToken);
            for (var index = 0; index < missing.Length; index++)
            {
                scores[(query, missing[index])] = fresh[index];
            }
        }

        return candidates.Select(candidate => scores[(query, candidate)]).ToArray();
    }
}

/// <summary>
/// Embeds each distinct query text once. A sweep re-runs the whole pipeline per threshold value and the
/// query text does not change, so without this the leg would re-embed the same few dozen strings a
/// couple of hundred times. The cached response keeps its original correlation id, which nothing in the
/// measured path reads.
/// </summary>
internal sealed class CachingBenchmarkEmbeddingClient(IEmbeddingClient inner) : IEmbeddingClient
{
    private readonly Dictionary<(string Input, EmbeddingInputKind Kind), EmbeddingResponse> cache = [];

    public async Task<EmbeddingResponse> CreateEmbeddingAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        var key = (request.Input, request.Kind);
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var response = await inner.CreateEmbeddingAsync(request, cancellationToken);
        cache[key] = response;
        return response;
    }
}
