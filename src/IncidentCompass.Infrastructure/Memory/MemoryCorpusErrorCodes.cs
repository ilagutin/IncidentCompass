using IncidentCompass.Application.Memory;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// The stable status codes a corpus that needs an operator rebuild is reported under.
/// </summary>
internal static class MemoryCorpusErrorCodes
{
    public const string EmbeddingRouteChanged = "memory_embedding_route_changed";

    public const string EmbeddingRoutesMixed = "memory_embedding_routes_mixed";

    public static string From(MemoryCorpusState state) => state switch
    {
        MemoryCorpusState.MixedEmbeddingRoutes => EmbeddingRoutesMixed,
        MemoryCorpusState.EmbeddingRouteChanged => EmbeddingRouteChanged,
        _ => throw new ArgumentOutOfRangeException(
            nameof(state), state, "Only a corpus that needs a rebuild has an error code."),
    };
}
