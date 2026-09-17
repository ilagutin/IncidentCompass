using System.Diagnostics;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The four measurements the local embedding model benchmark takes on a seeded corpus: the production
/// <c>memory_search</c> pipeline over a range of <c>MinScore</c> values, the same pipeline over each
/// <c>VectorOnlyFallback</c> mode at the shipped floor, the raw vector ranking, and query embedding
/// latency.
/// </summary>
internal static class LocalEmbeddingModelBenchmarkMeasurements
{
    public const int RawRankingDepth = 10;

    public const double RawMinScore = -1;

    public const int LatencyWarmupPasses = 5;

    public const int LatencyMeasuredPasses = 30;

    /// <summary>The shipped floor, at which every fallback mode is measured.</summary>
    public const double ShippedMinScore = 0.25;

    public static readonly IReadOnlyList<double> PipelineMinScores =
        [0.25, 0.50, 0.55, 0.60, 0.65, 0.70, 0.75, 0.80, 0.85, 0.90];

    /// <summary>
    /// The sweep keeps <c>off</c> so its numbers stay comparable with records taken before the setting
    /// existed; the modes are measured separately at <see cref="ShippedMinScore" />.
    /// </summary>
    public static readonly IReadOnlyList<string> PipelineFallbackModes =
        [MemorySearchVectorOnlyFallbackSetting.Off,
         MemorySearchVectorOnlyFallbackSetting.ForeignScript,
         MemorySearchVectorOnlyFallbackSetting.Always];

    /// <summary>
    /// The host configuration with <c>memory-embed</c> routed to a <c>LocalOnnx</c> provider naming the
    /// installed model id, and <c>memory_search</c> at <paramref name="minScore" /> under
    /// <paramref name="vectorOnlyFallback" />.
    /// </summary>
    public static TriageConfiguration ConfigurationFor(
        TriageConfiguration hostConfiguration,
        LocalEmbeddingModelBenchmarkModel model,
        double minScore,
        string vectorOnlyFallback = MemorySearchVectorOnlyFallbackSetting.Off)
    {
        var providers = new Dictionary<string, TriageProviderSettings>(hostConfiguration.Providers, StringComparer.Ordinal)
        {
            [LocalEmbeddingModelBenchmarkModel.RouteProviderId] = new("LocalOnnx", null, null)
        };
        var routes = new Dictionary<string, TriageRouteSettings>(hostConfiguration.Routes, StringComparer.Ordinal)
        {
            ["memory-embed"] = new(
                "Embedding",
                LocalEmbeddingModelBenchmarkModel.RouteProviderId,
                model.Installed.Manifest.Id,
                null,
                null,
                null)
        };
        var tools = new Dictionary<string, TriageToolSettings>(hostConfiguration.Tools, StringComparer.Ordinal);
        tools["memory_search"] = tools["memory_search"] with
        {
            MinScore = minScore,
            VectorOnlyFallback = vectorOnlyFallback
        };
        return hostConfiguration with { Providers = providers, Routes = routes, Tools = tools };
    }

    public static async Task<IReadOnlyList<LocalEmbeddingModelBenchmarkPipelineResult>> MeasurePipelineAsync(
        TriageConfiguration hostConfiguration,
        LocalEmbeddingModelBenchmarkModel model,
        IMemoryRepository repository,
        MemoryRetrievalBenchmarkCorpus corpus,
        Action<TriageConfiguration> setCurrentConfiguration,
        CancellationToken cancellationToken)
    {
        var results = new List<LocalEmbeddingModelBenchmarkPipelineResult>(PipelineMinScores.Count);
        foreach (var minScore in PipelineMinScores)
        {
            var run = await RunPipelineAsync(
                hostConfiguration, model, repository, corpus, minScore,
                MemorySearchVectorOnlyFallbackSetting.Off, setCurrentConfiguration, cancellationToken);
            results.Add(new LocalEmbeddingModelBenchmarkPipelineResult(
                minScore, run.Evaluation.Metrics, run.Evaluation.Queries));
        }

        return results;
    }

    /// <summary>
    /// The same production pipeline at the shipped floor under each fallback mode, with the count of
    /// returned items per retrieval-confidence band beside the ordinary retrieval metrics.
    /// </summary>
    public static async Task<IReadOnlyList<LocalEmbeddingModelBenchmarkFallbackResult>> MeasureFallbackModesAsync(
        TriageConfiguration hostConfiguration,
        LocalEmbeddingModelBenchmarkModel model,
        IMemoryRepository repository,
        MemoryRetrievalBenchmarkCorpus corpus,
        Action<TriageConfiguration> setCurrentConfiguration,
        CancellationToken cancellationToken)
    {
        var results = new List<LocalEmbeddingModelBenchmarkFallbackResult>(PipelineFallbackModes.Count);
        foreach (var mode in PipelineFallbackModes)
        {
            var run = await RunPipelineAsync(
                hostConfiguration, model, repository, corpus, ShippedMinScore,
                mode, setCurrentConfiguration, cancellationToken);
            results.Add(new LocalEmbeddingModelBenchmarkFallbackResult(
                mode,
                ShippedMinScore,
                run.Evaluation.Metrics,
                CountConfidenceBands(run.Results),
                run.Evaluation.Queries));
        }

        return results;
    }

    private static async Task<(IReadOnlyList<MemoryRetrievalQueryResult> Results, MemoryRetrievalEvaluation Evaluation)> RunPipelineAsync(
        TriageConfiguration hostConfiguration,
        LocalEmbeddingModelBenchmarkModel model,
        IMemoryRepository repository,
        MemoryRetrievalBenchmarkCorpus corpus,
        double minScore,
        string vectorOnlyFallback,
        Action<TriageConfiguration> setCurrentConfiguration,
        CancellationToken cancellationToken)
    {
        var configuration = ConfigurationFor(hostConfiguration, model, minScore, vectorOnlyFallback);
        setCurrentConfiguration(configuration);
        var results = await new ProductionMemoryRetrievalStrategy(
            new MemorySearchTool(model.Client, repository), configuration)
            .ExecuteAsync(corpus, cancellationToken);
        return (results, MemoryRetrievalMetrics.Evaluate(corpus, results));
    }

    private static Dictionary<string, int> CountConfidenceBands(
        IReadOnlyList<MemoryRetrievalQueryResult> results)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [MemoryRetrievalConfidence.High] = 0,
            [MemoryRetrievalConfidence.Medium] = 0,
            [MemoryRetrievalConfidence.Low] = 0
        };
        foreach (var band in results.SelectMany(static result => result.Matches)
                     .Select(static match => match.RetrievalConfidence)
                     .OfType<string>())
        {
            counts[band] = counts.TryGetValue(band, out var count) ? count + 1 : 1;
        }

        return counts;
    }

    public static async Task<(LocalEmbeddingModelBenchmarkRawSummary Summary, IReadOnlyList<LocalEmbeddingModelBenchmarkRawQuery> Queries)> MeasureRawAsync(
        LocalEmbeddingModelBenchmarkModel model,
        IMemoryRepository repository,
        MemoryRetrievalBenchmarkCorpus corpus,
        CancellationToken cancellationToken)
    {
        var queries = new List<LocalEmbeddingModelBenchmarkRawQuery>(corpus.Queries.Count);
        foreach (var query in corpus.Queries)
        {
            var embedding = await model.Client.CreateEmbeddingAsync(
                new EmbeddingRequest(query.Text, model.Installed.Manifest.Id, "memory-benchmark-raw", EmbeddingInputKind.Query),
                cancellationToken);
            var ranked = await repository.SearchAsync(
                new MemorySearchRequest(
                    corpus.TenantId,
                    embedding.Provider,
                    embedding.Model,
                    embedding.Vector.Count,
                    embedding.Vector,
                    100,
                    RawMinScore),
                cancellationToken);
            queries.Add(ScoreRawQuery(query, ranked));
        }

        var positive = queries.Where(static query => !query.IsNoMatch).ToArray();
        var summary = new LocalEmbeddingModelBenchmarkRawSummary(
            positive.Average(static query => query.NdcgAt10!.Value),
            positive.Average(static query => query.ChunkRecallAt5!.Value),
            positive.Average(static query => query.ChunkRecallAt10!.Value),
            positive.Average(static query => query.ItemRecallAt5!.Value),
            positive.Average(static query => query.ItemRecallAt10!.Value),
            positive.Select(static query => query.BestRelevantScore ?? double.NaN).ToArray(),
            queries.Where(static query => query.IsNoMatch).Select(static query => query.BestNonRelevantScore).ToArray());
        return (summary, queries);
    }

    public static async Task<LocalEmbeddingModelBenchmarkLatency> MeasureLatencyAsync(
        LocalEmbeddingModelBenchmarkModel model,
        IReadOnlyList<(string Language, string Text)> queryTexts,
        CancellationToken cancellationToken)
    {
        for (var pass = 0; pass < LatencyWarmupPasses; pass++)
        {
            foreach (var (_, text) in queryTexts)
            {
                await EmbedQueryAsync(model, text, cancellationToken);
            }
        }

        var samples = new List<(string Language, double Milliseconds)>(LatencyMeasuredPasses * queryTexts.Count);
        for (var pass = 0; pass < LatencyMeasuredPasses; pass++)
        {
            foreach (var (language, text) in queryTexts)
            {
                var started = Stopwatch.GetTimestamp();
                await EmbedQueryAsync(model, text, cancellationToken);
                samples.Add((language, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            }
        }

        return new LocalEmbeddingModelBenchmarkLatency(
            Summarize(samples.Select(static sample => sample.Milliseconds)),
            samples.GroupBy(static sample => sample.Language, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => Summarize(group.Select(static sample => sample.Milliseconds)),
                    StringComparer.Ordinal));
    }

    private static Task<EmbeddingResponse> EmbedQueryAsync(
        LocalEmbeddingModelBenchmarkModel model,
        string text,
        CancellationToken cancellationToken) =>
        model.Client.CreateEmbeddingAsync(
            new EmbeddingRequest(text, model.Installed.Manifest.Id, "memory-benchmark-latency", EmbeddingInputKind.Query),
            cancellationToken);

    private static LocalEmbeddingModelBenchmarkRawQuery ScoreRawQuery(
        MemoryRetrievalBenchmarkQuery query,
        IReadOnlyList<MemorySearchMatch> ranked)
    {
        var relevantChunks = query.RelevantChunkIds.ToHashSet();
        var relevantItems = query.RelevantItemIds.ToHashSet();
        var top = ranked.Take(RawRankingDepth)
            .Select(match => new LocalEmbeddingModelBenchmarkRankedChunk(
                match.ChunkId,
                match.MemoryItemId,
                match.Score,
                relevantChunks.Contains(match.ChunkId)))
            .ToArray();
        var bestNonRelevant = ranked.Where(match => !relevantChunks.Contains(match.ChunkId))
            .Select(static match => match.Score)
            .DefaultIfEmpty(double.NaN)
            .Max();
        if (relevantChunks.Count == 0)
        {
            return new LocalEmbeddingModelBenchmarkRawQuery(
                query.Id, true, top, null, null, null, null, null, null, null, bestNonRelevant);
        }

        var bestRelevantIndex = ranked.ToList().FindIndex(match => relevantChunks.Contains(match.ChunkId));
        return new LocalEmbeddingModelBenchmarkRawQuery(
            query.Id,
            false,
            top,
            Ndcg(top, relevantChunks.Count),
            ChunkRecall(ranked, relevantChunks, 5),
            ChunkRecall(ranked, relevantChunks, 10),
            ItemRecall(ranked, relevantItems, 5),
            ItemRecall(ranked, relevantItems, 10),
            bestRelevantIndex < 0 ? null : ranked[bestRelevantIndex].Score,
            bestRelevantIndex < 0 ? null : bestRelevantIndex + 1,
            bestNonRelevant);
    }

    /// <summary>nDCG@10 with binary chunk relevance: gain 1 for a relevant chunk at rank r, discounted by log2(r + 1).</summary>
    private static double Ndcg(IReadOnlyList<LocalEmbeddingModelBenchmarkRankedChunk> top, int relevantCount)
    {
        var dcg = top.Select(static (chunk, index) => chunk.Relevant ? 1 / Math.Log2(index + 2) : 0).Sum();
        var ideal = Enumerable.Range(0, Math.Min(relevantCount, RawRankingDepth)).Sum(static index => 1 / Math.Log2(index + 2));
        return dcg / ideal;
    }

    private static double ChunkRecall(IReadOnlyList<MemorySearchMatch> ranked, HashSet<Guid> relevant, int depth) =>
        (double)ranked.Take(depth).Select(static match => match.ChunkId).Distinct().Count(relevant.Contains) / relevant.Count;

    private static double ItemRecall(IReadOnlyList<MemorySearchMatch> ranked, HashSet<Guid> relevant, int depth) =>
        (double)ranked.Take(depth).Select(static match => match.MemoryItemId).Distinct().Count(relevant.Contains) / relevant.Count;

    private static LocalEmbeddingModelBenchmarkLatencySummary Summarize(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
        var p95 = sorted[(int)Math.Ceiling(0.95 * sorted.Length) - 1];
        return new LocalEmbeddingModelBenchmarkLatencySummary(sorted.Length, median, p95, sorted[0], sorted[^1]);
    }
}
