using IncidentCompass.Application.Memory;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// What one synchronization pass did, in bounded identifiers and counts.
/// </summary>
/// <remarks>
/// <c>Published</c> says whether a generation became current. False means the pass declined to
/// publish because the configured embedding route no longer matches the corpus, and the previous
/// corpus is untouched.
/// </remarks>
internal sealed record MemorySeedSyncOutcome(
    bool Published,
    MemoryCorpusState State,
    Guid? Generation,
    MemoryEmbeddingRoute Route,
    int ItemCount);
