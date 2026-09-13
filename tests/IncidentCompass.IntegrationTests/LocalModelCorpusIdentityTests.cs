using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// A memory corpus built by the in-process local model is identified by the installed model's encoded
/// identity, <c>&lt;model id&gt;@sha256:&lt;16 hex&gt;</c>. These tests run the real synchronizer, seed
/// hosted service, corpus command, status readers and PostgreSQL corpus; only the install pass and the
/// embedding adapter are substituted. The install state is set directly to a manifest with a chosen id
/// and model file digest, because what is under test is how the corpus is judged against the installed
/// model, not how a model is fetched or run, which the store and adapter tests cover.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class LocalModelCorpusIdentityTests(PostgresRepositoryFixture postgres)
{
    private const string ModelId = "intfloat/multilingual-e5-small";
    private const string LocalProviderId = "local-embed";

    private static readonly string FirstDigest = new('a', 64);
    private static readonly string SecondDigest = new('b', 64);

    [DockerAvailableFact]
    public async Task ChangedModelFile_IsARouteChangeThatKeepsThePreviousCorpusCurrentUntilRebuild()
    {
        var context = await SeedAsync(FirstDigest);
        var restarted = EmbeddingClient();

        using (var host = CreateHost(context, restarted, Installed(ModelId, SecondDigest)))
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            var persisted = await ReadSyncStatusAsync(host);
            var workerStatus = await ReadWorkerStatusAsync(host);
            var apiStatus = await ReadApiStatusAsync(host);
            var corpus = await ReadCorpusAsync(context);
            await host.StopAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, restarted.CallCount);
            Assert.Equal(MemoryCorpusErrorCodes.EmbeddingRouteChanged, persisted.LastErrorCode);
            Assert.Equal(context.Generation, corpus.CurrentGeneration);
            Assert.Equal(Encoded(FirstDigest) + ":4", corpus.Identities);
            Assert.Equal(nameof(MemoryCorpusState.EmbeddingRouteChanged), workerStatus.State);
            Assert.Equal(Encoded(SecondDigest), workerStatus.ConfiguredModel);
            Assert.Equal(nameof(MemoryCorpusState.EmbeddingRouteChanged), apiStatus.State);
            Assert.True(apiStatus.RebuildRequired);
        }

        using (var rebuildHost = CreateHost(context, EmbeddingClient(), Installed(ModelId, SecondDigest)))
        {
            var exitCode = await MemoryCorpusCommand.RunIfRequestedAsync(
                ["memory", "rebuild"], rebuildHost.Services, TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);
        }

        var rebuilt = await ReadCorpusAsync(context);
        Assert.Equal(Encoded(SecondDigest) + ":4", rebuilt.Identities);
        Assert.Equal(1, rebuilt.CurrentGenerationRows);
        Assert.NotEqual(context.Generation, rebuilt.CurrentGeneration);
    }

    [DockerAvailableFact]
    public async Task InstalledModelWithAnotherId_IsANonFatalModelMismatch()
    {
        var context = await SeedAsync(FirstDigest);
        var restarted = EmbeddingClient();
        using var host = CreateHost(context, restarted, Installed("intfloat/multilingual-e5-base", FirstDigest));

        await host.StartAsync(TestContext.Current.CancellationToken);
        var persisted = await ReadSyncStatusAsync(host);
        var workerStatus = await ReadWorkerStatusAsync(host);
        var apiStatus = await ReadApiStatusAsync(host);
        var statusExitCode = await MemoryCorpusCommand.RunIfRequestedAsync(
            ["memory", "status"], host.Services, TestContext.Current.CancellationToken);
        var corpus = await ReadCorpusAsync(context);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, restarted.CallCount);
        Assert.Equal(MemoryCorpusErrorCodes.EmbeddingModelMismatch, persisted.LastErrorCode);
        Assert.Equal(context.Generation, persisted.ActiveGeneration);
        Assert.Equal(context.Generation, corpus.CurrentGeneration);
        Assert.Equal(nameof(MemoryCorpusState.EmbeddingModelMismatch), workerStatus.State);
        Assert.False(workerStatus.RebuildRequired);
        Assert.Equal(nameof(MemoryCorpusState.EmbeddingModelMismatch), apiStatus.State);
        Assert.Equal(1, statusExitCode);
    }

    [DockerAvailableFact]
    public async Task UnavailableModel_BlocksSeedingWithoutFailingTheHostAndRefusesARebuild()
    {
        var context = await SeedAsync(FirstDigest);
        var restarted = EmbeddingClient();
        var failed = new LocalOnnxModelInstallState();
        failed.RecordFailed(LocalOnnxModelErrorCodes.FetchFailed, "The onnx file could not be downloaded in this test.");
        using var host = CreateHost(context, restarted, failed);

        await host.StartAsync(TestContext.Current.CancellationToken);
        var persisted = await ReadSyncStatusAsync(host);
        var apiStatus = await ReadApiStatusAsync(host);
        var rebuildExitCode = await MemoryCorpusCommand.RunIfRequestedAsync(
            ["memory", "rebuild"], host.Services, TestContext.Current.CancellationToken);
        var corpus = await ReadCorpusAsync(context);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, restarted.CallCount);
        Assert.Equal(MemoryCorpusErrorCodes.EmbeddingModelUnavailable, persisted.LastErrorCode);
        Assert.Equal(nameof(MemoryCorpusState.EmbeddingModelUnavailable), apiStatus.State);
        Assert.Equal(1, rebuildExitCode);
        Assert.Equal(context.Generation, corpus.CurrentGeneration);
        Assert.Equal(Encoded(FirstDigest) + ":4", corpus.Identities);
    }

    private async Task<CorpusContext> SeedAsync(string modelDigest)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await MemoryCorpusTestSupport.ClearMemoryAsync(connectionString, TestContext.Current.CancellationToken);
        var sourceDirectory = await MemoryCorpusTestSupport.CreateSeedDirectoryAsync(TestContext.Current.CancellationToken);
        var modelDirectory = Path.Combine(Path.GetTempPath(), "incidentcompass-local-model-corpus-" + Guid.NewGuid().ToString("N"));
        var context = new CorpusContext(
            connectionString,
            sourceDirectory,
            modelDirectory,
            "local-model-" + Guid.NewGuid().ToString("N")[..8],
            null);
        var embeddingClient = EmbeddingClient();
        using (var host = CreateHost(context, embeddingClient, Installed(ModelId, modelDigest)))
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        var seeded = await ReadCorpusAsync(context);
        Assert.Equal(2, embeddingClient.CallCount);
        Assert.Equal(Encoded(modelDigest) + ":4", seeded.Identities);
        Assert.Equal(LocalProviderId, seeded.CurrentProviderId);
        return context with { Generation = seeded.CurrentGeneration };
    }

    private static IHost CreateHost(
        CorpusContext context,
        IEmbeddingClient embeddingClient,
        LocalOnnxModelInstallState installState) =>
        MemoryCorpusTestSupport.CreateHost(
            context.ConnectionString,
            context.SourceDirectory,
            context.Owner,
            embeddingClient,
            ModelId,
            LocalProviderId,
            providerKind: "LocalOnnx",
            settings: new Dictionary<string, string?>
            {
                ["IncidentCompass:Embeddings:Provider"] = "LocalOnnx",
                ["IncidentCompass:Embeddings:LocalOnnx:ModelDirectory"] = context.ModelDirectory
            },
            configureServices: services =>
            {
                services.RemoveAll<LocalOnnxModelInstallState>();
                services.AddSingleton(installState);
                foreach (var install in services
                             .Where(static descriptor => descriptor.ImplementationType == typeof(LocalOnnxModelInstallHostedService))
                             .ToArray())
                {
                    services.Remove(install);
                }
            });

    private static MemoryCorpusTestSupport.ModelSizedEmbeddingClient EmbeddingClient() =>
        new(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [Encoded(FirstDigest)] = 4,
            [Encoded(SecondDigest)] = 4
        });

    private static LocalOnnxModelInstallState Installed(string modelId, string modelDigest)
    {
        var options = new LocalOnnxEmbeddingOptions
        {
            ModelDirectory = Path.GetTempPath(),
            ModelId = modelId,
            ModelFileSha256 = modelDigest
        };
        var state = new LocalOnnxModelInstallState();
        state.RecordInstalled(new LocalOnnxInstalledModel(LocalOnnxModelStore.CreateManifest(options), "unused.onnx", "unused.model"));
        return state;
    }

    private static string Encoded(string modelDigest) => EncodedEmbeddingModelIdentity.Encode(ModelId, modelDigest);

    private static Task<MemoryCorpusTestSupport.MemoryCorpusRow> ReadCorpusAsync(CorpusContext context) =>
        MemoryCorpusTestSupport.ReadCorpusAsync(context.ConnectionString, context.Owner, TestContext.Current.CancellationToken);

    private static async Task<MemorySeedSyncSnapshot> ReadSyncStatusAsync(IHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMemorySeedSyncStatusReader>()
            .GetAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<MemoryCorpusSnapshot> ReadWorkerStatusAsync(IHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMemoryCorpusStatusReader>()
            .GetAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The reader the Api composes, over the same database and configuration.</summary>
    private static async Task<MemoryCorpusSnapshot> ReadApiStatusAsync(IHost host)
    {
        using var scope = host.Services.CreateScope();
        return await ActivatorUtilities.CreateInstance<MemoryCorpusStatusReader>(scope.ServiceProvider)
            .GetAsync(TestContext.Current.CancellationToken);
    }

    private sealed record CorpusContext(
        string ConnectionString,
        string SourceDirectory,
        string ModelDirectory,
        string Owner,
        Guid? Generation);
}
