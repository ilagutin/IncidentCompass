namespace IncidentCompass.Application.Memory;

/// <summary>
/// What an owner's memory corpus is, relative to the embedding route currently configured for
/// <c>memory_search</c>.
/// </summary>
internal enum MemoryCorpusState
{
    /// <summary>No active seed-managed items exist for this owner.</summary>
    NotBuilt = 0,

    /// <summary>
    /// One vector space, one current generation, and it is the configured route's.
    /// </summary>
    Current = 1,

    /// <summary>
    /// The corpus is present and coherent but was built under a different embedding route.
    /// Retrieval under the configured route finds nothing until a rebuild runs; the corpus itself
    /// is intact and is still retrievable by restoring the previous route.
    /// </summary>
    EmbeddingRouteChanged = 2,

    /// <summary>
    /// The owner's active chunks hold more than one vector space at once. The publishing path can
    /// no longer produce this; a corpus seeded before generations were published transactionally
    /// can be in it, because that path re-embedded only the files whose content had changed.
    /// </summary>
    MixedEmbeddingRoutes = 3,

    /// <summary>
    /// The corpus matches the configured model but no current generation names it, so the provider
    /// half of its identity was never recorded. A rebuild records it.
    /// </summary>
    Unrecorded = 4
}
