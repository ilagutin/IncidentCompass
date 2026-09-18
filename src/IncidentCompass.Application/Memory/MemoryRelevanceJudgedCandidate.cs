namespace IncidentCompass.Application.Memory;

/// <summary>
/// One candidate the relevance judge admitted, with the score it gave and whether that score reached
/// the confirm threshold. A candidate below the floor is not represented here at all: it was dropped,
/// not admitted unconfirmed.
/// </summary>
internal sealed record MemoryRelevanceJudgedCandidate(
    MemorySearchMatch Match,
    double Score,
    bool Confirmed);
