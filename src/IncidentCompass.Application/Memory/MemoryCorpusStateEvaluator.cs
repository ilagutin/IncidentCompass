namespace IncidentCompass.Application.Memory;

/// <summary>
/// Decides what an owner's corpus is relative to the configured embedding route, from the corpus
/// rows alone and without asking a provider for anything.
/// </summary>
/// <remarks>
/// The check deliberately runs before any embedding call, because the point of it is to report a
/// route change rather than to discover one by spending a provider round trip per file. It can see
/// only what configuration declares: a provider identifier and a model name. The adapter name and
/// the vector width are known only once a provider has answered, so the publishing path checks
/// those against the recorded generation separately, before it writes anything.
/// </remarks>
internal static class MemoryCorpusStateEvaluator
{
    public static MemoryCorpusState Evaluate(
        string configuredProviderId,
        string configuredModel,
        MemoryCorpusInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.ActiveItemCount == 0 || inventory.ActiveIdentities.Count == 0)
        {
            return MemoryCorpusState.NotBuilt;
        }

        if (inventory.ActiveIdentities.Count > 1)
        {
            return MemoryCorpusState.MixedEmbeddingRoutes;
        }

        var active = inventory.ActiveIdentities[0];
        var recorded = ResolveRecordedIdentity(inventory, active);
        if (!string.Equals(configuredModel, active.EmbeddingModel, StringComparison.Ordinal))
        {
            return MemoryCorpusState.EmbeddingRouteChanged;
        }

        if (recorded?.ProviderId is { } providerId &&
            !string.Equals(configuredProviderId, providerId, StringComparison.Ordinal))
        {
            return MemoryCorpusState.EmbeddingRouteChanged;
        }

        return recorded is null ? MemoryCorpusState.Unrecorded : MemoryCorpusState.Current;
    }

    /// <summary>
    /// Returns the current generation's identity only when it describes the vector space the active
    /// chunks are actually in. A current row that names a different space is stale bookkeeping, and
    /// treating it as authority would let a recorded provider identifier vouch for rows it never
    /// produced.
    /// </summary>
    private static MemoryCorpusIdentity? ResolveRecordedIdentity(
        MemoryCorpusInventory inventory,
        MemoryCorpusIdentity active)
    {
        var recorded = inventory.Current?.Identity;
        if (recorded is null)
        {
            return null;
        }

        return string.Equals(recorded.EmbeddingProvider, active.EmbeddingProvider, StringComparison.Ordinal) &&
            string.Equals(recorded.EmbeddingModel, active.EmbeddingModel, StringComparison.Ordinal) &&
            recorded.EmbeddingDimensions == active.EmbeddingDimensions
            ? recorded
            : null;
    }
}
