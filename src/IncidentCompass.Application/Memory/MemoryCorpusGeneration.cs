namespace IncidentCompass.Application.Memory;

/// <summary>
/// One published memory corpus generation: which vector space it is in and how much of it there
/// is. Counts are corpus sizes, never content.
/// </summary>
internal sealed record MemoryCorpusGeneration(
    Guid Generation,
    MemoryCorpusIdentity Identity,
    int ItemCount,
    int ChunkCount,
    DateTimeOffset PublishedAtUtc);
