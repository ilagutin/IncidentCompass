using IncidentCompass.Application.Memory;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// The stable status codes a synchronization pass that published nothing is reported under: a corpus
/// that needs an operator rebuild, or a route the installed local embedding model cannot serve.
/// </summary>
internal static class MemoryCorpusErrorCodes
{
    public const string EmbeddingRouteChanged = "memory_embedding_route_changed";

    public const string EmbeddingRoutesMixed = "memory_embedding_routes_mixed";

    public const string EmbeddingModelMismatch = "memory_embedding_model_mismatch";

    public const string EmbeddingModelUnavailable = "memory_embedding_model_unavailable";

    public static string From(MemoryCorpusState state) => state switch
    {
        MemoryCorpusState.MixedEmbeddingRoutes => EmbeddingRoutesMixed,
        MemoryCorpusState.EmbeddingRouteChanged => EmbeddingRouteChanged,
        MemoryCorpusState.EmbeddingModelMismatch => EmbeddingModelMismatch,
        MemoryCorpusState.EmbeddingModelUnavailable => EmbeddingModelUnavailable,
        _ => throw new ArgumentOutOfRangeException(
            nameof(state), state, "Only a corpus a pass declined to publish under has an error code."),
    };
}
