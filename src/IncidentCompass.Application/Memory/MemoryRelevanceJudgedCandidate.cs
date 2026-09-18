namespace IncidentCompass.Application.Memory;

/// <summary>
/// One candidate the relevance judge admitted, with the score it gave for the role's query. A
/// candidate below the floor is not represented here at all: it was dropped, not admitted.
/// </summary>
/// <remarks>
/// There is deliberately no "confirmed" flag. Whether an admitted document describes the fault is
/// decided against the fault query by <see cref="MemoryFaultConfirmation" />, never against the
/// role's query this score answers.
/// </remarks>
internal sealed record MemoryRelevanceJudgedCandidate(
    MemorySearchMatch Match,
    double Score);
