using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// The corpus status as the Worker sees it: the route is resolved through
/// <see cref="WorkerMemoryEmbeddingRouteResolver" />, so for a route served by the local model the
/// configured model reads as the installed model's encoded identity and a route the installed model
/// cannot serve reads as its blocked state. <c>AddEmbeddingHost</c> puts it in place of the Api's
/// <see cref="MemoryCorpusStatusReader" />; the snapshot it composes has the same shape.
/// </summary>
internal sealed class WorkerMemoryCorpusStatusReader(
    IOptions<MemorySeedOptions> options,
    ITriageConfigurationRepository configurationRepository,
    IMemoryRepository memoryRepository,
    WorkerMemoryEmbeddingRouteResolver routeResolver) : IMemoryCorpusStatusReader
{
    public async Task<MemoryCorpusSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var configuration = await configurationRepository.GetCurrentAsync(cancellationToken);
        var resolution = await routeResolver.ResolveAsync(configuration, cancellationToken);
        var inventory = await memoryRepository.GetCorpusInventoryAsync(
            settings.TenantId, settings.Owner, cancellationToken);
        return resolution.BlockedState is { } blocked
            ? MemoryCorpusStatusReader.Compose(settings, resolution.Route, inventory, blocked)
            : MemoryCorpusStatusReader.Compose(settings, resolution.Route, inventory);
    }
}
