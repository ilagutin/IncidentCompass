namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi;

/// <summary>
/// Raised when a response body grows past the permitted size: the whole body in bytes, read by
/// <see cref="OpenAiResponseBodyReader" /> or <see cref="OpenAiStreamingResponseReader" />, or one line
/// or event of a stream in characters, checked by <see cref="OpenAiServerSentEventParser" />.
/// It is deliberately not an <see cref="IOException" />, so it cannot be mistaken for a body that was
/// cut off in transit.
/// </summary>
internal sealed class OpenAiResponseBodyTooLargeException(long maxBytes)
    : Exception("The provider response exceeded a size limit of " + maxBytes + ".")
{
    public long MaxBytes { get; } = maxBytes;
}
