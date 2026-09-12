using IncidentCompass.Application.Core.Errors;

namespace IncidentCompass.Application.Core.Embeddings;

public sealed class EmbeddingClientException : ProviderException
{
    public EmbeddingClientException(
        string provider,
        string message,
        string? errorCode = null,
        string? providerErrorCode = null,
        Exception? innerException = null,
        ProviderFailureKind failureKind = ProviderFailureKind.Unknown)
        : base(provider, message, errorCode, providerErrorCode, innerException, failureKind)
    {
    }
}
