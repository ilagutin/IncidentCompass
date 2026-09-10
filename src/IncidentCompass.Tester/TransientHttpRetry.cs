using System.Net;

namespace IncidentCompass.Tester;

internal sealed class TransientHttpRetry(TimeSpan delay)
{
    // A tester run is bounded, so the loop carries its own attempt cap rather than relying on the
    // outer run deadline as its only bound. On the last attempt the transient failure propagates,
    // so the run reports the real error instead of stalling until the deadline expires.
    internal const int MaxAttempts = 10;

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (HttpRequestException exception) when (attempt < MaxAttempts && IsTransient(exception.StatusCode))
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (TaskCanceledException) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    // Deliberately wider than the provider retry rules in Infrastructure, and unrelated to them:
    // this retries the IncidentCompass API itself while a local demo or evaluation stack is still
    // coming up, where no status code at all and any 5xx both mean "not listening yet".
    private static bool IsTransient(HttpStatusCode? statusCode)
    {
        return statusCode is null ||
               statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
               (int)statusCode >= 500;
    }
}
