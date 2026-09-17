using System.Diagnostics;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Embeddings.LocalOnnx;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// One candidate local embedding model for the benchmark: its pinned artifacts, installed through the
/// product's model store into its own directory, and the production adapter running it with one
/// intra-op thread.
/// </summary>
internal sealed class LocalEmbeddingModelBenchmarkModel : IDisposable
{
    public const string RouteProviderId = "local-embed";

    private const string BaseRevision = "d128750597153bb5987e10b1c3493a34e5a4502a";

    private const string BaseResolveUrl =
        "https://huggingface.co/intfloat/multilingual-e5-base/resolve/" + BaseRevision + "/";

    private readonly LocalOnnxModelRuntime runtime;

    private LocalEmbeddingModelBenchmarkModel(
        string shortName,
        LocalOnnxInstalledModel installed,
        LocalOnnxModelRuntime runtime,
        LocalOnnxEmbeddingClient client,
        double installSeconds)
    {
        ShortName = shortName;
        Installed = installed;
        this.runtime = runtime;
        Client = client;
        InstallSeconds = installSeconds;
    }

    public string ShortName { get; }

    public LocalOnnxInstalledModel Installed { get; }

    public IEmbeddingClient Client { get; }

    public double InstallSeconds { get; }

    public string EncodedIdentity => LocalOnnxModelIdentity.Describe(Installed.Manifest);

    public MemoryCorpusIdentity CorpusIdentity => new(
        "memory-embed",
        RouteProviderId,
        LocalOnnxEmbeddingProvider.Name,
        EncodedIdentity,
        Installed.Manifest.GetEmbeddingProfile().Dimensions);

    public static LocalOnnxEmbeddingOptions SmallOptions(string cacheRoot) => new()
    {
        ModelDirectory = Path.Combine(cacheRoot, "multilingual-e5-small"),
        IntraOpThreads = 1
    };

    public static LocalOnnxEmbeddingOptions BaseOptions(string cacheRoot) => new()
    {
        ModelDirectory = Path.Combine(cacheRoot, "multilingual-e5-base"),
        ModelId = "intfloat/multilingual-e5-base",
        Revision = BaseRevision,
        ModelFileUrl = BaseResolveUrl + "onnx/model_qint8_avx512_vnni.onnx",
        ModelFileSha256 = "2523551878658b305550d8759443822dbfda9ed9c8012ef2c354ba2c5b9de503",
        TokenizerFileUrl = BaseResolveUrl + "onnx/sentencepiece.bpe.model",
        TokenizerFileSha256 = "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865",
        Dimensions = 768,
        License = "MIT",
        IntraOpThreads = 1
    };

    /// <summary>
    /// Installs the pinned model through the store, then reads it back from the store, so the manifest
    /// the benchmark records is the one on disk. An existing manifest for a different model is refused
    /// rather than measured under the wrong name.
    /// </summary>
    public static async Task<LocalEmbeddingModelBenchmarkModel> InstallAsync(
        string shortName,
        LocalOnnxEmbeddingOptions options,
        Func<TriageConfiguration> currentConfiguration,
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
        var started = Stopwatch.GetTimestamp();
        await store.EnsureInstalledAsync(options.CreatePin(), installCancellation.Token);
        var installed = await store.ReadInstalledAsync(options.CreatePin(), cancellationToken)
            ?? throw new InvalidOperationException("The model store reports no installed model after install.");
        var installSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        if (installed.Manifest != LocalOnnxModelStore.CreateManifest(options.CreatePin()))
        {
            throw new InvalidOperationException(
                "The model directory for " + shortName + " holds another manifest than the pinned one.");
        }

        var installState = new LocalOnnxModelInstallState();
        installState.RecordInstalled(installed);
        var runtime = new LocalOnnxModelRuntime(Options.Create(options));
        var reader = new LocalOnnxInstalledModelReader(
            Options.Create(new EmbeddingOptions { Provider = "LocalOnnx" }),
            Options.Create(options),
            installState,
            store);
        var client = new LocalOnnxEmbeddingClient(
            reader,
            runtime,
            new BenchmarkConfigurationRepository(currentConfiguration));
        return new LocalEmbeddingModelBenchmarkModel(shortName, installed, runtime, client, installSeconds);
    }

    public void Dispose() => runtime.Dispose();

    private sealed class BenchmarkConfigurationRepository(Func<TriageConfiguration> currentConfiguration)
        : ITriageConfigurationRepository
    {
        public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(currentConfiguration());

        public Task<TriageConfiguration> GetByHashAsync(string configHash, CancellationToken cancellationToken) =>
            GetCurrentAsync(cancellationToken);
    }
}
