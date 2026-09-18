namespace IncidentCompass.Application.Memory;

/// <summary>
/// The resolved relevance-judge settings of one <c>memory_search</c> call: whether to ask, the score
/// at or above which a candidate is confirmed, and the score below which it is dropped outright.
/// </summary>
/// <remarks>
/// The two scores are on the judge model's own scale, which the port deliberately does not define.
/// They are therefore configuration, not constants derived from a vector similarity: a different
/// cross-encoder needs different numbers, and the shipped defaults are the ones measured for the
/// pinned judge on the retrieval benchmark corpus.
/// </remarks>
internal sealed record MemoryRelevanceJudgeSettings(
    MemoryRelevanceJudgeMode Mode,
    double ConfirmScore,
    double FloorScore);
