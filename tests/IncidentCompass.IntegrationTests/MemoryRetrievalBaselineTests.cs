using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class MemoryRetrievalBaselineTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task LegacyStrategy_ReproducesVersionedDeterministicBaseline()
    {
        await using var harness = await BenchmarkHarness.CreateAsync(postgres);
        var results = await harness.Legacy.ExecuteAsync(
            harness.Corpus,
            TestContext.Current.CancellationToken);
        var actual = MemoryRetrievalMetrics.Evaluate(harness.Corpus, results);
        var baseline = MemoryRetrievalBaselineRecord.Load(FindRepoRoot());

        Assert.Equal(DeterministicJson(baseline.Deterministic), DeterministicJson(actual));
        var falseEmpty = Assert.Single(
            actual.Queries,
            static query => query.QueryId == "false-empty-checkout-timeout");
        Assert.Empty(falseEmpty.ReturnedChunkIds);
        Assert.Equal(6, falseEmpty.FirstRelevantChunkRank);
    }

    [DockerAvailableFact]
    public async Task CaptureV020ProductionBaselineAndLegacyParity_WhenExplicitlyEnabled()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable("INCIDENTCOMPASS_CAPTURE_MEMORY_BASELINE")))
        {
            return;
        }

        await using var harness = await BenchmarkHarness.CreateAsync(postgres);
        var productionRun = await MemoryRetrievalBenchmarkRunner.MeasureAsync(harness.Production, harness.Corpus);
        var legacyRun = await MemoryRetrievalBenchmarkRunner.MeasureAsync(harness.Legacy, harness.Corpus);
        var productionEvaluation = MemoryRetrievalMetrics.Evaluate(harness.Corpus, productionRun.Results);
        var legacyEvaluation = MemoryRetrievalMetrics.Evaluate(harness.Corpus, legacyRun.Results);
        TestContext.Current.TestOutputHelper?.WriteLine("PRODUCTION=" + DeterministicJson(productionEvaluation));
        TestContext.Current.TestOutputHelper?.WriteLine("LEGACY=" + DeterministicJson(legacyEvaluation));
        Assert.Equal(DeterministicJson(productionEvaluation), DeterministicJson(legacyEvaluation));

        var record = new MemoryRetrievalBaselineRecord(
            SchemaVersion: 1,
            CorpusVersion: harness.Corpus.CorpusVersion,
            Strategy: "production-v0.2.0-vector-topk-then-lexical",
            TopK: harness.Corpus.TopK,
            Deterministic: productionEvaluation,
            Diagnostics: new MemoryRetrievalBaselineDiagnostics(
                MemoryRetrievalBenchmarkRunner.WarmupCalls,
                MemoryRetrievalBenchmarkRunner.MeasurementCalls,
                productionRun.Latency,
                legacyRun.Latency));
        TestContext.Current.TestOutputHelper?.WriteLine("MEMORY_RETRIEVAL_BASELINE_JSON_BEGIN");
        TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(record, SerializerOptions));
        TestContext.Current.TestOutputHelper?.WriteLine("MEMORY_RETRIEVAL_BASELINE_JSON_END");

        if (File.Exists(MemoryRetrievalBaselineRecord.PathFor(FindRepoRoot())))
        {
            var persisted = MemoryRetrievalBaselineRecord.Load(FindRepoRoot());
            Assert.Equal(DeterministicJson(persisted.Deterministic), DeterministicJson(record.Deterministic));
        }
    }

    private static string DeterministicJson(MemoryRetrievalEvaluation evaluation) =>
        JsonSerializer.Serialize(evaluation, SerializerOptions);

    private static bool IsTruthy(string? value) => value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IncidentCompass.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

public sealed record MemoryRetrievalBaselineRecord(
    int SchemaVersion,
    string CorpusVersion,
    string Strategy,
    int TopK,
    MemoryRetrievalEvaluation Deterministic,
    MemoryRetrievalBaselineDiagnostics Diagnostics)
{
    public static MemoryRetrievalBaselineRecord Load(string repoRoot) =>
        JsonSerializer.Deserialize<MemoryRetrievalBaselineRecord>(File.ReadAllText(PathFor(repoRoot)), SerializerOptions)
        ?? throw new InvalidOperationException("Memory retrieval baseline is empty.");

    public static string PathFor(string repoRoot) => Path.Combine(
        repoRoot,
        "tests",
        "IncidentCompass.IntegrationTests",
        "Fixtures",
        "MemoryRetrieval",
        "baseline-v0.2.0.json");

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

public sealed record MemoryRetrievalBaselineDiagnostics(
    int WarmupCallsPerStrategy,
    int MeasuredCallsPerStrategy,
    MemoryRetrievalLatencyDiagnostics Production,
    MemoryRetrievalLatencyDiagnostics Legacy);

public sealed record MemoryRetrievalLatencyDiagnostics(double MedianMilliseconds, double P95Milliseconds);

internal sealed record MemoryRetrievalBenchmarkRun(
    IReadOnlyList<MemoryRetrievalQueryResult> Results,
    MemoryRetrievalLatencyDiagnostics Latency);

internal static class MemoryRetrievalBenchmarkRunner
{
    public const int WarmupCalls = 5;
    public const int MeasurementCalls = 30;

    public static async Task<MemoryRetrievalBenchmarkRun> MeasureAsync(
        IMemoryRetrievalBenchmarkStrategy strategy,
        MemoryRetrievalBenchmarkCorpus corpus)
    {
        for (var index = 0; index < WarmupCalls; index++)
        {
            await strategy.ExecuteAsync(corpus, TestContext.Current.CancellationToken);
        }

        var elapsedMilliseconds = new double[MeasurementCalls];
        IReadOnlyList<MemoryRetrievalQueryResult>? results = null;
        for (var index = 0; index < MeasurementCalls; index++)
        {
            var started = Stopwatch.GetTimestamp();
            results = await strategy.ExecuteAsync(corpus, TestContext.Current.CancellationToken);
            var elapsedTicks = Stopwatch.GetTimestamp() - started;
            elapsedMilliseconds[index] = elapsedTicks * 1000d / Stopwatch.Frequency;
        }

        Array.Sort(elapsedMilliseconds);
        var median = (elapsedMilliseconds[14] + elapsedMilliseconds[15]) / 2;
        var p95Index = (int)Math.Ceiling(0.95 * MeasurementCalls) - 1;
        return new MemoryRetrievalBenchmarkRun(
            results!,
            new MemoryRetrievalLatencyDiagnostics(median, elapsedMilliseconds[p95Index]));
    }
}

internal sealed class BenchmarkHarness(
    MemoryRetrievalBenchmarkCorpus corpus,
    IMemoryRetrievalBenchmarkStrategy production,
    IMemoryRetrievalBenchmarkStrategy legacy,
    IServiceScope scope,
    WebApplicationFactory<Program> factory) : IAsyncDisposable
{
    public MemoryRetrievalBenchmarkCorpus Corpus { get; } = corpus;

    public IMemoryRetrievalBenchmarkStrategy Production { get; } = production;

    public IMemoryRetrievalBenchmarkStrategy Legacy { get; } = legacy;

    public static async Task<BenchmarkHarness> CreateAsync(PostgresRepositoryFixture postgres)
    {
        var repoRoot = FindRepoRoot();
        var corpus = MemoryRetrievalBenchmarkCorpus.Load(repoRoot);
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
            builder.UseSetting("IncidentCompass:Embeddings:MockDimensions", corpus.EmbeddingDimensions.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("IncidentCompass:Memory:Seed:Enabled", "false");
        });
        var scope = factory.Services.CreateScope();
        var embeddingClient = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();
        var repository = scope.ServiceProvider.GetRequiredService<IMemoryRepository>();
        await corpus.SeedAsync(
            connectionString,
            embeddingClient,
            repository,
            TestContext.Current.CancellationToken);
        var configuration = await scope.ServiceProvider.GetRequiredService<ITriageConfigurationRepository>()
            .GetCurrentAsync(TestContext.Current.CancellationToken);
        var memoryTool = scope.ServiceProvider.GetServices<IImmediateAgentTool>()
            .Single(static tool => tool.Definition.Name == "memory_search");
        return new BenchmarkHarness(
            corpus,
            new ProductionMemoryRetrievalStrategy(memoryTool, configuration),
            new LegacyMemoryRetrievalStrategy(embeddingClient, repository),
            scope,
            factory);
    }

    public ValueTask DisposeAsync()
    {
        scope.Dispose();
        factory.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IncidentCompass.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
