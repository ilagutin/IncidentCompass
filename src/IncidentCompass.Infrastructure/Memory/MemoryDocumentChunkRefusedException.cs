namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// The chunker's refusal of a document whose heading path, or one line together with its heading path,
/// does not fit the chunk token limit. It is never raised for a counter or tokenizer failure.
/// </summary>
/// <remarks>The message states the reason only and never carries document text.</remarks>
internal sealed class MemoryDocumentChunkRefusedException(string message) : InvalidOperationException(message);
