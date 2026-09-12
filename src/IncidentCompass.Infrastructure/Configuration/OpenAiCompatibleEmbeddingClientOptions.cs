namespace IncidentCompass.Infrastructure.Configuration;

public sealed class OpenAiCompatibleEmbeddingClientOptions
{
    public const string SectionName = "IncidentCompass:Embeddings:OpenAiCompatible";

    public string BaseUrl { get; init; } = "https://api.openai.com";

    public string EmbeddingsPath { get; init; } = "/v1/embeddings";

    public string? ApiKey { get; init; }

    public string? Organization { get; init; }

    public int TimeoutSeconds { get; init; } = 30;

    public int MaxRetryAttempts { get; init; } = 2;

    public int RetryBaseDelayMilliseconds { get; init; } = 200;

    public int MaxRetryDelaySeconds { get; init; } = 5;

    public bool AllowInsecureHttpForLoopback { get; init; }

    public bool IsValid()
    {
        return IsTransportValid() &&
               OpenAiCompatibleEndpointPolicy.IsValid(
                   ApiKey,
                   BaseUrl,
                   EmbeddingsPath,
                   AllowInsecureHttpForLoopback,
                   TimeoutSeconds,
                   MaxRetryAttempts,
                   RetryBaseDelayMilliseconds);
    }

    /// <summary>
    /// The settings that stay host-wide when a triage-configuration provider entry supplies the
    /// endpoint and credential for a call. See the matching member on
    /// <see cref="OpenAiCompatibleModelClientOptions" />.
    /// </summary>
    public bool IsTransportValid()
    {
        return MaxRetryDelaySeconds is > 0 and <= 3600 &&
               OpenAiCompatibleEndpointPolicy.IsTransportValid(
                   TimeoutSeconds,
                   MaxRetryAttempts,
                   RetryBaseDelayMilliseconds);
    }

    public bool TryCreateEndpointUri(out Uri? endpointUri)
    {
        return OpenAiCompatibleEndpointPolicy.TryCreateEndpointUri(
            BaseUrl,
            EmbeddingsPath,
            AllowInsecureHttpForLoopback,
            out endpointUri);
    }
}
