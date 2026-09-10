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
        return MaxRetryDelaySeconds is > 0 and <= 3600 &&
               OpenAiCompatibleEndpointPolicy.IsValid(
                   ApiKey,
                   BaseUrl,
                   EmbeddingsPath,
                   AllowInsecureHttpForLoopback,
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
