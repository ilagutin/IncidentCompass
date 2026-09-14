using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// A memory route served by the local model is judged against the installed model before anything is
/// embedded: another model's id blocks it as a mismatch, no usable model blocks it as unavailable, a
/// model file replaced under the same id is a route change, and a matching model builds the corpus
/// under the encoded identity.
/// </summary>
public sealed class MemorySeedSynchronizerLocalModelTests : IDisposable
{
    private static readonly Guid ExistingGeneration = Guid.Parse("7a000000-0000-0000-0000-000000000001");

    private readonly LocalOnnxTestDirectory sourceDirectory = new();

    public void Dispose() => sourceDirectory.Dispose();

    [Theory]
    [InlineData("Incremental")]
    [InlineData("Rebuild")]
    public async Task Synchronize_WhenTheInstalledModelHasAnotherId_BlocksWithAModelMismatchBeforeEmbedding(string mode)
    {
        var embedding = new RecordingEmbeddingClient();
        var memory = new InventoryMemoryRepository(
            LocalModelTestSupport.Corpus(LocalModelTestSupport.Encoded(LocalModelTestSupport.FirstDigest), ExistingGeneration));
        var synchronizer = CreateSynchronizer(
            LocalModelTestSupport.InstalledState("intfloat/multilingual-e5-base", LocalModelTestSupport.FirstDigest),
            embedding,
            memory);

        var outcome = await synchronizer.SynchronizeAsync(Enum.Parse<MemorySeedSyncMode>(mode), TestContext.Current.CancellationToken);

        Assert.False(outcome.Published);
        Assert.Equal(MemoryCorpusState.EmbeddingModelMismatch, outcome.State);
        Assert.Equal(ExistingGeneration, outcome.Generation);
        Assert.Empty(embedding.Requests);
        Assert.Equal(0, memory.ReconcileCount);
    }

    [Theory]
    [InlineData("Incremental")]
    [InlineData("Rebuild")]
    public async Task Synchronize_WithNoUsableInstalledModel_BlocksAsUnavailableAndCarriesTheInstallCode(string mode)
    {
        var embedding = new RecordingEmbeddingClient();
        var memory = new InventoryMemoryRepository(
            LocalModelTestSupport.Corpus(LocalModelTestSupport.Encoded(LocalModelTestSupport.FirstDigest), ExistingGeneration));
        var synchronizer = CreateSynchronizer(
            LocalModelTestSupport.FailedState(LocalOnnxModelErrorCodes.FetchFailed),
            embedding,
            memory);

        var outcome = await synchronizer.SynchronizeAsync(Enum.Parse<MemorySeedSyncMode>(mode), TestContext.Current.CancellationToken);

        Assert.False(outcome.Published);
        Assert.Equal(MemoryCorpusState.EmbeddingModelUnavailable, outcome.State);
        Assert.Equal(LocalOnnxModelErrorCodes.FetchFailed, outcome.ModelErrorCode);
        Assert.Equal(ExistingGeneration, outcome.Generation);
        Assert.Empty(embedding.Requests);
        Assert.Equal(0, memory.ReconcileCount);
    }

    [Fact]
    public async Task Synchronize_WhenTheModelFileChangedUnderTheSameId_IsARouteChange()
    {
        var embedding = new RecordingEmbeddingClient();
        var memory = new InventoryMemoryRepository(
            LocalModelTestSupport.Corpus(LocalModelTestSupport.Encoded(LocalModelTestSupport.FirstDigest), ExistingGeneration));
        var synchronizer = CreateSynchronizer(
            LocalModelTestSupport.InstalledState(LocalModelTestSupport.ModelId, LocalModelTestSupport.SecondDigest),
            embedding,
            memory);

        var outcome = await synchronizer.SynchronizeAsync(MemorySeedSyncMode.Incremental, TestContext.Current.CancellationToken);

        Assert.False(outcome.Published);
        Assert.Equal(MemoryCorpusState.EmbeddingRouteChanged, outcome.State);
        Assert.Equal(LocalModelTestSupport.Encoded(LocalModelTestSupport.SecondDigest), outcome.Route.Model);
        Assert.Empty(embedding.Requests);
    }

    [Fact]
    public async Task Synchronize_WithTheConfiguredModelInstalled_BuildsTheCorpusUnderTheEncodedIdentity()
    {
        await WriteSeedFileAsync();
        var embedding = new RecordingEmbeddingClient();
        var memory = new InventoryMemoryRepository(new MemoryCorpusInventory(null, [], 0, 0));
        var synchronizer = CreateSynchronizer(
            LocalModelTestSupport.InstalledState(LocalModelTestSupport.ModelId, LocalModelTestSupport.FirstDigest),
            embedding,
            memory);

        var outcome = await synchronizer.SynchronizeAsync(MemorySeedSyncMode.Incremental, TestContext.Current.CancellationToken);

        var encoded = LocalModelTestSupport.Encoded(LocalModelTestSupport.FirstDigest);
        Assert.True(outcome.Published);
        Assert.Equal(encoded, Assert.Single(embedding.Requests).Model);
        Assert.Equal(encoded, memory.Reconciled!.Identity.EmbeddingModel);
    }

    /// <summary>
    /// A Mock host has no local model, and the mock adapter answers the local route itself, so the route
    /// is neither judged nor encoded: the corpus is built under the model id the route names.
    /// </summary>
    [Fact]
    public async Task Synchronize_OnAMockHost_BuildsTheLocalRouteAsConfiguredWithoutReadingAnInstalledModel()
    {
        await WriteSeedFileAsync();
        var embedding = new RecordingEmbeddingClient();
        var memory = new InventoryMemoryRepository(new MemoryCorpusInventory(null, [], 0, 0));
        var synchronizer = CreateSynchronizer(
            LocalModelTestSupport.FailedState(LocalOnnxModelErrorCodes.NotInstalled),
            embedding,
            memory,
            hostProvider: "Mock");

        var outcome = await synchronizer.SynchronizeAsync(MemorySeedSyncMode.Incremental, TestContext.Current.CancellationToken);

        Assert.True(outcome.Published);
        Assert.Equal(LocalModelTestSupport.ModelId, Assert.Single(embedding.Requests).Model);
        Assert.Equal(LocalModelTestSupport.ModelId, memory.Reconciled!.Identity.EmbeddingModel);
    }

    private MemorySeedSynchronizer CreateSynchronizer(
        LocalOnnxModelInstallState state,
        IEmbeddingClient embedding,
        IMemoryRepository memory,
        string hostProvider = "LocalOnnx") =>
        new(
            Options.Create(new MemorySeedOptions
            {
                Enabled = true,
                TenantId = "local",
                Owner = "local-model",
                SourceDirectory = sourceDirectory.FullPath
            }),
            new TestHostEnvironment(sourceDirectory.FullPath),
            new StaticConfigurationRepository(LocalModelTestSupport.Configuration()),
            new WorkerMemoryEmbeddingRouteResolver(
                Options.Create(new EmbeddingOptions { Provider = hostProvider }),
                LocalModelTestSupport.Reader(state, provider: hostProvider)),
            embedding,
            memory,
            new CharacterEstimateChunkTokenCounter());

    private async Task WriteSeedFileAsync()
    {
        var runbooks = Path.Combine(sourceDirectory.FullPath, "runbooks");
        Directory.CreateDirectory(runbooks);
        await File.WriteAllTextAsync(
            Path.Combine(runbooks, "checkout-timeout.md"),
            "---\nkind: Runbook\nservice: checkout-api\n---\n\n# Checkout Timeout\n\nUpstream payment latency.",
            TestContext.Current.CancellationToken);
    }

    private sealed class RecordingEmbeddingClient : IEmbeddingClient
    {
        public List<EmbeddingRequest> Requests { get; } = [];

        public Task<EmbeddingResponse> CreateEmbeddingAsync(EmbeddingRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var vector = new float[8];
            vector[0] = 1f;
            return Task.FromResult(new EmbeddingResponse(vector, request.Model, "local-onnx", 1, request.CorrelationId));
        }
    }

    private sealed class InventoryMemoryRepository(MemoryCorpusInventory inventory) : IMemoryRepository
    {
        public int ReconcileCount { get; private set; }

        public MemorySeedCorpus? Reconciled { get; private set; }

        public Task<IReadOnlyList<MemorySearchMatch>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> SeedItemExistsAsync(string owner, MemorySeedItem item, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task ReconcileSeedCorpusAsync(MemorySeedCorpus corpus, CancellationToken cancellationToken)
        {
            ReconcileCount++;
            Reconciled = corpus;
            return Task.CompletedTask;
        }

        public Task<MemoryCorpusInventory> GetCorpusInventoryAsync(string tenantId, string owner, CancellationToken cancellationToken) =>
            Task.FromResult(inventory);
    }

    private sealed class StaticConfigurationRepository(TriageConfiguration configuration) : ITriageConfigurationRepository
    {
        public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult(configuration);

        public Task<TriageConfiguration> GetByHashAsync(string configHash, CancellationToken cancellationToken) =>
            Task.FromResult(configuration);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "IncidentCompass.UnitTests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
