namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The two fault scores a confirm score has to fall between. <c>StrongestNegativeFaultScore</c> is the
/// highest fault score any off-topic or hard-negative query gave any candidate it was offered, so a
/// confirm score at or below it can confirm a document that describes no fault the corpus answers.
/// <c>WeakestRelevantFaultScore</c> is the lowest fault score of any labelled relevant chunk its own
/// positive query was offered, so a confirm score above it leaves that answer unconfirmed.
/// </summary>
public sealed record MemorySearchRelevanceJudgeBenchmarkConfirmBoundary(
    string Language,
    double? StrongestNegativeFaultScore,
    string? StrongestNegativeQueryId,
    string? StrongestNegativeChunkId,
    int NegativePairCount,
    double? WeakestRelevantFaultScore,
    string? WeakestRelevantQueryId,
    string? WeakestRelevantChunkId,
    int RelevantPairCount,
    double? WindowWidth,
    bool SeparableByAConfirmScore);
