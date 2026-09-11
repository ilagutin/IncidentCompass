using System.Net.Sockets;

namespace IncidentCompass.Infrastructure.Http;

/// <summary>
/// The one place that answers whether an <see cref="HttpRequestException" /> means the request never
/// left this process.
/// </summary>
/// <remarks>
/// The distinction decides what an adapter may honestly say. A failure that happened before any
/// request byte was written changed nothing at the other end, so it can be reported as a plain
/// unavailability and settled by retrying later. Any other transport fault may have arrived, been
/// acted on, and lost only its answer, so a mutating call must leave that outcome open for a person
/// to settle rather than claiming nothing happened.
/// <para>
/// The set is deliberately narrow. Name resolution, TLS handshake and proxy tunnel failures all
/// precede the request itself. A connection error qualifies only when the socket says the connection
/// was never established - refused, unreachable, unresolved or timed out while connecting - because
/// <see cref="HttpRequestError.ConnectionError" /> alone is also reported for a connection that
/// failed after the request was on the wire.
/// </para>
/// </remarks>
internal static class HttpTransportFailureClassifier
{
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
