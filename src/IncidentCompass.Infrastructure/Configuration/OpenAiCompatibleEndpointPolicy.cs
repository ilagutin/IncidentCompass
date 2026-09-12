namespace IncidentCompass.Infrastructure.Configuration;

internal static class OpenAiCompatibleEndpointPolicy
{
    public static bool IsValid(
        string? apiKey,
        string? baseUrl,
        string? endpointPath,
        bool allowInsecureHttpForLoopback,
        int timeoutSeconds,
        int maxRetryAttempts,
        int retryBaseDelayMilliseconds)
    {
        return !string.IsNullOrWhiteSpace(apiKey) &&
               TryCreateEndpointUri(
                   baseUrl,
                   endpointPath,
                   allowInsecureHttpForLoopback,
                   out _) &&
               IsTransportValid(
                   timeoutSeconds,
                   maxRetryAttempts,
                   retryBaseDelayMilliseconds);
    }

    /// <summary>
    /// The half of <see cref="IsValid" /> that stays host-wide once a provider entry can bring its
    /// own endpoint and credential. A per-call check uses this rather than the whole predicate,
    /// because in a multi-provider configuration the host-wide <c>BaseUrl</c> and <c>ApiKey</c> are
    /// not the ones the call will use, and failing a call because an unused default is blank would
    /// force operators to invent a dummy host credential.
    /// </summary>
    public static bool IsTransportValid(
        int timeoutSeconds,
        int maxRetryAttempts,
        int retryBaseDelayMilliseconds)
    {
        return timeoutSeconds is > 0 and <= 3600 &&
               maxRetryAttempts is >= 0 and <= 10 &&
               retryBaseDelayMilliseconds is > 0 and <= 60_000;
    }

    public static bool TryCreateEndpointUri(
        string? baseUrl,
        string? endpointPath,
        bool allowInsecureHttpForLoopback,
        out Uri? endpointUri)
    {
        endpointUri = null;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            !IsAllowedBaseUri(baseUri, allowInsecureHttpForLoopback) ||
            !IsValidEndpointPath(endpointPath))
        {
            return false;
        }

        endpointUri = new Uri(baseUri, endpointPath!);
        return true;
    }

    private static bool IsAllowedBaseUri(
        Uri baseUri,
        bool allowInsecureHttpForLoopback)
    {
        if (baseUri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return allowInsecureHttpForLoopback &&
               baseUri.Scheme == Uri.UriSchemeHttp &&
               IsLocalHttpAlias(baseUri);
    }

    private static bool IsLocalHttpAlias(Uri baseUri)
    {
        return baseUri.IsLoopback ||
               string.Equals(baseUri.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidEndpointPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        return Uri.TryCreate(path, UriKind.Relative, out _);
    }
}
