using System.Net;
using System.Net.Sockets;
using IncidentCompass.Application.Core.Errors;

namespace IncidentCompass.Infrastructure.OpenAiCompatible;

internal static class OpenAiCompatibleFailureClassifier
{
    public static ProviderFailureKind Classify(HttpStatusCode statusCode)
    {
        if (statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            return ProviderFailureKind.Unavailable;
        }

        if (statusCode == HttpStatusCode.RequestTimeout)
        {
            return ProviderFailureKind.GenerationTimeout;
        }

        var statusCodeValue = (int)statusCode;
        if (statusCodeValue is >= 400 and < 500 ||
            statusCode is HttpStatusCode.NotImplemented or HttpStatusCode.HttpVersionNotSupported)
        {
            return ProviderFailureKind.RejectedRequest;
        }

        return statusCodeValue >= 500
            ? ProviderFailureKind.AmbiguousInterruption
            : ProviderFailureKind.RejectedRequest;
    }

    public static bool IsRetryableGenerationStatus(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
    }

    public static ProviderFailureKind ClassifyEmbedding(HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.RequestTimeout)
        {
            return ProviderFailureKind.GenerationTimeout;
        }

        if (statusCode is HttpStatusCode.NotImplemented or HttpStatusCode.HttpVersionNotSupported)
        {
            return ProviderFailureKind.RejectedRequest;
        }

        var statusCodeValue = (int)statusCode;
        if (statusCode == HttpStatusCode.TooManyRequests || statusCodeValue >= 500)
        {
            return ProviderFailureKind.Unavailable;
        }

        return ProviderFailureKind.RejectedRequest;
    }

    public static bool IsSafePreDispatchFailure(HttpRequestException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception.HttpRequestError is
            HttpRequestError.NameResolutionError or
            HttpRequestError.SecureConnectionError or
            HttpRequestError.ProxyTunnelError)
        {
            return true;
        }

        if (exception.HttpRequestError != HttpRequestError.ConnectionError)
        {
            return false;
        }

        return FindSocketError(exception) is
            SocketError.ConnectionRefused or
            SocketError.TimedOut or
            SocketError.HostUnreachable or
            SocketError.NetworkUnreachable or
            SocketError.HostNotFound or
            SocketError.AddressNotAvailable;
    }

    public static ProviderFailureKind Classify(HttpRequestException exception)
    {
        return IsSafePreDispatchFailure(exception)
            ? ProviderFailureKind.Unavailable
            : ProviderFailureKind.AmbiguousInterruption;
    }

    private static SocketError? FindSocketError(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socketException)
            {
                return socketException.SocketErrorCode;
            }
        }

        return null;
    }
}
