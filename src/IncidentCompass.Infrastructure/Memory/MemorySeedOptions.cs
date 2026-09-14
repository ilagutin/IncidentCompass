namespace IncidentCompass.Infrastructure.Memory;

internal sealed class MemorySeedOptions
{
    public const string SectionName = "IncidentCompass:Memory:Seed";

    public bool Enabled { get; init; }

    public string TenantId { get; init; } = "local";

    public string Owner { get; init; } = "default";

    public string SourceDirectory { get; init; } = "../../samples";

    public bool RuntimeResyncEnabled { get; init; }

    public int RuntimeResyncIntervalSeconds { get; init; } = 300;

    public MemoryChunkingOptions Chunking { get; init; } = new();
}
