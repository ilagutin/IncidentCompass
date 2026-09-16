namespace IncidentCompass.Infrastructure.Memory;

internal interface IMemoryChunkTokenCounter
{
    string Kind { get; }

    Task InitializeAsync(MemoryChunkingOptions options, CancellationToken cancellationToken);

    int CountTokens(string text);

    int CountOverlapTokens(string text);
}
