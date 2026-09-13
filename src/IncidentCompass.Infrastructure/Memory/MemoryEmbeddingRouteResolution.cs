using IncidentCompass.Application.Memory;

namespace IncidentCompass.Infrastructure.Memory;

/// <param name="Route">
/// The route the corpus is judged and built under. For a route served by the installed local model
/// its model is the encoded identity; when the route is blocked it is the route as configured.
/// </param>
/// <param name="BlockedState">
/// <see cref="MemoryCorpusState.EmbeddingModelMismatch" /> or
/// <see cref="MemoryCorpusState.EmbeddingModelUnavailable" /> when nothing may be embedded under the
/// route on this host; <see langword="null" /> otherwise.
/// </param>
/// <param name="ModelErrorCode">The local model's own code when the model is unavailable.</param>
/// <param name="Detail">An operator-facing sentence about why the route is blocked.</param>
internal sealed record MemoryEmbeddingRouteResolution(
    MemoryEmbeddingRoute Route,
    MemoryCorpusState? BlockedState,
    string? ModelErrorCode,
    string? Detail)
{
    public static MemoryEmbeddingRouteResolution Ready(MemoryEmbeddingRoute route) => new(route, null, null, null);

    public static MemoryEmbeddingRouteResolution Blocked(
        MemoryEmbeddingRoute route,
        MemoryCorpusState state,
        string? modelErrorCode,
        string detail) =>
        new(route, state, modelErrorCode, detail);
}
