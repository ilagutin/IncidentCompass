using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.IntegrationTests;

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
    double RawMinScore,
    int RawRankingDepth,
    int LatencyWarmupPasses,
    int LatencyMeasuredPasses,
    int IntraOpThreads,
    string PipelineDescription,
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
    LocalEmbeddingModelBenchmarkRawSummary Raw,
    IReadOnlyList<LocalEmbeddingModelBenchmarkRawQuery> RawQueries);

internal sealed record LocalEmbeddingModelBenchmarkPipelineResult(
    double MinScore,
    MemoryRetrievalMetricSummary Metrics,
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
