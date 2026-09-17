using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Text.Json.Serialization;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Compares the shipped local embedding model with <c>intfloat/multilingual-e5-base</c> on the memory
/// retrieval corpus, in English and in Polish and Russian renderings of its queries. Opt-in only: it
/// returns at once unless <c>INCIDENTCOMPASS_EMBEDDING_BENCHMARK</c> is truthy, because it downloads
/// both models and measures latency, which no gate should depend on. Models are installed through the
/// product's model store under <c>INCIDENTCOMPASS_EMBEDDING_MODEL_CACHE</c>, one directory per model.
/// The result is one JSON document, written to the test output and to
/// <c>INCIDENTCOMPASS_EMBEDDING_BENCHMARK_OUTPUT</c> when it is set.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class LocalEmbeddingModelBenchmarkTests(PostgresRepositoryFixture postgres)
{
    private const string EnabledVariable = "INCIDENTCOMPASS_EMBEDDING_BENCHMARK";
    private const string CacheDirectoryVariable = "INCIDENTCOMPASS_EMBEDDING_MODEL_CACHE";
    private const string OutputVariable = "INCIDENTCOMPASS_EMBEDDING_BENCHMARK_OUTPUT";

    [DockerAvailableFact]
    public async Task CompareLocalEmbeddingModels_WhenExplicitlyEnabled()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnabledVariable)))
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var startedAt = DateTimeOffset.UtcNow;
        var repoRoot = RepositoryRootLocator.Find();
        var corpus = MemoryRetrievalBenchmarkCorpus.Load(repoRoot);
        var multilingual = MemoryRetrievalMultilingualQueries.Load(repoRoot);
        var groups = new (string Name, MemoryRetrievalBenchmarkCorpus Corpus)[]
        {
            ("en", corpus),
            (MemoryRetrievalMultilingualQueries.Polish, multilingual.ToCorpus(corpus, MemoryRetrievalMultilingualQueries.Polish)),
            (MemoryRetrievalMultilingualQueries.Russian, multilingual.ToCorpus(corpus, MemoryRetrievalMultilingualQueries.Russian)),
            ("pl+ru", multilingual.ToCorpus(corpus, MemoryRetrievalMultilingualQueries.Polish, MemoryRetrievalMultilingualQueries.Russian))
        };
        var latencyTexts = groups.Take(3)
            .SelectMany(static group => group.Corpus.Queries.Select(query => (group.Name, query.Text)))
            .ToArray();

        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await using var factory = CreateHost(repoRoot, connectionString);
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMemoryRepository>();
        var hostConfiguration = await scope.ServiceProvider.GetRequiredService<ITriageConfigurationRepository>()
            .GetCurrentAsync(cancellationToken);

        var cacheRoot = ResolveCacheDirectory();
        var candidates = new[]
        {
            ("multilingual-e5-small", LocalEmbeddingModelBenchmarkModel.SmallOptions(cacheRoot)),
            ("multilingual-e5-base", LocalEmbeddingModelBenchmarkModel.BaseOptions(cacheRoot))
        };
        var modelResults = new List<LocalEmbeddingModelBenchmarkModelResult>(candidates.Length);
        foreach (var (shortName, options) in candidates)
        {
            var currentConfiguration = hostConfiguration;
            using var model = await LocalEmbeddingModelBenchmarkModel.InstallAsync(
                shortName,
                options,
                () => currentConfiguration,
                cancellationToken);
            currentConfiguration = LocalEmbeddingModelBenchmarkMeasurements.ConfigurationFor(
                hostConfiguration,
                model,
                LocalEmbeddingModelBenchmarkMeasurements.ShippedMinScore);
            await corpus.SeedWithEmbeddingIdentityAsync(
                connectionString,
                model.Client,
                repository,
                model.EncodedIdentity,
                model.CorpusIdentity,
                cancellationToken);

            var groupResults = new List<LocalEmbeddingModelBenchmarkGroupResult>(groups.Length);
            foreach (var (name, groupCorpus) in groups)
            {
                var pipeline = await LocalEmbeddingModelBenchmarkMeasurements.MeasurePipelineAsync(
                    hostConfiguration,
                    model,
                    repository,
                    groupCorpus,
                    configuration => currentConfiguration = configuration,
                    cancellationToken);
                var fallbackModes = await LocalEmbeddingModelBenchmarkMeasurements.MeasureFallbackModesAsync(
                    hostConfiguration,
                    model,
                    repository,
                    groupCorpus,
                    configuration => currentConfiguration = configuration,
                    cancellationToken);
                var (rawSummary, rawQueries) = await LocalEmbeddingModelBenchmarkMeasurements.MeasureRawAsync(
                    model,
                    repository,
                    groupCorpus,
                    cancellationToken);
                var positiveCount = groupCorpus.Queries.Count(static query => query.RelevantChunkIds.Count > 0);
                groupResults.Add(new LocalEmbeddingModelBenchmarkGroupResult(
                    name,
                    groupCorpus.Queries.Count,
                    positiveCount,
                    groupCorpus.Queries.Count - positiveCount,
                    pipeline,
                    fallbackModes,
                    rawSummary,
                    rawQueries));
            }

            var latency = await LocalEmbeddingModelBenchmarkMeasurements.MeasureLatencyAsync(
                model,
                latencyTexts,
                cancellationToken);
            modelResults.Add(new LocalEmbeddingModelBenchmarkModelResult(
                shortName,
                model.EncodedIdentity,
                model.Installed.Manifest,
                model.InstallSeconds,
                DateTimeOffset.UtcNow,
                groupResults,
                latency));
        }

        var record = new LocalEmbeddingModelBenchmarkRecord(
            2,
            "local-embedding-model-benchmark-v2",
            corpus.CorpusVersion,
            multilingual.Version,
            startedAt,
            DateTimeOffset.UtcNow,
            DescribeMachine(),
            DescribeSettings(corpus),
            modelResults);
        var json = JsonSerializer.Serialize(record, SerializerOptions);
        TestContext.Current.TestOutputHelper?.WriteLine("LOCAL_EMBEDDING_MODEL_BENCHMARK_JSON_BEGIN");
        TestContext.Current.TestOutputHelper?.WriteLine(json);
        TestContext.Current.TestOutputHelper?.WriteLine("LOCAL_EMBEDDING_MODEL_BENCHMARK_JSON_END");
        var outputPath = Environment.GetEnvironmentVariable(OutputVariable);
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, json + "\n", cancellationToken);
        }

        Assert.Equal(candidates.Length, record.Models.Count);
    }

    private static WebApplicationFactory<Program> CreateHost(string repoRoot, string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            builder.UseWorkerModelHost();
            builder.UseSetting(
                "IncidentCompass:ConfigSource:Path",
                Path.Combine(
                    repoRoot,
                    "tests",
                    "IncidentCompass.IntegrationTests",
                    "Fixtures",
                    "test-triage-config",
                    "incidentcompass.config.json"));
            builder.UseSetting("IncidentCompass:Memory:Seed:Enabled", "false");
        });

    private static LocalEmbeddingModelBenchmarkMachine DescribeMachine() => new(
        Environment.ProcessorCount,
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription,
        Avx2.IsSupported,
        AvxVnni.IsSupported,
        Avx512F.IsSupported);

    private static LocalEmbeddingModelBenchmarkSettings DescribeSettings(MemoryRetrievalBenchmarkCorpus corpus) => new(
        corpus.TopK,
        LocalEmbeddingModelBenchmarkMeasurements.PipelineMinScores,
        LocalEmbeddingModelBenchmarkMeasurements.PipelineFallbackModes,
        LocalEmbeddingModelBenchmarkMeasurements.ShippedMinScore,
        LocalEmbeddingModelBenchmarkMeasurements.RawMinScore,
        LocalEmbeddingModelBenchmarkMeasurements.RawRankingDepth,
        LocalEmbeddingModelBenchmarkMeasurements.LatencyWarmupPasses,
        LocalEmbeddingModelBenchmarkMeasurements.LatencyMeasuredPasses,
        1,
        "MemorySearchTool over the real LocalOnnx adapter: memory-embed routed to a LocalOnnx provider naming the" +
        " installed model id, TopK " + corpus.TopK.ToString(CultureInfo.InvariantCulture) +
        ", candidates above MinScore then MemorySearchReranker; metrics from MemoryRetrievalMetrics." +
        " The MinScore sweep runs with Tools.memory_search.VectorOnlyFallback off, so its numbers stay comparable" +
        " with records taken before that setting existed.",
        "The same pipeline at the shipped MinScore under each VectorOnlyFallback mode, with the count of returned" +
        " items per retrievalConfidence band beside the ordinary retrieval metrics.",
        "Repository vector search with MinScore -1 over every active chunk; nDCG@10 with binary chunk relevance," +
        " chunk and item recall@5 and @10 macro-averaged over positive queries.",
        "Direct adapter query embedding per query text (English, Polish, Russian), 5 warm-up and 30 measured passes" +
        " over all texts, one sample per call, IntraOpThreads 1.");

    private static string ResolveCacheDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(CacheDirectoryVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "embedding-model-cache")
            : Path.GetFullPath(configured);
    }

    private static bool IsTruthy(string? value) => value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
}
