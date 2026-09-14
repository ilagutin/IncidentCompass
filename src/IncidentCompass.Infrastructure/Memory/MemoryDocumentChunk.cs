namespace IncidentCompass.Infrastructure.Memory;

internal sealed record MemoryDocumentChunk(int Position, string HeadingPath, string Text);
