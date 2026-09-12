using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// One pass over the reviewed seed files, shared by startup synchronization, runtime resync and
/// the operator rebuild command.
/// </summary>
/// <remarks>
/// <para>
/// The pass checks the corpus against the configured embedding route before it asks a provider for
/// anything. An incremental pass that finds a route change publishes nothing at all: re-embedding
/// a whole corpus is an operator's decision, not something a restart should do on its own, and the
/// alternative the code used to take was worse than either. It embedded only the files whose text
/// had changed, which left the corpus split between two vector spaces, and because retrieval
/// filters candidates by the query embedding's provider, model and dimensions, the rest of the
/// corpus stopped matching anything while remaining fully active and apparently healthy.
/// </para>
/// <para>
/// A second check runs after the embeddings come back, because configuration cannot see everything
/// that decides a vector space: the adapter that answers and the width of the vector it returns are
/// known only from a real response. A pass whose vectors disagree with the corpus it is adding to
/// stops before the reconciliation transaction, so nothing partial reaches the database.
/// </para>
/// </remarks>
internal sealed class MemorySeedSynchronizer(
    IOptions<MemorySeedOptions> options,
    IHostEnvironment environment,
    ITriageConfigurationRepository configurationRepository,
    IEmbeddingClient embeddingClient,
    IMemoryRepository memoryRepository)
{
    public async Task<MemorySeedSyncOutcome> SynchronizeAsync(
        MemorySeedSyncMode mode,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var configuration = await configurationRepository.GetCurrentAsync(cancellationToken);
        var route = MemoryEmbeddingRouteResolver.Resolve(configuration);
        var inventory = await memoryRepository.GetCorpusInventoryAsync(
            settings.TenantId, settings.Owner, cancellationToken);
        var state = MemoryCorpusStateEvaluator.Evaluate(route.ProviderId, route.Model, inventory);
        if (mode == MemorySeedSyncMode.Incremental && RequiresRebuild(state))
        {
            return Blocked(state, inventory, route);
        }

        var scan = MemorySeedFileLoader.LoadScan(ResolveRootDirectory(settings));
        if (scan.Files.Count == 0)
        {
            // Refused here rather than in the reconciliation transaction so that no provider call
            // is spent on a scan that could only ever deactivate the corpus it was meant to refresh.
            throw new InvalidOperationException("Memory seed scan did not find any files.");
        }

        var builder = new MemorySeedEntryBuilder(embeddingClient, memoryRepository);
        var entries = new List<MemorySeedEntry>(scan.Files.Count);
        foreach (var file in scan.Files)
        {
            entries.Add(await builder.BuildAsync(
                file, settings, route, mode == MemorySeedSyncMode.Rebuild, cancellationToken));
        }

        var identity = ResolveIdentity(mode, route, entries, inventory);
        if (identity is null)
        {
            return Blocked(MemoryCorpusState.EmbeddingRouteChanged, inventory, route);
        }

        var corpus = new MemorySeedCorpus(
            settings.TenantId, settings.Owner, Guid.NewGuid(), identity, scan.PresentDirectories, entries);
        await memoryRepository.ReconcileSeedCorpusAsync(corpus, cancellationToken);
        return new MemorySeedSyncOutcome(
            Published: true, MemoryCorpusState.Current, corpus.Generation, route, entries.Count);
    }

    private static bool RequiresRebuild(MemoryCorpusState state) =>
        state is MemoryCorpusState.EmbeddingRouteChanged or MemoryCorpusState.MixedEmbeddingRoutes;

    private static MemorySeedSyncOutcome Blocked(
        MemoryCorpusState state,
        MemoryCorpusInventory inventory,
        MemoryEmbeddingRoute route) =>
        new(Published: false, state, inventory.Current?.Generation, route, inventory.ActiveItemCount);

    /// <summary>
    /// Picks the one identity this pass is allowed to publish, or null when the vectors it just
    /// produced belong to a different space than the corpus it would join.
    /// </summary>
    /// <remarks>
    /// A pass that produced no vectors at all republishes the identity the corpus already has: it
    /// changed no content and touched no chunk, so claiming a different vector space for it would
    /// be a record of something that did not happen.
    /// </remarks>
    private static MemoryCorpusIdentity? ResolveIdentity(
        MemorySeedSyncMode mode,
        MemoryEmbeddingRoute route,
        IReadOnlyList<MemorySeedEntry> entries,
        MemoryCorpusInventory inventory)
    {
        var produced = entries
            .SelectMany(static entry => entry.Chunks)
            .Select(chunk => new MemoryCorpusIdentity(
                route.RouteId, route.ProviderId, chunk.EmbeddingProvider, chunk.EmbeddingModel, chunk.EmbeddingDimensions))
            .Distinct()
            .ToArray();
        if (produced.Length > 1)
        {
            // Not a route change: one route answered one pass with more than one vector width. The
            // corpus cannot be published under a single identity, and picking one would bury it.
            throw new InvalidOperationException(
                "Embedding route '" + route.RouteId + "' returned more than one vector shape in a" +
                " single memory seed pass, so the corpus has no single embedding identity to publish.");
        }

        if (produced.Length == 0)
        {
            // Nothing was embedded, so nothing observed a provider. The identity is republished as
            // it stands, gaining only the route name: claiming the configured provider for chunks
            // this pass never produced would record an attribution nobody checked.
            var existing = inventory.ActiveIdentities.Count == 1 ? inventory.ActiveIdentities[0] : null;
            return existing is null ? null : existing with { RouteId = route.RouteId };
        }

        if (mode == MemorySeedSyncMode.Incremental &&
            inventory.ActiveIdentities.Count > 0 &&
            !inventory.ActiveIdentities.All(existing => existing.Matches(produced[0])))
        {
            return null;
        }

        return produced[0];
    }

    private string ResolveRootDirectory(MemorySeedOptions settings)
    {
        var sourceDirectory = settings.SourceDirectory;
        return Path.IsPathRooted(sourceDirectory)
            ? sourceDirectory
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, sourceDirectory));
    }
}
