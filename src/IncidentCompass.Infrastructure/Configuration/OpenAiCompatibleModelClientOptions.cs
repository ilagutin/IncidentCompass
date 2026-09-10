namespace IncidentCompass.Infrastructure.Configuration;

public sealed class OpenAiCompatibleModelClientOptions
{
    public const string SectionName = "IncidentCompass:ModelGateway:OpenAiCompatible";

    public string BaseUrl { get; init; } = "https://api.openai.com";

    public string ChatCompletionsPath { get; init; } = "/v1/chat/completions";

    public string? ApiKey { get; init; }

    public string? Organization { get; init; }

    public int TimeoutSeconds { get; init; } = 300;

    public int MaxRetryAttempts { get; init; } = 2;

    public int RetryBaseDelayMilliseconds { get; init; } = 200;

    public int MaxRetryDelaySeconds { get; init; } = 5;

    public Dictionary<string, OpenAiReasoningMode> ReasoningModes { get; init; } = new(StringComparer.Ordinal);

    public bool AllowInsecureHttpForLoopback { get; init; }

    public bool IsValid()
    {
        return IsTransportValid() &&
               OpenAiCompatibleEndpointPolicy.IsValid(
                   ApiKey,
                   BaseUrl,
                   ChatCompletionsPath,
                   AllowInsecureHttpForLoopback,
                   TimeoutSeconds,
                   MaxRetryAttempts,
                   RetryBaseDelayMilliseconds);
    }

    /// <summary>
    /// The settings that stay host-wide when a triage-configuration provider entry supplies the
    /// endpoint and credential for a call. <see cref="IsValid" /> keeps its meaning for the
    /// startup options validator, which still requires a usable host-wide default profile whenever
    /// the host selects the OpenAI-compatible gateway.
    /// </summary>
    public bool IsTransportValid()
    {
        return MaxRetryDelaySeconds is > 0 and <= 3600 &&
               ReasoningModes.All(static entry =>
                   !string.IsNullOrWhiteSpace(entry.Key) &&
                   Enum.IsDefined(entry.Value)) &&
               OpenAiCompatibleEndpointPolicy.IsTransportValid(
                   TimeoutSeconds,
                   MaxRetryAttempts,
                   RetryBaseDelayMilliseconds);
    }

    public bool TryCreateEndpointUri(out Uri? endpointUri)
    {
        return OpenAiCompatibleEndpointPolicy.TryCreateEndpointUri(
            BaseUrl,
            ChatCompletionsPath,
            AllowInsecureHttpForLoopback,
            out endpointUri);
    }
}
