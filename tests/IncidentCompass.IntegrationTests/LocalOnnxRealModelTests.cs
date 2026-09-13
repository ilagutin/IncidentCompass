using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Embeddings.LocalOnnx;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The pinned <c>intfloat/multilingual-e5-small</c> model through the real store and adapter. The
/// model directory is <c>INCIDENTCOMPASS_EMBEDDING_MODEL_CACHE</c>, or a directory under the test
/// output; an empty directory is filled through the same fetch-and-verify path production uses.
/// Assertions are about shape and ranking, never vector values.
/// </summary>
public sealed class LocalOnnxRealModelTests
{
    private const string CacheDirectoryVariable = "INCIDENTCOMPASS_EMBEDDING_MODEL_CACHE";

    private static readonly (string Language, string Query, string Relevant, string Unrelated)[] Cases =
    [
        (
            "English",
            "checkout timeout",
            "Checkout requests time out when the payment service responds slowly; raise the payment client timeout and check the gateway latency.",
            "The office coffee machine is descaled every Friday afternoon."),
        (
            "Polish",
            "przekroczenie limitu czasu przy finalizacji zamówienia",
            "Finalizacja zamówienia kończy się przekroczeniem limitu czasu, gdy usługa płatności odpowiada zbyt wolno.",
            "Ekspres do kawy w biurze jest odkamieniany w każdy piątek po południu."),
        (
            "Russian",
            "таймаут при оформлении заказа",
            "Оформление заказа завершается по таймауту, когда платёжный сервис отвечает слишком медленно.",
            "Кофемашину в офисе чистят от накипи каждую пятницу после обеда.")
    ];

    [RealEmbeddingModelFact]
    public async Task PinnedModel_RanksTheRelevantPassageAboveTheUnrelatedOneInEnglishPolishAndRussian()
    {
        var options = new LocalOnnxEmbeddingOptions { ModelDirectory = ResolveCacheDirectory() };
        using var httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var installCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        installCancellation.CancelAfter(TimeSpan.FromSeconds(options.InstallTimeoutSeconds));
        var store = new LocalOnnxModelStore(new LocalOnnxModelFileFetcher(httpClient));
        var installed = await store.EnsureInstalledAsync(options, installCancellation.Token);
        var installState = new LocalOnnxModelInstallState();
        installState.RecordInstalled(installed);
        using var runtime = new LocalOnnxModelRuntime(Options.Create(options));
        var reader = new LocalOnnxInstalledModelReader(
            Options.Create(new EmbeddingOptions { Provider = "LocalOnnx" }),
            Options.Create(options),
            installState,
            store);
        var client = new LocalOnnxEmbeddingClient(reader, runtime, new UnreadConfigurationRepository());

        foreach (var (language, query, relevant, unrelated) in Cases)
        {
            var queryVector = await EmbedAsync(client, options, query, EmbeddingInputKind.Query);
            var relevantVector = await EmbedAsync(client, options, relevant, EmbeddingInputKind.Passage);
            var unrelatedVector = await EmbedAsync(client, options, unrelated, EmbeddingInputKind.Passage);

            var relevantSimilarity = Cosine(queryVector, relevantVector);
            var unrelatedSimilarity = Cosine(queryVector, unrelatedVector);
            Assert.True(
                relevantSimilarity > unrelatedSimilarity,
                $"{language}: the relevant passage scored {relevantSimilarity:F4}, not above the unrelated passage's {unrelatedSimilarity:F4}.");
        }
    }

    private static async Task<IReadOnlyList<float>> EmbedAsync(
        LocalOnnxEmbeddingClient client,
        LocalOnnxEmbeddingOptions options,
        string input,
        EmbeddingInputKind kind)
    {
        var response = await client.CreateEmbeddingAsync(
            new EmbeddingRequest(input, options.ModelId, "local-onnx-real-model-test", kind),
            TestContext.Current.CancellationToken);

        Assert.Equal(384, response.Vector.Count);
        Assert.InRange(Math.Sqrt(response.Vector.Sum(static value => (double)value * value)), 0.999, 1.001);
        return response.Vector;
    }

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        double dot = 0, leftSquares = 0, rightSquares = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += (double)left[index] * right[index];
            leftSquares += (double)left[index] * left[index];
            rightSquares += (double)right[index] * right[index];
        }

        return dot / Math.Sqrt(leftSquares * rightSquares);
    }

    private static string ResolveCacheDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(CacheDirectoryVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "embedding-model-cache")
            : Path.GetFullPath(configured);
    }

    private sealed class UnreadConfigurationRepository : ITriageConfigurationRepository
    {
        public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This test sends no route provider, so no configuration is read.");

        public Task<TriageConfiguration> GetByHashAsync(string configHash, CancellationToken cancellationToken) =>
            GetCurrentAsync(cancellationToken);
    }
}
