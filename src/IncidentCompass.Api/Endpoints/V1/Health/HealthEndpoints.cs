using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Core.Health;
using IncidentCompass.Infrastructure.Memory;

namespace IncidentCompass.Api;

internal static class HealthEndpoints
{
    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/health", async (
                IApplicationDispatcher dispatcher,
                CancellationToken cancellationToken) =>
            {
                var result = await dispatcher.DispatchAsync<GetHealthStatusQuery, HealthStatus>(
                    new GetHealthStatusQuery("api"),
                    cancellationToken);

                return Results.Ok(result);
            })
            .WithName("GetApiV1Health")
            .WithSummary("Liveness probe for the API host.")
            .AllowAnonymous()
            .DisableRateLimiting()
            .Produces<HealthStatus>(StatusCodes.Status200OK);

        api.MapGet("/health/memory-sync", async (
                IMemorySeedSyncStatusReader syncStatus,
                CancellationToken cancellationToken) =>
                Results.Ok(await syncStatus.GetAsync(cancellationToken)))
            .WithName("GetMemorySeedSyncStatus")
            .WithSummary("Metadata-only Worker memory synchronization status persisted in PostgreSQL.")
            .AllowAnonymous()
            .DisableRateLimiting()
            .Produces<MemorySeedSyncSnapshot>(StatusCodes.Status200OK);

        api.MapGet("/health/memory-corpus", async (
                IMemoryCorpusStatusReader corpusStatus,
                CancellationToken cancellationToken) =>
                Results.Ok(await corpusStatus.GetAsync(cancellationToken)))
            .WithName("GetMemoryCorpusStatus")
            .WithSummary("Metadata-only memory corpus status: configured embedding route, the route that built the active corpus, and counts.")
            .AllowAnonymous()
            .DisableRateLimiting()
            .Produces<MemoryCorpusSnapshot>(StatusCodes.Status200OK);

        return api;
    }
}
