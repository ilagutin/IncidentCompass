namespace IncidentCompass.Infrastructure.OpenAiCompatible;

internal sealed class OpenAiCompatibleRetryPolicy
{
    public Task DelayBeforeRetryAsync(
        int retryBaseDelayMilliseconds,
        int maxRetryDelaySeconds,
        HttpResponseMessage? response,
        int attempt,
        CancellationToken cancellationToken)
    {
        var delay = CalculateDelay(
            retryBaseDelayMilliseconds,
            maxRetryDelaySeconds,
            response,
            attempt,
            DateTimeOffset.UtcNow);
        return Task.Delay(delay, cancellationToken);
    }

    public static TimeSpan CalculateDelay(
        int retryBaseDelayMilliseconds,
        int maxRetryDelaySeconds,
        HttpResponseMessage? response,
        int attempt,
        DateTimeOffset now)
    {
        var maxDelay = TimeSpan.FromSeconds(Math.Max(1, maxRetryDelaySeconds));
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } retryAfterDelta && retryAfterDelta > TimeSpan.Zero)
        {
            return ClampRetryDelay(retryAfterDelta, maxDelay);
        }

        if (retryAfter?.Date is { } retryAfterDate && retryAfterDate > now)
        {
            return ClampRetryDelay(retryAfterDate - now, maxDelay);
        }

        var baseDelayMilliseconds = Math.Max(1, retryBaseDelayMilliseconds);
        var exponentialDelayMilliseconds = baseDelayMilliseconds * Math.Pow(2, Math.Max(0, attempt));
        return TimeSpan.FromMilliseconds(Math.Min(maxDelay.TotalMilliseconds, exponentialDelayMilliseconds));
    }

    private static TimeSpan ClampRetryDelay(TimeSpan delay, TimeSpan maxDelay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > maxDelay ? maxDelay : delay;
    }
}
