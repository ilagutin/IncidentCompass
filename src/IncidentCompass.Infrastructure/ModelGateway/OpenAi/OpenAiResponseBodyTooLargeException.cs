namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi;

/// <summary>
/// Raised by <see cref="OpenAiResponseBodyReader" /> when a response body grows past the permitted
/// size. It is deliberately not an <see cref="IOException" />, so it cannot be mistaken for a body
/// that was cut off in transit.
/// </summary>
internal sealed class OpenAiResponseBodyTooLargeException(long maxBytes)
    : Exception("The provider response body exceeded " + maxBytes + " bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}
