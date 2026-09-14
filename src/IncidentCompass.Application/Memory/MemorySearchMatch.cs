namespace IncidentCompass.Application.Memory;

internal sealed record MemorySearchMatch(
    Guid MemoryItemId,
    Guid ChunkId,
    string Kind,
    string Source,
    string Title,
    int ChunkPosition,
    string Text,
    double Score,
    string? ServiceName,
    string? Component,
    string? ReleaseName,
    string? HeadingPath = null);
