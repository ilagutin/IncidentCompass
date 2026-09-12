using IncidentCompass.Application.Intake.Configuration;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// The single place that turns triage configuration into the embedding route the memory corpus is
/// built and judged under. The seed pass, the rebuild command and the status reader all resolve
/// through it, so none of them can disagree about which route the corpus belongs to.
/// </summary>
internal static class MemoryEmbeddingRouteResolver
{
    private const string MemorySearchToolName = "memory_search";

    public static MemoryEmbeddingRoute Resolve(TriageConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.Tools.TryGetValue(MemorySearchToolName, out var tool) ||
            string.IsNullOrWhiteSpace(tool.EmbeddingRouteId))
        {
            throw new InvalidOperationException("memory_search must declare an EmbeddingRouteId before memory seeding can run.");
        }

        if (!configuration.Routes.TryGetValue(tool.EmbeddingRouteId, out var route))
        {
            throw new InvalidOperationException(
                "Memory seed embedding route '" + tool.EmbeddingRouteId +
                "' is not configured; fix Tools.memory_search.EmbeddingRouteId.");
        }

        return new MemoryEmbeddingRoute(tool.EmbeddingRouteId, route.ProviderId, route.Model);
    }
}
