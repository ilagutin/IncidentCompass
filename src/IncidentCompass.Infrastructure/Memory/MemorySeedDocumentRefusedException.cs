namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// A reviewed seed file the chunker refused to cut into embedding passages, named by its source key
/// relative to the seed directory so an operator can find it.
/// </summary>
/// <remarks>
/// The message carries the source key and the chunker's reason, never the document text. It stays
/// an <see cref="InvalidOperationException" /> so the seed pass and the corpus commands treat it as the
/// failed pass it is: nothing is published and the previous corpus stays current.
/// </remarks>
internal sealed class MemorySeedDocumentRefusedException(string seedSource, MemoryDocumentChunkRefusedException reason)
    : InvalidOperationException("Memory seed '" + seedSource + "' was refused by the chunker: " + reason.Message, reason)
{
    public string SeedSource { get; } = seedSource;
}
