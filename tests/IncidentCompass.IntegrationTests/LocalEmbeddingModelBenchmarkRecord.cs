using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The identity of the result document. Schema 3 is schema 2 plus the per-category composition of each
/// group and the per-category counts on <see cref="MemoryRetrievalMetricSummary" />: the metric summary
/// means more than it did, so the document says so. Records written at schema 1 and 2 stay parseable and
/// nothing migrates them.
/// </summary>
internal static class LocalEmbeddingModelBenchmarkContract
{
    public const int SchemaVersion = 3;

    public const string Benchmark = "local-embedding-model-benchmark-v3";
}

internal sealed record LocalEmbeddingModelBenchmarkRecord(
    int SchemaVersion,
    string Benchmark,
    string CorpusVersion,
    string MultilingualQueriesVersion,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    LocalEmbeddingModelBenchmarkMachine Machine,
    LocalEmbeddingModelBenchmarkSettings Settings,
    IReadOnlyList<LocalEmbeddingModelBenchmarkModelResult> Models);

internal sealed record LocalEmbeddingModelBenchmarkMachine(
    int ProcessorCount,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string Runtime,
    bool Avx2,
    bool AvxVnni,
    bool Avx512F);

internal sealed record LocalEmbeddingModelBenchmarkSettings(
    int TopK,
    IReadOnlyList<double> PipelineMinScores,
    IReadOnlyList<string> PipelineFallbackModes,
    double PipelineFallbackMinScore,
    double RawMinScore,
    int RawRankingDepth,
    int LatencyWarmupPasses,
    int LatencyMeasuredPasses,
    int IntraOpThreads,
    string PipelineDescription,
    string FallbackDescription,
    string RawDescription,
    string LatencyDescription);

internal sealed record LocalEmbeddingModelBenchmarkModelResult(
    string ShortName,
    string EncodedIdentity,
    LocalOnnxModelManifest Manifest,
    double InstallSeconds,
    DateTimeOffset MeasuredAtUtc,
    IReadOnlyList<LocalEmbeddingModelBenchmarkGroupResult> Groups,
    LocalEmbeddingModelBenchmarkLatency Latency);

internal sealed record LocalEmbeddingModelBenchmarkGroupResult(
    string Group,
    int QueryCount,
    int PositiveQueryCount,
    int NoMatchQueryCount,
    IReadOnlyList<LocalEmbeddingModelBenchmarkPipelineResult> Pipeline,
    IReadOnlyList<LocalEmbeddingModelBenchmarkFallbackResult> FallbackModes,
    LocalEmbeddingModelBenchmarkRawSummary Raw,
    IReadOnlyList<LocalEmbeddingModelBenchmarkRawQuery> RawQueries,
    LocalEmbeddingModelBenchmarkCategoryCounts Categories);

/// <summary>
/// How the group's queries divide over <see cref="MemoryRetrievalQueryCategory" />, so a reader can see
/// what a no-match number was measured over without opening the corpus fixture.
/// </summary>
internal sealed record LocalEmbeddingModelBenchmarkCategoryCounts(int Positive, int OffTopic, int HardNegative);

internal sealed record LocalEmbeddingModelBenchmarkPipelineResult(
    double MinScore,
    MemoryRetrievalMetricSummary Metrics,
    IReadOnlyList<MemoryRetrievalQueryOutcome> Queries);

internal sealed record LocalEmbeddingModelBenchmarkFallbackResult(
    string VectorOnlyFallback,
    double MinScore,
    MemoryRetrievalMetricSummary Metrics,
    IReadOnlyDictionary<string, int> ReturnedItemsByConfidenceBand,
    IReadOnlyList<MemoryRetrievalQueryOutcome> Queries);

internal sealed record LocalEmbeddingModelBenchmarkRawSummary(
    double NdcgAt10,
    double ChunkRecallAt5,
    double ChunkRecallAt10,
    double ItemRecallAt5,
    double ItemRecallAt10,
    IReadOnlyList<double> BestRelevantScores,
    IReadOnlyList<double> BestNoMatchScores);

internal sealed record LocalEmbeddingModelBenchmarkRawQuery(
    string QueryId,
    bool IsNoMatch,
    IReadOnlyList<LocalEmbeddingModelBenchmarkRankedChunk> Top10,
    double? NdcgAt10,
    double? ChunkRecallAt5,
    double? ChunkRecallAt10,
    double? ItemRecallAt5,
    double? ItemRecallAt10,
    double? BestRelevantScore,
    int? BestRelevantRank,
    double BestNonRelevantScore);

internal sealed record LocalEmbeddingModelBenchmarkRankedChunk(Guid ChunkId, Guid ItemId, double Score, bool Relevant);

internal sealed record LocalEmbeddingModelBenchmarkLatency(
    LocalEmbeddingModelBenchmarkLatencySummary All,
    IReadOnlyDictionary<string, LocalEmbeddingModelBenchmarkLatencySummary> ByLanguage);

internal sealed record LocalEmbeddingModelBenchmarkLatencySummary(
    int Samples,
    double MedianMilliseconds,
    double P95Milliseconds,
    double MinMilliseconds,
    double MaxMilliseconds);
