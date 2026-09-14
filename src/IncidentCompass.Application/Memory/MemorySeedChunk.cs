namespace IncidentCompass.Application.Memory;

internal sealed record MemorySeedChunk(
    Guid Id,
    int Position,
    string Text,
    string TextHash,
    string EmbeddingProvider,
    string EmbeddingModel,
    int EmbeddingDimensions,
    IReadOnlyList<float> EmbeddingValues,
    string? HeadingPath = null);
