namespace IncidentCompass.Infrastructure.Memory;

internal static class MemorySeedOptionsValidator
{
    public static void Validate(MemorySeedOptions settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Owner);
        settings.Chunking.Validate();
        if (settings.RuntimeResyncEnabled &&
            (settings.RuntimeResyncIntervalSeconds < 1 || settings.RuntimeResyncIntervalSeconds > 86400))
        {
            throw new InvalidOperationException("Memory RuntimeResyncIntervalSeconds must be between 1 and 86400.");
        }
    }
}
