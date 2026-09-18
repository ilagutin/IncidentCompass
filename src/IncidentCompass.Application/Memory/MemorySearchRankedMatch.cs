namespace IncidentCompass.Application.Memory;

/// <summary>
/// One reranked match and how it was admitted: the retrieval-confidence band it is reported under,
/// whether the vector-only fallback returned it, and the relevance judge's score when a judge judged
/// this call. <see cref="JudgeScore" /> is null exactly when the call was not judged, and is reported
/// beside the vector score rather than replacing it.
/// </summary>
internal sealed record MemorySearchRankedMatch(
    MemorySearchMatch Match,
    string RetrievalConfidence,
    bool VectorOnly,
    double? JudgeScore = null);
