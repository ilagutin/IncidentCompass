namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// The embedding route <c>memory_search</c> is configured to use, reduced to the three bounded
/// identifiers a corpus is judged against. No endpoint and no credential is carried here.
/// </summary>
public sealed record MemoryEmbeddingRoute(string RouteId, string ProviderId, string Model);
