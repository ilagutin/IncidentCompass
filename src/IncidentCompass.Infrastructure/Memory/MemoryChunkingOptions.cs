namespace IncidentCompass.Infrastructure.Memory;

internal sealed class MemoryChunkingOptions
{
    public int MaxTokens { get; init; } = 448;

    public int OverlapTokens { get; init; } = 48;

    public int MinTokens { get; init; } = 32;

    public string Describe(string counterKind) =>
        FormattableString.Invariant($"v1;max={MaxTokens};overlap={OverlapTokens};min={MinTokens};{counterKind}");

    public void Validate()
    {
        if (MinTokens < 1 || OverlapTokens < 0 || MaxTokens <= (long)OverlapTokens + MinTokens)
        {
            throw new InvalidOperationException(
                "Memory chunking requires MinTokens > 0, OverlapTokens >= 0 and MaxTokens > OverlapTokens + MinTokens.");
        }
    }
}
