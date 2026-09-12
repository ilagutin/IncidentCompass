namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// The metadata-only view of one owner's memory corpus: what route it is configured to use, what
/// route actually built it, how much of it there is, and whether those agree.
/// </summary>
/// <remarks>
/// Every field is a configured identifier, a count or a timestamp. No document text, no chunk text
/// and no vector is reachable from here, and neither is a provider endpoint or a credential: a
/// provider identifier is the key of an entry in the configured provider table and nothing more.
/// <para>
/// <c>State</c> is one of <c>NotBuilt</c>, <c>Current</c>, <c>EmbeddingRouteChanged</c>,
/// <c>MixedEmbeddingRoutes</c> or <c>Unrecorded</c>. <c>RebuildRequired</c> is true when retrieval
/// under the configured route cannot reach this corpus until a rebuild runs.
/// </para>
/// </remarks>
public sealed record MemoryCorpusSnapshot(
    string TenantId,
    string Owner,
    string State,
    bool RebuildRequired,
    string ConfiguredRouteId,
    string ConfiguredProviderId,
    string ConfiguredModel,
    Guid? ActiveGeneration,
    string? ActiveRouteId,
    string? ActiveProviderId,
    string? ActiveEmbeddingProvider,
    string? ActiveEmbeddingModel,
    int? ActiveEmbeddingDimensions,
    int ActiveItemCount,
    int ActiveChunkCount,
    int ActiveEmbeddingIdentityCount,
    DateTimeOffset? PublishedAtUtc);
