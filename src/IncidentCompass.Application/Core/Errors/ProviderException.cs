using System.Net;

namespace IncidentCompass.Application.Core.Errors;

public abstract class ProviderException : IncidentCompass.Application.Core.Exceptions.AppException
{
    protected ProviderException(
        string provider,
        string message,
        string? errorCode = null,
        HttpStatusCode? statusCode = null,
        string? providerErrorCode = null,
        Exception? innerException = null,
        ProviderFailureKind failureKind = ProviderFailureKind.Unknown)
        : base(message, innerException)
    {
        Provider = provider;
        ErrorCode = errorCode;
        StatusCode = statusCode;
        ProviderErrorCode = providerErrorCode;
        FailureKind = failureKind;
    }

    public string Provider { get; }

    public string? ErrorCode { get; }

    public HttpStatusCode? StatusCode { get; }

    public string? ProviderErrorCode { get; }

    public ProviderFailureKind FailureKind { get; }
}
