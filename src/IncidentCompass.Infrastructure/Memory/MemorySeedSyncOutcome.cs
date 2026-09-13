using IncidentCompass.Application.Memory;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// What one synchronization pass did, in bounded identifiers and counts.
/// </summary>
/// <remarks>
/// <c>Published</c> says whether a generation became current. False means the pass declined to
/// publish, because the configured embedding route no longer matches the corpus or because the
/// installed local model cannot serve the route, and the previous corpus is untouched.
/// <c>ModelErrorCode</c> is the local model's own code when the model is unavailable.
/// </remarks>
internal sealed record MemorySeedSyncOutcome(
    bool Published,
    MemoryCorpusState State,
    Guid? Generation,
    MemoryEmbeddingRoute Route,
    int ItemCount,
    string? ModelErrorCode = null);
