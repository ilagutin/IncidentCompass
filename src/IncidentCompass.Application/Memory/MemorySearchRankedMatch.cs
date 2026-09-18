namespace IncidentCompass.Application.Memory;

/// <summary>
/// One reranked match and how it was admitted and confirmed: the retrieval-confidence band it is
/// reported under, whether the vector-only fallback returned it, the relevance judge's score for the
/// role's query when a judge judged this call, and that judge's score for the fault query when it
/// confirmed this call. Both scores are null exactly when no judge scored them, and both are reported
/// beside the vector score rather than replacing it.
/// </summary>
/// <remarks>
/// The reranker builds every match <c>low</c>: it admits and orders, and never confirms.
/// <see cref="MemoryFaultConfirmation" /> is the only step that raises a band.
/// </remarks>
internal sealed record MemorySearchRankedMatch(
    MemorySearchMatch Match,
    string RetrievalConfidence,
    bool VectorOnly,
    double? JudgeScore = null,
    double? ConfirmationScore = null);
