using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// Composes the corpus status from the configured embedding route and the corpus rows, so the API
/// host and the operator command answer the question the same way.
/// </summary>
internal sealed class MemoryCorpusStatusReader(
    IOptions<MemorySeedOptions> options,
    ITriageConfigurationRepository configurationRepository,
    IMemoryRepository memoryRepository) : IMemoryCorpusStatusReader
{
    public async Task<MemoryCorpusSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var configuration = await configurationRepository.GetCurrentAsync(cancellationToken);
        var route = MemoryEmbeddingRouteResolver.Resolve(configuration);
        var inventory = await memoryRepository.GetCorpusInventoryAsync(
            settings.TenantId, settings.Owner, cancellationToken);
        return Compose(settings, route, inventory);
    }

    internal static MemoryCorpusSnapshot Compose(
        MemorySeedOptions settings,
        MemoryEmbeddingRoute route,
        MemoryCorpusInventory inventory)
    {
        var state = MemoryCorpusStateEvaluator.Evaluate(route.ProviderId, route.Model, inventory);
        var active = inventory.ActiveIdentities.Count == 1 ? inventory.ActiveIdentities[0] : null;
        var recorded = inventory.Current;
        return new MemoryCorpusSnapshot(
            settings.TenantId,
            settings.Owner,
            state.ToString(),
            state is MemoryCorpusState.EmbeddingRouteChanged or MemoryCorpusState.MixedEmbeddingRoutes,
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
}
