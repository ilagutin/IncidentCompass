using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Infrastructure.Configuration;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// The single place that turns triage configuration into the embedding route the memory corpus is
/// built and judged under, as configured. The Api's status reader uses it as is. The Worker, which
/// has the local embedding model, refines it through <see cref="WorkerMemoryEmbeddingRouteResolver" />
/// so a route served by that model is judged under the installed model's encoded identity.
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

    /// <summary>
    /// Whether the route's provider entry is the in-process local model, the only kind of route whose
    /// corpus is identified by an encoded model name.
    /// </summary>
    public static bool IsLocalOnnxRoute(TriageConfiguration configuration, MemoryEmbeddingRoute route)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(route);
        return configuration.Providers.TryGetValue(route.ProviderId, out var provider) &&
            string.Equals(provider.Kind, ProviderKindHostDefaultRule.LocalOnnxKind, StringComparison.Ordinal);
    }
}
