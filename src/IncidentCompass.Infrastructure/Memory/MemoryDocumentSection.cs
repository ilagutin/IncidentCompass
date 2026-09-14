namespace IncidentCompass.Infrastructure.Memory;

internal sealed record MemoryDocumentSection(string HeadingPath, IReadOnlyList<string> Lines);
