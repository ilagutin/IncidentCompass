namespace IncidentCompass.Application.Memory;

/// <summary>
/// One reranked match and how it was admitted: the retrieval-confidence band it is reported under, and
/// whether the lexical gate kept it or the vector-only fallback returned it.
/// </summary>
internal sealed record MemorySearchRankedMatch(
    MemorySearchMatch Match,
    string RetrievalConfidence,
    bool VectorOnly);
