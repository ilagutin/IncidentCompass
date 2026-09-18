using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;
using IncidentCompass.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Measures the production <c>memory_search</c> pipeline with the real relevance judge behind it, over
/// the grown retrieval corpus in English, Polish and Russian. Opt-in only: it returns at once unless
/// <c>INCIDENTCOMPASS_RELEVANCE_JUDGE_BENCHMARK</c> is truthy, because it installs the pinned
/// cross-encoder, about 544 MiB, and scores every candidate of every query on the CPU.
/// <para>
/// This is where the shipped thresholds come from, and it is deliberately not where an offline sweep
/// comes from. An int8 graph is entitled to differ between runtime builds, and the floor's usable
/// window is fractions of a unit wide, so a value swept under one ONNX runtime over every active chunk
/// does not transfer to the product, which runs a different runtime build over the bounded candidate
/// set the vector search returned. Every number here goes through the real tool.
/// </para>
/// <para>
/// The run has three parts: a capture pass that scores every (query, candidate) pair once and records
/// the boundary the floor sits between, the two shipped-default legs the acceptance bar is asserted on,
/// and a sweep of each threshold over its own list of values. The sweep reuses the captured scores, so
/// it costs no model time. The result is one JSON document, written to the test output and to
/// <c>INCIDENTCOMPASS_RELEVANCE_JUDGE_BENCHMARK_OUTPUT</c> when it is set.
/// </para>
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class MemorySearchRelevanceJudgeBenchmarkTests(PostgresRepositoryFixture postgres)
{
    private const string EnabledVariable = "INCIDENTCOMPASS_RELEVANCE_JUDGE_BENCHMARK";
    private const string EmbeddingCacheVariable = "INCIDENTCOMPASS_EMBEDDING_MODEL_CACHE";
    private const string JudgeCacheVariable = "INCIDENTCOMPASS_RELEVANCE_JUDGE_MODEL_CACHE";
    private const string OutputVariable = "INCIDENTCOMPASS_RELEVANCE_JUDGE_BENCHMARK_OUTPUT";
    private const string EnglishLanguage = "en";
    private const string JudgeOnLeg = "judge-on";
    private const string JudgeOffLeg = "judge-off";
    private const string MemorySearchToolName = "memory_search";
    private const int SchemaVersion = 2;

    /// <summary>
    /// Polish chunk recall@5 was 0.000 before a judge existed: an all-Latin query in another language
    /// left the lexical gate with nothing and the foreign-script fallback did not fire. Raising it is
    /// the defect this work fixes, so the bar names a floor for it rather than trusting the change.
    /// </summary>
    private const double MinimumPolishChunkRecall = 0.5;

    /// <summary>
    /// Why a candidate floor value is not measured. A floor at or above the confirm score it is swept
    /// against is refused by load validation when it is above and carries no information when it is
    /// equal, because the unconfirmed band is then empty by construction.
    /// </summary>
    public const string FloorExclusionRule = "floor >= held confirm score";

    /// <summary>
    /// Why a candidate confirm value is not measured. A confirm score below the floor it is swept
    /// against is a pair load validation refuses. Equality is kept: it is the representable extreme
    /// where everything admitted is confirmed, and it is the natural bottom end of this curve.
    /// </summary>
    public const string ConfirmExclusionRule = "confirm < held floor score";

    /// <summary>
    /// The candidate floor values. The measured scores of this judge run from roughly -9 to +8, so the
    /// list runs from a floor that admits every candidate to one that drops all but a confirmed match,
    /// and it is dense exactly where the answer lives. Coarse steps of 1.0 from -9 to -3 and 0.25 from
    /// -3 to -2 establish the shape; 0.025 from -2 to 0 is the working range, fine enough that a window
    /// even a third as wide as the one measured offline still contains several samples; six values above
    /// zero show the curve breaking. Nothing is interpolated: a value that is not in this list was not
    /// measured.
    /// <para>
    /// Which of these are actually measured is derived from the confirm score they are swept against,
    /// by <see cref="FloorScoresFor" />, rather than trimmed here by hand. The list has to keep covering
    /// clearly-too-high whatever the confirm default becomes, and a hand-trimmed tail would either stop
    /// short of a raised default or crash against a lowered one.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<double> FloorScoreCandidates = BuildValues(
        [(-9.0, -3.0, 1.0), (-3.0, -2.0, 0.25), (-2.0, 0.0, 0.025)],
        [0.25, 0.5, 0.75, 1.0, 1.25, 1.5]);

    /// <summary>
    /// The candidate confirm values. Its only job is keeping a hard negative out of the confirmed band
    /// while leaving a real answer in it, and those two populations sat about two units apart offline,
    /// so the list steps 0.05 across the whole of that gap and well past both ends, then takes four
    /// coarse values to show the confirmed band emptying. Which of these are measured is derived from
    /// the floor they are swept against, by <see cref="ConfirmScoresFor" />, for the same reason.
    /// </summary>
    public static readonly IReadOnlyList<double> ConfirmScoreCandidates = BuildValues(
        [(-2.0, 3.0, 0.05)],
        [4.0, 5.0, 6.0, 8.0]);

    /// <summary>The floor values that form a representable, informative pair with this confirm score.</summary>
    public static IReadOnlyList<double> FloorScoresFor(double heldConfirmScore) =>
        FloorScoreCandidates.Where(value => value < heldConfirmScore).ToArray();

    /// <summary>The confirm values that form a representable pair with this floor.</summary>
    public static IReadOnlyList<double> ConfirmScoresFor(double heldFloorScore) =>
        ConfirmScoreCandidates.Where(value => value >= heldFloorScore).ToArray();

    [DockerAvailableFact]
    public async Task MeasureMemorySearchWithTheRealRelevanceJudge_WhenExplicitlyEnabled()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnabledVariable)))
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var startedAt = DateTimeOffset.UtcNow;
        var repoRoot = RepositoryRootLocator.Find();
        var corpus = MemoryRetrievalBenchmarkCorpus.LoadV2(repoRoot);
        var multilingual = MemoryRetrievalMultilingualQueries.LoadV2(repoRoot);
        var groups = new (string Name, MemoryRetrievalBenchmarkCorpus Corpus)[]
        {
            (EnglishLanguage, corpus),
            (MemoryRetrievalMultilingualQueries.Polish,
                multilingual.ToCorpus(corpus, MemoryRetrievalMultilingualQueries.Polish)),
            (MemoryRetrievalMultilingualQueries.Russian,
                multilingual.ToCorpus(corpus, MemoryRetrievalMultilingualQueries.Russian))
        };

        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await using var factory = CreateHost(repoRoot, connectionString);
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMemoryRepository>();
        var hostConfiguration = await scope.ServiceProvider.GetRequiredService<ITriageConfigurationRepository>()
            .GetCurrentAsync(cancellationToken);

        var currentConfiguration = hostConfiguration;
        using var model = await LocalEmbeddingModelBenchmarkModel.InstallAsync(
            "multilingual-e5-small",
            LocalEmbeddingModelBenchmarkModel.SmallOptions(
                ResolveCacheDirectory(EmbeddingCacheVariable, "embedding-model-cache")),
            () => currentConfiguration,
            cancellationToken);
        currentConfiguration = ConfigurationFor(hostConfiguration, model, MemoryRelevanceJudgeSetting.On);
        await corpus.SeedWithEmbeddingIdentityAsync(
            connectionString,
            model.Client,
            repository,
            model.EncodedIdentity,
            model.CorpusIdentity,
            cancellationToken);

        var judgeOptions = new LocalOnnxRelevanceJudgeOptions
        {
            ModelDirectory = ResolveCacheDirectory(JudgeCacheVariable, "relevance-judge-model-cache")
        };
        using var judgeRuntime = new LocalOnnxRelevanceJudgeRuntime(Options.Create(judgeOptions));
        var judge = new RecordingMemoryRelevanceJudge(
            await InstallJudgeAsync(judgeOptions, judgeRuntime, cancellationToken));
        var embeddingClient = new CachingBenchmarkEmbeddingClient(model.Client);

        var shipped = ConfigurationFor(hostConfiguration, model, MemoryRelevanceJudgeSetting.On);
        var route = shipped.Routes[shipped.Tools[MemorySearchToolName].EmbeddingRouteId!];
        var topK = shipped.Tools[MemorySearchToolName].TopK ?? corpus.TopK;
        var candidateCount = Math.Min(100, topK * 4);
        var scoring = await CaptureScoresAsync(
            groups, embeddingClient, repository, judge, route, candidateCount, cancellationToken);

        judge.ResetModelCalls();
        var legs = new List<MemorySearchRelevanceJudgeBenchmarkLeg>(2);
        foreach (var legName in new[] { JudgeOnLeg, JudgeOffLeg })
        {
            var mode = legName == JudgeOnLeg ? MemoryRelevanceJudgeSetting.On : MemoryRelevanceJudgeSetting.Off;
            legs.Add(new MemorySearchRelevanceJudgeBenchmarkLeg(
                legName,
                mode,
                await MeasureAsync(
                    groups, hostConfiguration, model, repository, embeddingClient, judge, mode,
                    MemoryRelevanceJudgeSetting.DefaultConfirmScore,
                    MemoryRelevanceJudgeSetting.DefaultFloorScore,
                    configuration => currentConfiguration = configuration,
                    cancellationToken)));
        }

        var sweeps = new[]
        {
            await SweepAsync(
                MemoryRelevanceJudgeSetting.FloorScoreSettingName,
                FloorScoreCandidates,
                FloorScoresFor(MemoryRelevanceJudgeSetting.DefaultConfirmScore),
                FloorExclusionRule,
                value => (MemoryRelevanceJudgeSetting.DefaultConfirmScore, value),
                groups, hostConfiguration, model, repository, embeddingClient, judge,
                configuration => currentConfiguration = configuration, cancellationToken),
            await SweepAsync(
                MemoryRelevanceJudgeSetting.ConfirmScoreSettingName,
                ConfirmScoreCandidates,
                ConfirmScoresFor(MemoryRelevanceJudgeSetting.DefaultFloorScore),
                ConfirmExclusionRule,
                value => (value, MemoryRelevanceJudgeSetting.DefaultFloorScore),
                groups, hostConfiguration, model, repository, embeddingClient, judge,
                configuration => currentConfiguration = configuration, cancellationToken)
        };

        var record = new MemorySearchRelevanceJudgeBenchmarkRecord(
            SchemaVersion,
            "memory-search-relevance-judge",
            corpus.CorpusVersion,
            multilingual.Version,
            model.EncodedIdentity,
            judgeOptions.ModelId,
            RuntimeInformation.FrameworkDescription,
            MemoryRelevanceJudgeSetting.DefaultConfirmScore,
            MemoryRelevanceJudgeSetting.DefaultFloorScore,
            LocalEmbeddingModelBenchmarkMeasurements.ShippedMinScore,
            topK,
            candidateCount,
            RuntimeInformation.OSDescription,
            startedAt,
            DateTimeOffset.UtcNow,
            scoring with { JudgeModelCallsDuringSweep = judge.ModelCalls },
            legs,
            sweeps);
        // The record is written before the bar is asserted, and these two lines must not be swapped. A
        // run that fails the bar is exactly the run whose curve is needed to choose a replacement
        // threshold, and asserting first would throw the whole measurement away.
        await WriteAsync(record, cancellationToken);
        AssertAcceptanceBar(record);
    }

    /// <summary>
    /// The bar the leg exists for, asserted on the shipped defaults and on nothing else, so the leg
    /// still fails when a default is wrong; the sweep beside it is how a replacement is chosen, not how
    /// the bar is met.
    /// <para>
    /// It deliberately does not ask for English chunk recall@5 of 1.000. Measured through the product
    /// the English window is negative: the judge scores an off-topic query about the same service above
    /// a keyword query's secondary chunk, so leaving every off-topic query empty and returning every
    /// labelled chunk cannot both hold. The release chooses the first, and the bar asks instead that no
    /// positive query comes back empty, which is what recall@5 was standing in for and is the statement
    /// that actually matters: losing a second chunk that repeats an answer already returned is not
    /// losing the answer.
    /// </para>
    /// <para>
    /// Every count is asserted against its denominator first. A corpus that predates the category field
    /// names no off-topic query at all, and on such a corpus a false-positive count of zero is vacuously
    /// true: the bar would then pass without measuring anything.
    /// </para>
    /// </summary>
    private static void AssertAcceptanceBar(MemorySearchRelevanceJudgeBenchmarkRecord record)
    {
        var languages = record.Legs
            .Single(static leg => leg.Name == JudgeOnLeg)
            .Languages;
        var english = languages.Single(static language => language.Language == EnglishLanguage);
        var polish = languages.Single(static language =>
            language.Language == MemoryRetrievalMultilingualQueries.Polish);
        var boundary = record.Scoring.ByLanguage
            .Single(static language => language.Language == EnglishLanguage);

        Assert.True(
            english.PositiveQueryCount > 0,
            "The corpus contributed no English positive query, so the unanswered count measures nothing.");
        Assert.True(
            english.UnansweredPositiveQueryCount == 0,
            $"{english.UnansweredPositiveQueryCount} of {english.PositiveQueryCount} English positive" +
            $" queries returned none of their labelled chunks, at recall@5 {english.ChunkRecallAt5:F3}." +
            $" The English boundary is {Describe(boundary)}.");

        Assert.True(
            english.OffTopicQueryCount > 0,
            "The corpus contributed no off-topic query, so the off-topic false-positive count measures nothing.");
        Assert.True(
            english.OffTopicFalsePositiveCount == 0,
            $"English off-topic false positives with the judge on are {english.OffTopicFalsePositiveCount}" +
            $" of {english.OffTopicQueryCount}, not 0. The English boundary is {Describe(boundary)}.");

        Assert.All(languages, language =>
        {
            Assert.True(
                language.HardNegativeQueryCount > 0,
                $"The {language.Language} corpus contributed no hard negative, so the confirmed count" +
                " measures nothing.");
            Assert.True(
                language.HardNegativeConfirmedCount == 0,
                $"{language.Language} confirmed {language.HardNegativeConfirmedCount} of" +
                $" {language.HardNegativeQueryCount} hard negatives, of" +
                $" {language.HardNegativeReturnedCount} returned, not 0.");
        });

        Assert.True(
            polish.PositiveQueryCount > 0,
            "The corpus contributed no Polish positive query, so Polish recall@5 measures nothing.");
        Assert.True(
            polish.ChunkRecallAt5 >= MinimumPolishChunkRecall,
            $"Polish chunk recall@5 with the judge on is {polish.ChunkRecallAt5:F3} over" +
            $" {polish.PositiveQueryCount} positive queries, below {MinimumPolishChunkRecall:F3}. It was" +
            " 0.000 before the judge existed, and raising it is the defect this work fixes.");
    }

    private static string Describe(MemorySearchRelevanceJudgeBenchmarkBoundary boundary) =>
        $"strongest off-topic {boundary.StrongestOffTopicScore:F3} ({boundary.StrongestOffTopicQueryId})," +
        $" weakest relevant {boundary.WeakestRelevantScore:F3} ({boundary.WeakestRelevantQueryId})," +
        $" window {boundary.WindowWidth:F3}, separable {boundary.SeparableByAFloor}; read the sweep in the" +
        " record for the whole curve.";

    private static async Task<MemorySearchRelevanceJudgeBenchmarkScoring> CaptureScoresAsync(
        (string Name, MemoryRetrievalBenchmarkCorpus Corpus)[] groups,
        IEmbeddingClient embeddingClient,
        IMemoryRepository repository,
        RecordingMemoryRelevanceJudge judge,
        TriageRouteSettings route,
        int candidateCount,
        CancellationToken cancellationToken)
    {
        var pairs = new List<MemorySearchRelevanceJudgeBenchmarkPair>();
        var boundaries = new List<MemorySearchRelevanceJudgeBenchmarkBoundary>(groups.Length);
        foreach (var (name, groupCorpus) in groups)
        {
            var captured = await MemorySearchRelevanceJudgeBenchmarkScores.CaptureAsync(
                name,
                groupCorpus,
                embeddingClient,
                repository,
                judge,
                route.Model,
                route.ProviderId,
                candidateCount,
                LocalEmbeddingModelBenchmarkMeasurements.ShippedMinScore,
                cancellationToken);
            pairs.AddRange(captured);
            boundaries.Add(MemorySearchRelevanceJudgeBenchmarkScores.Summarize(name, captured));
        }

        return new MemorySearchRelevanceJudgeBenchmarkScoring(
            judge.ScoredPairs,
            judge.ModelCalls,
            0,
            boundaries,
            MemorySearchRelevanceJudgeBenchmarkScores.Summarize(
                MemorySearchRelevanceJudgeBenchmarkScores.PooledLanguage, pairs));
    }

    private static async Task<MemorySearchRelevanceJudgeBenchmarkSweep> SweepAsync(
        string threshold,
        IReadOnlyList<double> candidates,
        IReadOnlyList<double> values,
        string exclusionRule,
        Func<double, (double ConfirmScore, double FloorScore)> thresholdsFor,
        (string Name, MemoryRetrievalBenchmarkCorpus Corpus)[] groups,
        TriageConfiguration hostConfiguration,
        LocalEmbeddingModelBenchmarkModel model,
        IMemoryRepository repository,
        IEmbeddingClient embeddingClient,
        IMemoryRelevanceJudge judge,
        Action<TriageConfiguration> setCurrentConfiguration,
        CancellationToken cancellationToken)
    {
        var points = new List<MemorySearchRelevanceJudgeBenchmarkSweepPoint>(values.Count);
        foreach (var value in values)
        {
            var (confirmScore, floorScore) = thresholdsFor(value);
            points.Add(new MemorySearchRelevanceJudgeBenchmarkSweepPoint(
                value,
                await MeasureAsync(
                    groups, hostConfiguration, model, repository, embeddingClient, judge,
                    MemoryRelevanceJudgeSetting.On, confirmScore, floorScore,
                    setCurrentConfiguration, cancellationToken)));
        }

        return new MemorySearchRelevanceJudgeBenchmarkSweep(
            threshold,
            MemoryRelevanceJudgeSetting.DefaultConfirmScore,
            MemoryRelevanceJudgeSetting.DefaultFloorScore,
            candidates.Count,
            candidates.Except(values).Order().ToArray(),
            exclusionRule,
            points);
    }

    /// <summary>
    /// One setting of both thresholds, measured through the real <c>memory_search</c> in every language.
    /// </summary>
    private static async Task<IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkLanguage>> MeasureAsync(
        (string Name, MemoryRetrievalBenchmarkCorpus Corpus)[] groups,
        TriageConfiguration hostConfiguration,
        LocalEmbeddingModelBenchmarkModel model,
        IMemoryRepository repository,
        IEmbeddingClient embeddingClient,
        IMemoryRelevanceJudge judge,
        string relevanceJudge,
        double confirmScore,
        double floorScore,
        Action<TriageConfiguration> setCurrentConfiguration,
        CancellationToken cancellationToken)
    {
        var languages = new List<MemorySearchRelevanceJudgeBenchmarkLanguage>(groups.Length);
        foreach (var (name, groupCorpus) in groups)
        {
            var configuration = ConfigurationFor(
                hostConfiguration, model, relevanceJudge, confirmScore, floorScore);
            setCurrentConfiguration(configuration);
            var results = await new ProductionMemoryRetrievalStrategy(
                    new MemorySearchTool(embeddingClient, repository, judge),
                    configuration)
                .ExecuteAsync(groupCorpus, cancellationToken);
            var evaluation = MemoryRetrievalMetrics.Evaluate(groupCorpus, results);
            var metrics = evaluation.Metrics;
            languages.Add(new MemorySearchRelevanceJudgeBenchmarkLanguage(
                name,
                metrics.ChunkMacroRecallAt5,
                metrics.PositiveQueryCount,
                CountUnansweredPositiveQueries(evaluation),
                metrics.OffTopicQueryCount,
                metrics.OffTopicFalsePositiveCount,
                metrics.HardNegativeQueryCount,
                metrics.HardNegativeReturnedCount,
                metrics.HardNegativeConfirmedCount,
                CountBands(results)));
        }

        return languages;
    }

    /// <summary>
    /// The pinned judge through the real store and the production adapter, installed into its own
    /// directory exactly the way the Worker's install pass does it.
    /// </summary>
    private static async Task<IMemoryRelevanceJudge> InstallJudgeAsync(
        LocalOnnxRelevanceJudgeOptions options,
        LocalOnnxRelevanceJudgeRuntime runtime,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.ModelDirectory!);
        using var httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var installCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        installCancellation.CancelAfter(TimeSpan.FromSeconds(options.InstallTimeoutSeconds));
        var store = new LocalOnnxModelStore(new LocalOnnxModelFileFetcher(httpClient));
        var installed = await store.EnsureInstalledAsync(options.CreatePin(), installCancellation.Token);
        var installState = new LocalOnnxRelevanceJudgeInstallState();
        installState.RecordInstalled(installed);
        return new LocalOnnxRelevanceJudgeClient(
            new LocalOnnxInstalledRelevanceJudgeReader(Options.Create(options), installState, store),
            runtime,
            Options.Create(options));
    }

    /// <summary>
    /// The host configuration with <c>memory-embed</c> routed to the installed local model and
    /// <c>memory_search</c> at the shipped floor under the given judge settings. The vector-only
    /// fallback is left at the shipped default so the judge-off leg is the shipped pre-judge behaviour
    /// rather than a stricter one.
    /// </summary>
    private static TriageConfiguration ConfigurationFor(
        TriageConfiguration hostConfiguration,
        LocalEmbeddingModelBenchmarkModel model,
        string relevanceJudge,
        double? confirmScore = null,
        double? floorScore = null)
    {
        var configuration = LocalEmbeddingModelBenchmarkMeasurements.ConfigurationFor(
            hostConfiguration,
            model,
            LocalEmbeddingModelBenchmarkMeasurements.ShippedMinScore,
            MemorySearchVectorOnlyFallbackSetting.ForeignScript);
        var tools = new Dictionary<string, TriageToolSettings>(configuration.Tools, StringComparer.Ordinal);
        tools[MemorySearchToolName] = tools[MemorySearchToolName] with
        {
            RelevanceJudge = relevanceJudge,
            RelevanceConfirmScore = confirmScore,
            RelevanceFloorScore = floorScore
        };
        return configuration with { Tools = tools };
    }

    /// <summary>
    /// Positive queries that came back with none of their labelled relevant chunks. It is derived from
    /// the per-query outcomes here rather than added to <see cref="MemoryRetrievalMetricSummary" />,
    /// because that summary is the shape a versioned baseline is compared on and a new field in it
    /// would not deserialize from the persisted record.
    /// </summary>
    private static int CountUnansweredPositiveQueries(MemoryRetrievalEvaluation evaluation) =>
        evaluation.Queries.Count(static query =>
            query.RelevantChunkCount > 0 && query.RelevantChunkHits == 0);

    private static Dictionary<string, int> CountBands(IReadOnlyList<MemoryRetrievalQueryResult> results)
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

    /// <summary>
    /// Builds a sweep list from inclusive ranges plus loose values, rounded so a value is written the
    /// way it was meant rather than the way repeated addition leaves it, and de-duplicated where two
    /// ranges meet.
    /// </summary>
    private static double[] BuildValues(
        IReadOnlyList<(double From, double To, double Step)> ranges,
        IReadOnlyList<double> extras) =>
        ranges
            .SelectMany(static range => Enumerable
                .Range(0, (int)Math.Round((range.To - range.From) / range.Step) + 1)
                .Select(index => Math.Round(range.From + (index * range.Step), 4)))
            .Concat(extras)
            .Distinct()
            .Order()
            .ToArray();

    private static async Task WriteAsync(
        MemorySearchRelevanceJudgeBenchmarkRecord record,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(record, SerializerOptions);
        TestContext.Current.TestOutputHelper?.WriteLine("MEMORY_SEARCH_RELEVANCE_JUDGE_BENCHMARK_JSON_BEGIN");
        TestContext.Current.TestOutputHelper?.WriteLine(json);
        TestContext.Current.TestOutputHelper?.WriteLine("MEMORY_SEARCH_RELEVANCE_JUDGE_BENCHMARK_JSON_END");
        var outputPath = Environment.GetEnvironmentVariable(OutputVariable);
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, json + "\n", cancellationToken);
        }
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

    private static string ResolveCacheDirectory(string variableName, string fallbackDirectoryName)
    {
        var configured = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, fallbackDirectoryName)
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
