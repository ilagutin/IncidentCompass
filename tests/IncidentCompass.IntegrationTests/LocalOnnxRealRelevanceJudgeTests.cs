using System.Text.Json;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;
using IncidentCompass.TestSupport;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The pinned <c>BAAI/bge-reranker-v2-m3</c> cross-encoder through the real store and adapter. The
/// model directory is <c>INCIDENTCOMPASS_RELEVANCE_JUDGE_MODEL_CACHE</c>, or a directory under the
/// test output; an empty directory is filled through the same fetch-and-verify path production uses.
/// <para>
/// This is where the reference tokenizer's own pair ids are asserted, because only the real
/// tokenizer file can produce them. It is opt-in: see
/// <see cref="RealRelevanceJudgeModelFactAttribute" />.
/// </para>
/// </summary>
public sealed class LocalOnnxRealRelevanceJudgeTests
{
    private const string CacheDirectoryVariable = "INCIDENTCOMPASS_RELEVANCE_JUDGE_MODEL_CACHE";

    private static readonly (string Language, string Query, string Relevant, string Unrelated)[] RankingCases =
    [
        (
            "English",
            "checkout timeout",
            "Checkout requests time out when the payment service responds slowly; raise the payment client timeout and check the gateway latency.",
            "The office coffee machine is descaled every Friday afternoon."),
        (
            "Polish",
            "przekroczenie limitu czasu przy finalizacji zamowienia",
            "Finalizacja zamowienia konczy sie przekroczeniem limitu czasu, gdy usluga platnosci odpowiada zbyt wolno.",
            "Ekspres do kawy w biurze jest odkamieniany w kazdy piatek po poludniu.")
    ];

    [RealRelevanceJudgeModelFact]
    public async Task PinnedJudge_ProducesTheReferencePairIdsAndRanksTheRelevantPassageHigher()
    {
        var options = new LocalOnnxRelevanceJudgeOptions { ModelDirectory = ResolveCacheDirectory() };
        Assert.Empty(LocalOnnxRelevanceJudgeOptionsValidator.FindFailures(options));
        using var httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var installCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        installCancellation.CancelAfter(TimeSpan.FromSeconds(options.InstallTimeoutSeconds));
        var store = new LocalOnnxModelStore(new LocalOnnxModelFileFetcher(httpClient));
        var installed = await store.EnsureInstalledAsync(options.CreatePin(), installCancellation.Token);

        Assert.Equal(LocalOnnxModelManifest.RelevanceJudgeKind, installed.Manifest.Kind);
        AssertReferencePairIds(installed);
        await AssertRankingAsync(options, store, installed);
    }

    /// <summary>
    /// The pair encoder against the real tokenizer file, id for id, on the sequences the reference
    /// <c>XLMRobertaTokenizerFast</c> produces: a short English pair, an English pair whose query
    /// carries a service name and an exception type, a Polish one, a transliterated Russian one, and
    /// one whose segments have leading and trailing whitespace.
    /// </summary>
    private static void AssertReferencePairIds(LocalOnnxInstalledModel installed)
    {
        var encoder = LocalOnnxPairEncoder.Load(installed.TokenizerFilePath, installed.Manifest);
        var pairs = ReadReferencePairs();
        Assert.Equal(5, pairs.Length);

        foreach (var (query, passage, expectedIds) in pairs)
        {
            var ids = encoder.Encode(query, passage);

            Assert.True(
                expectedIds.SequenceEqual(ids),
                $"The pair encoder produced [{string.Join(", ", ids)}] for a reference pair.");
        }
    }

    private static async Task AssertRankingAsync(
        LocalOnnxRelevanceJudgeOptions options,
        LocalOnnxModelStore store,
        LocalOnnxInstalledModel installed)
    {
        var installState = new LocalOnnxModelInstallState();
        installState.RecordInstalled(installed);
        using var runtime = new LocalOnnxRelevanceJudgeRuntime(Options.Create(options));
        var reader = new LocalOnnxInstalledRelevanceJudgeReader(Options.Create(options), installState, store);
        var judge = new LocalOnnxRelevanceJudgeClient(reader, runtime, Options.Create(options));
        Assert.IsAssignableFrom<IMemoryRelevanceJudge>(judge);

        foreach (var (language, query, relevant, unrelated) in RankingCases)
        {
            var scores = await judge.ScoreAsync(query, [relevant, unrelated], TestContext.Current.CancellationToken);

            Assert.Equal(2, scores.Count);
            Assert.True(
                scores[0] > scores[1],
                $"{language}: the relevant passage scored {scores[0]:F4}, not above the unrelated passage's {scores[1]:F4}.");
        }
    }

    private static (string Query, string Passage, long[] Ids)[] ReadReferencePairs()
    {
        var path = Path.Combine(
            RepositoryRootLocator.Find(), "tests", "Shared", "fixtures", "real-relevance-judge", "reference-pairs.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("pairs").EnumerateArray()
            .Select(static element => (
                element.GetProperty("query").GetString()!,
                element.GetProperty("passage").GetString()!,
                element.GetProperty("ids").EnumerateArray().Select(static id => id.GetInt64()).ToArray()))
            .ToArray();
    }

    private static string ResolveCacheDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(CacheDirectoryVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "relevance-judge-model-cache")
            : Path.GetFullPath(configured);
    }
}
