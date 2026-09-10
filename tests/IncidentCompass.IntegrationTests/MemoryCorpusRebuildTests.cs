using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Covers what happens to a file-backed memory corpus when the embedding route under it changes.
/// Retrieval filters candidate chunks by the query embedding's provider, model and dimensions, so a
/// corpus in the wrong vector space returns nothing while remaining fully active: these tests hold
/// the line that such a corpus is reported, is never half-replaced, and is repaired by one command.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class MemoryCorpusRebuildTests(PostgresRepositoryFixture postgres)
{
    private const string OriginalModel = "embed-small";
    private const string SameWidthModel = "embed-small-v2";
    private const string WiderModel = "embed-large";

    private static readonly Dictionary<string, int> ModelWidths = new(StringComparer.Ordinal)
    {
        [OriginalModel] = 4,
        [SameWidthModel] = 4,
        [WiderModel] = 8
    };

    [DockerAvailableFact]
    public async Task ModelChangeAtSameWidth_BlocksStartupPublicationAndRebuildRepublishesEveryChunk()
    {
        var context = await SeedOriginalCorpusAsync();
        var restarted = new MemoryCorpusTestSupport.ModelSizedEmbeddingClient(ModelWidths);

        using (var changed = CreateHost(context, restarted, SameWidthModel))
        {
            await changed.StartAsync(TestContext.Current.CancellationToken);
            var blocked = await MemoryCorpusTestSupport.ReadCorpusAsync(
                context.ConnectionString, context.Owner, TestContext.Current.CancellationToken);

            Assert.Equal(0, restarted.CallCount);
            Assert.Equal(OriginalModel + ":4", blocked.Identities);
            Assert.Equal(context.OriginalGeneration, blocked.CurrentGeneration);
            Assert.Equal(2, blocked.ActiveChunks);
            Assert.Equal(
                MemoryCorpusState.EmbeddingRouteChanged.ToString(),
                (await ReadStatusAsync(changed)).State);

            // The persisted status is what the API host reads, so the route change has to reach it
            // as a code, alongside the generation that is still current and still retrievable under
            // the route that built it.
            var persisted = await ReadSyncStatusAsync(changed);
            Assert.Equal("memory_embedding_route_changed", persisted.LastErrorCode);
            Assert.Equal(context.OriginalGeneration, persisted.ActiveGeneration);
            await changed.StopAsync(TestContext.Current.CancellationToken);
        }

        await RebuildAsync(context, SameWidthModel);
        var rebuilt = await MemoryCorpusTestSupport.ReadCorpusAsync(
            context.ConnectionString, context.Owner, TestContext.Current.CancellationToken);

        Assert.Equal(SameWidthModel + ":4", rebuilt.Identities);
        Assert.Equal(2, rebuilt.ActiveItems);
        Assert.Equal(2, rebuilt.ActiveChunks);
        Assert.Equal(1, rebuilt.CurrentGenerationRows);
        Assert.Equal(1, rebuilt.DistinctItemGenerations);
        Assert.NotEqual(context.OriginalGeneration, rebuilt.CurrentGeneration);
    }

    [DockerAvailableFact]
    public async Task DimensionChange_BlocksStartupPublicationAndRebuildRepublishesAtTheNewWidth()
    {
        var context = await SeedOriginalCorpusAsync();
        var restarted = new MemoryCorpusTestSupport.ModelSizedEmbeddingClient(ModelWidths);

        using (var changed = CreateHost(context, restarted, WiderModel))
        {
            await changed.StartAsync(TestContext.Current.CancellationToken);
            var status = await ReadStatusAsync(changed);

            Assert.Equal(MemoryCorpusState.EmbeddingRouteChanged.ToString(), status.State);
            Assert.True(status.RebuildRequired);
            Assert.Equal(WiderModel, status.ConfiguredModel);
            Assert.Equal(OriginalModel, status.ActiveEmbeddingModel);
            Assert.Equal(4, status.ActiveEmbeddingDimensions);
            Assert.Equal(2, status.ActiveItemCount);
            await changed.StopAsync(TestContext.Current.CancellationToken);
        }

        await RebuildAsync(context, WiderModel);
        var rebuilt = await MemoryCorpusTestSupport.ReadCorpusAsync(
            context.ConnectionString, context.Owner, TestContext.Current.CancellationToken);

        Assert.Equal(WiderModel + ":8", rebuilt.Identities);
        Assert.Equal(1, rebuilt.CurrentGenerationRows);
        Assert.Equal(2, rebuilt.ActiveChunks);
    }

    [DockerAvailableFact]
    public async Task RebuildFailure_LeavesThePreviousCorpusCurrentAndUntouched()
    {
        var context = await SeedOriginalCorpusAsync();
        using var failing = MemoryCorpusTestSupport.CreateHost(
            context.ConnectionString,
            context.SourceDirectory,
            context.Owner,
            new MemoryCorpusTestSupport.FailingEmbeddingClient(successfulCalls: 1, dimensions: 8),
            WiderModel);

        var exitCode = await MemoryCorpusCommand.RunIfRequestedAsync(
            ["memory", "rebuild"], failing.Services, TestContext.Current.CancellationToken);
        var preserved = await MemoryCorpusTestSupport.ReadCorpusAsync(
            context.ConnectionString, context.Owner, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(OriginalModel + ":4", preserved.Identities);
        Assert.Equal(2, preserved.ActiveItems);
        Assert.Equal(2, preserved.ActiveChunks);
        Assert.Equal(1, preserved.CurrentGenerationRows);
        Assert.Equal(context.OriginalGeneration, preserved.CurrentGeneration);
    }

    [DockerAvailableFact]
    public async Task RebuildCancellation_LeavesThePreviousCorpusCurrentAndUntouched()
    {
        var context = await SeedOriginalCorpusAsync();
        using var cancellation = new CancellationTokenSource();
        using var cancelled = MemoryCorpusTestSupport.CreateHost(
            context.ConnectionString,
            context.SourceDirectory,
            context.Owner,
            new MemoryCorpusTestSupport.CancellingEmbeddingClient(1, 8, cancellation),
            WiderModel);

        var exitCode = await MemoryCorpusCommand.RunIfRequestedAsync(
            ["memory", "rebuild"], cancelled.Services, cancellation.Token);
        var preserved = await MemoryCorpusTestSupport.ReadCorpusAsync(
            context.ConnectionString, context.Owner, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(OriginalModel + ":4", preserved.Identities);
        Assert.Equal(2, preserved.ActiveChunks);
        Assert.Equal(context.OriginalGeneration, preserved.CurrentGeneration);
    }

    [DockerAvailableFact]
    public async Task RestartOnARebuiltCorpus_SynchronizesWithoutReembeddingAnything()
    {
        var context = await SeedOriginalCorpusAsync();
        await RebuildAsync(context, WiderModel);
        var afterRebuild = await MemoryCorpusTestSupport.ReadCorpusAsync(
            context.ConnectionString, context.Owner, TestContext.Current.CancellationToken);
        var restarted = new MemoryCorpusTestSupport.ModelSizedEmbeddingClient(ModelWidths);

        using var host = CreateHost(context, restarted, WiderModel);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var status = await ReadStatusAsync(host);
        var after = await MemoryCorpusTestSupport.ReadCorpusAsync(
            context.ConnectionString, context.Owner, TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, restarted.CallCount);
        Assert.Equal(MemoryCorpusState.Current.ToString(), status.State);
        Assert.False(status.RebuildRequired);
        Assert.Equal(WiderModel + ":8", after.Identities);
        Assert.Equal(1, after.CurrentGenerationRows);
        Assert.Equal(1, after.DistinctItemGenerations);
        Assert.NotEqual(afterRebuild.CurrentGeneration, after.CurrentGeneration);
    }

    [DockerAvailableFact]
    public async Task ConcurrentRebuildAndStartupSync_LeaveExactlyOneCurrentGeneration()
    {
        var context = await SeedOriginalCorpusAsync();
        using var rebuildHost = CreateHost(
            context, new MemoryCorpusTestSupport.ModelSizedEmbeddingClient(ModelWidths), OriginalModel);
        using var startupHost = CreateHost(
            context, new MemoryCorpusTestSupport.ModelSizedEmbeddingClient(ModelWidths), OriginalModel);

        await Task.WhenAll(
            MemoryCorpusCommand.RunIfRequestedAsync(
                ["memory", "rebuild"], rebuildHost.Services, TestContext.Current.CancellationToken),
            startupHost.StartAsync(TestContext.Current.CancellationToken));
        await startupHost.StopAsync(TestContext.Current.CancellationToken);
        var settled = await MemoryCorpusTestSupport.ReadCorpusAsync(
            context.ConnectionString, context.Owner, TestContext.Current.CancellationToken);

        Assert.Equal(1, settled.CurrentGenerationRows);
        Assert.Equal(1, settled.DistinctItemGenerations);
        Assert.Equal(OriginalModel + ":4", settled.Identities);
        Assert.Equal(2, settled.ActiveItems);
        Assert.Equal(2, settled.ActiveChunks);
    }

    [DockerAvailableFact]
    public async Task Rebuild_DoesNotDeactivateOrRepublishAnotherOwnersCorpus()
    {
        var context = await SeedOriginalCorpusAsync();
        var neighbourOwner = context.Owner + "-neighbour";
        using (var neighbour = MemoryCorpusTestSupport.CreateHost(
            context.ConnectionString,
            context.SourceDirectory,
            neighbourOwner,
            new MemoryCorpusTestSupport.ModelSizedEmbeddingClient(ModelWidths),
            OriginalModel))
        {
            await neighbour.StartAsync(TestContext.Current.CancellationToken);
            await neighbour.StopAsync(TestContext.Current.CancellationToken);
        }

        var before = await MemoryCorpusTestSupport.ReadCorpusAsync(
            context.ConnectionString, neighbourOwner, TestContext.Current.CancellationToken);
        await RebuildAsync(context, WiderModel);
        var after = await MemoryCorpusTestSupport.ReadCorpusAsync(
            context.ConnectionString, neighbourOwner, TestContext.Current.CancellationToken);

        Assert.Equal(2, before.ActiveItems);
        Assert.Equal(before.CurrentGeneration, after.CurrentGeneration);
        Assert.Equal(before.ActiveItems, after.ActiveItems);
        Assert.Equal(OriginalModel + ":4", after.Identities);
        Assert.Equal(1, after.CurrentGenerationRows);
    }

    private async Task<CorpusContext> SeedOriginalCorpusAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await MemoryCorpusTestSupport.ClearMemoryAsync(connectionString, TestContext.Current.CancellationToken);
        var sourceDirectory = await MemoryCorpusTestSupport.CreateSeedDirectoryAsync(
            TestContext.Current.CancellationToken);
        var owner = "corpus-" + Guid.NewGuid().ToString("N")[..8];
        var embeddingClient = new MemoryCorpusTestSupport.ModelSizedEmbeddingClient(ModelWidths);
        using (var host = MemoryCorpusTestSupport.CreateHost(
            connectionString, sourceDirectory, owner, embeddingClient, OriginalModel))
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        var seeded = await MemoryCorpusTestSupport.ReadCorpusAsync(
            connectionString, owner, TestContext.Current.CancellationToken);
        Assert.Equal(2, embeddingClient.CallCount);
        Assert.Equal(OriginalModel + ":4", seeded.Identities);
        Assert.Equal(1, seeded.CurrentGenerationRows);
        Assert.Equal("local-oai", seeded.CurrentProviderId);
        return new CorpusContext(connectionString, sourceDirectory, owner, seeded.CurrentGeneration);
    }

    private static IHost CreateHost(CorpusContext context, IEmbeddingClient embeddingClient, string model) =>
        MemoryCorpusTestSupport.CreateHost(
            context.ConnectionString, context.SourceDirectory, context.Owner, embeddingClient, model);

    private static async Task RebuildAsync(CorpusContext context, string model)
    {
        using var host = MemoryCorpusTestSupport.CreateHost(
            context.ConnectionString,
            context.SourceDirectory,
            context.Owner,
            new MemoryCorpusTestSupport.ModelSizedEmbeddingClient(ModelWidths),
            model);
        var exitCode = await MemoryCorpusCommand.RunIfRequestedAsync(
            ["memory", "rebuild"], host.Services, TestContext.Current.CancellationToken);
        Assert.Equal(0, exitCode);
    }

    private static async Task<MemoryCorpusSnapshot> ReadStatusAsync(IHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IMemoryCorpusStatusReader>()
            .GetAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<MemorySeedSyncSnapshot> ReadSyncStatusAsync(IHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IMemorySeedSyncStatusReader>()
            .GetAsync(TestContext.Current.CancellationToken);
    }

    private sealed record CorpusContext(
        string ConnectionString,
        string SourceDirectory,
        string Owner,
        Guid? OriginalGeneration);
}
