namespace IncidentCompass.Infrastructure.Memory;

/// <summary>Generic embedding endpoints expose no tokenizer; estimate one token per four UTF-16 characters.</summary>
internal sealed class CharacterEstimateChunkTokenCounter : IMemoryChunkTokenCounter
{
    public const int CharactersPerToken = 4;

    public string Kind => "estimate";

    public Task InitializeAsync(MemoryChunkingOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        return Task.CompletedTask;
    }

    public int CountTokens(string text) => (int)((text.Length + (long)CharactersPerToken - 1) / CharactersPerToken);

    public int CountOverlapTokens(string text) => CountTokens(text);
}
