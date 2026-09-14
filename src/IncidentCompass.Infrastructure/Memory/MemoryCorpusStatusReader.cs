using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// Composes the corpus status from the configured embedding route and the corpus rows. This is the
/// reader the Api composes; the Worker replaces it with <see cref="WorkerMemoryCorpusStatusReader" />,
/// which knows the installed local model.
/// <para>
/// A route served by the in-process local model is recorded in the corpus under an encoded model name
/// that carries the model file's digest, and only the Worker, which has the model volume, knows that
/// digest. For such a route this reader compares the configured model with only the id part of the
/// corpus model, then refines the result with the code the Worker last persisted: a model mismatch
/// or an unavailable model is reported as such, and a route change the Worker detected from the
/// digest is reported even when the id parts agree.
/// </para>
/// </summary>
internal sealed class MemoryCorpusStatusReader(
    IOptions<MemorySeedOptions> options,
    ITriageConfigurationRepository configurationRepository,
    IMemoryRepository memoryRepository,
    IMemorySeedSyncStatusReader syncStatusReader) : IMemoryCorpusStatusReader
{
    public async Task<MemoryCorpusSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var configuration = await configurationRepository.GetCurrentAsync(cancellationToken);
        var route = MemoryEmbeddingRouteResolver.Resolve(configuration);
        var inventory = await memoryRepository.GetCorpusInventoryAsync(
            settings.TenantId, settings.Owner, cancellationToken);
        var workerStatus = await syncStatusReader.GetAsync(cancellationToken);
        // Chunking belongs to the Worker. The Api has no counter and may have different host
        // defaults, so it reads the Worker's decision for the current generation. A completed
        // rebuild supersedes a blocked status even before the next background synchronization.
        var errorCode = workerStatus.LastErrorCode;
        if (errorCode == MemoryCorpusErrorCodes.ChunkPolicyChanged &&
            workerStatus.ActiveGeneration != inventory.Current?.Generation)
        {
            errorCode = null;
        }

        if (!MemoryEmbeddingRouteResolver.IsLocalOnnxRoute(configuration, route))
        {
            var state = MemoryCorpusStateEvaluator.Evaluate(route.ProviderId, route.Model, inventory);
            return Compose(settings, route, inventory,
                errorCode == MemoryCorpusErrorCodes.ChunkPolicyChanged && state == MemoryCorpusState.Current
                    ? MemoryCorpusState.ChunkPolicyChanged
                    : state);
        }

        return ComposeForLocalModelRoute(settings, route, inventory, errorCode);
    }

    internal static MemoryCorpusSnapshot Compose(
        MemorySeedOptions settings,
        MemoryEmbeddingRoute route,
        MemoryCorpusInventory inventory,
        string counterKind = "estimate") =>
        Compose(settings, route, inventory, MemoryCorpusStateEvaluator.Evaluate(
            route.ProviderId, route.Model, inventory, settings.Chunking.Describe(counterKind)));

    internal static MemoryCorpusSnapshot ComposeForLocalModelRoute(
        MemorySeedOptions settings,
        MemoryEmbeddingRoute route,
        MemoryCorpusInventory inventory,
        string? workerErrorCode)
    {
        var idPartState = MemoryCorpusStateEvaluator.Evaluate(
            route.ProviderId, route.Model, WithModelIdParts(inventory));
        return Compose(settings, route, inventory, FoldWorkerErrorCode(idPartState, workerErrorCode));
    }

    /// <summary>
    /// <c>RebuildRequired</c> is true only for the two route states a rebuild repairs. It stays false
    /// for a model mismatch and an unavailable model: a rebuild cannot embed under a route the
    /// installed model cannot serve, and the synchronizer refuses one. Once the operator installs the
    /// configured model or corrects the route, the state becomes a route change, which does.
    /// </summary>
    internal static MemoryCorpusSnapshot Compose(
        MemorySeedOptions settings,
        MemoryEmbeddingRoute route,
        MemoryCorpusInventory inventory,
        MemoryCorpusState state)
    {
        var active = inventory.ActiveIdentities.Count == 1 ? inventory.ActiveIdentities[0] : null;
        var recorded = inventory.Current;
        return new MemoryCorpusSnapshot(
            settings.TenantId,
            settings.Owner,
            state.ToString(),
            state is MemoryCorpusState.EmbeddingRouteChanged or MemoryCorpusState.MixedEmbeddingRoutes or MemoryCorpusState.ChunkPolicyChanged,
            route.RouteId,
            route.ProviderId,
            route.Model,
            recorded?.Generation,
            recorded?.Identity.RouteId,
            recorded?.Identity.ProviderId,
            active?.EmbeddingProvider ?? recorded?.Identity.EmbeddingProvider,
            active?.EmbeddingModel ?? recorded?.Identity.EmbeddingModel,
            active?.EmbeddingDimensions ?? recorded?.Identity.EmbeddingDimensions,
            inventory.ActiveItemCount,
            inventory.ActiveChunkCount,
            inventory.ActiveIdentities.Count,
            recorded?.PublishedAtUtc);
    }

    private static MemoryCorpusState FoldWorkerErrorCode(MemoryCorpusState idPartState, string? workerErrorCode) =>
        workerErrorCode switch
        {
            MemoryCorpusErrorCodes.EmbeddingModelMismatch => MemoryCorpusState.EmbeddingModelMismatch,
            MemoryCorpusErrorCodes.EmbeddingModelUnavailable => MemoryCorpusState.EmbeddingModelUnavailable,
            MemoryCorpusErrorCodes.EmbeddingRouteChanged when idPartState == MemoryCorpusState.Current =>
                MemoryCorpusState.EmbeddingRouteChanged,
            MemoryCorpusErrorCodes.ChunkPolicyChanged when idPartState == MemoryCorpusState.Current =>
                MemoryCorpusState.ChunkPolicyChanged,
            _ => idPartState
        };

    /// <summary>
    /// The same inventory with every recorded model reduced to its id part. Identities are not merged:
    /// two encoded identities that share an id are still two vector spaces.
    /// </summary>
    private static MemoryCorpusInventory WithModelIdParts(MemoryCorpusInventory inventory) =>
        inventory with
        {
            Current = inventory.Current is null
                ? null
                : inventory.Current with { Identity = WithModelIdPart(inventory.Current.Identity) },
            ActiveIdentities = inventory.ActiveIdentities.Select(WithModelIdPart).ToArray()
        };

    private static MemoryCorpusIdentity WithModelIdPart(MemoryCorpusIdentity identity) =>
        identity with { EmbeddingModel = EncodedEmbeddingModelIdentity.ModelIdOf(identity.EmbeddingModel) };
}
