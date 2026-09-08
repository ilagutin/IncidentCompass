using System.Net;
using IncidentCompass.Application.Core.Errors;

namespace IncidentCompass.Application.Core.Embeddings;

public sealed class EmbeddingClientException : ProviderException
{
    public EmbeddingClientException(
        string provider,
        string message,
        string? errorCode = null,
        HttpStatusCode? statusCode = null,
        string? providerErrorCode = null,
        Exception? innerException = null,
        ProviderFailureKind failureKind = ProviderFailureKind.Unknown)
        : base(provider, message, errorCode, statusCode, providerErrorCode, innerException, failureKind)
    {
    }
}
