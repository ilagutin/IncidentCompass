namespace IncidentCompass.IntegrationTests;

/// <summary>One attacked query: the chunk whose text replaced it, and what the tool did with that text.</summary>
public sealed record MemorySearchRelevanceJudgeBenchmarkAttackQuery(
    string QueryId,
    string Category,
    string ChunkId,
    int ReturnedItemCount,
    int ConfirmedItemCount);
