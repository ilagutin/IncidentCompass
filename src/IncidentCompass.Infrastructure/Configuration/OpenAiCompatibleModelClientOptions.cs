namespace IncidentCompass.Infrastructure.Configuration;

public sealed class OpenAiCompatibleModelClientOptions
{
    public const string SectionName = "IncidentCompass:ModelGateway:OpenAiCompatible";

    public const int DefaultConnectTimeoutSeconds = 30;

    public const int MaximumConnectTimeoutSeconds = 600;

    public const int DefaultFirstOutputTimeoutSeconds = 600;

    public const int DefaultStreamInactivityTimeoutSeconds = 600;

    public const int MaximumResponseTimeoutSeconds = 3600;

    public string BaseUrl { get; init; } = "https://api.openai.com";

    public string ChatCompletionsPath { get; init; } = "/v1/chat/completions";

    public string? ApiKey { get; init; }

    public string? Organization { get; init; }

    /// <summary>
    /// How long establishing the connection to the provider may take, per HTTP attempt. It bounds
    /// the connection phase only, never the generation.
    /// </summary>
    public int ConnectTimeoutSeconds { get; init; } = DefaultConnectTimeoutSeconds;

    /// <summary>
    /// How long an HTTP attempt may wait from dispatch until the response starts. A non-streaming
    /// provider starts its response once it has finished generating, so this bounds one generation
    /// per HTTP attempt. Unset means <see cref="TimeoutSeconds"/> when that deprecated key is set,
    /// otherwise <see cref="DefaultFirstOutputTimeoutSeconds"/>.
    /// </summary>
    public int? FirstOutputTimeoutSeconds { get; init; }

    /// <summary>
    /// How long the response body may go without delivering any bytes once it has started.
    /// </summary>
    public int StreamInactivityTimeoutSeconds { get; init; } = DefaultStreamInactivityTimeoutSeconds;

    /// <summary>
    /// Deprecated: use <see cref="FirstOutputTimeoutSeconds"/>. When it is set and the new key is
    /// not, its value is the first-output limit. Setting both is a validation error.
    /// </summary>
    public int? TimeoutSeconds { get; init; }

    public int MaxRetryAttempts { get; init; } = 2;

    public int RetryBaseDelayMilliseconds { get; init; } = 200;

    public int MaxRetryDelaySeconds { get; init; } = 5;

    public Dictionary<string, OpenAiReasoningMode> ReasoningModes { get; init; } = new(StringComparer.Ordinal);

    public bool AllowInsecureHttpForLoopback { get; init; }

    /// <summary>Whether the first-output limit comes from the deprecated <see cref="TimeoutSeconds"/>.</summary>
    public bool UsesDeprecatedTimeoutSeconds => TimeoutSeconds is not null && FirstOutputTimeoutSeconds is null;

    /// <summary>Whether both the current and the deprecated first-output keys are set.</summary>
    public bool HasConflictingFirstOutputTimeouts => TimeoutSeconds is not null && FirstOutputTimeoutSeconds is not null;

    public int ResolveFirstOutputTimeoutSeconds() =>
        FirstOutputTimeoutSeconds ?? TimeoutSeconds ?? DefaultFirstOutputTimeoutSeconds;

    public bool IsValid()
    {
        return IsTransportValid() &&
               OpenAiCompatibleEndpointPolicy.IsValid(
                   ApiKey,
                   BaseUrl,
                   ChatCompletionsPath,
                   AllowInsecureHttpForLoopback,
                   ResolveFirstOutputTimeoutSeconds(),
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
               AreCallLimitsValid() &&
               ReasoningModes.All(static entry =>
                   !string.IsNullOrWhiteSpace(entry.Key) &&
                   Enum.IsDefined(entry.Value)) &&
               OpenAiCompatibleEndpointPolicy.IsTransportValid(
                   ResolveFirstOutputTimeoutSeconds(),
                   MaxRetryAttempts,
                   RetryBaseDelayMilliseconds);
    }

    /// <summary>
    /// The provider call limits: each in range, and the first-output limit spelled at most once.
    /// </summary>
    public bool AreCallLimitsValid()
    {
        return !HasConflictingFirstOutputTimeouts &&
               ConnectTimeoutSeconds is > 0 and <= MaximumConnectTimeoutSeconds &&
               ResolveFirstOutputTimeoutSeconds() is > 0 and <= MaximumResponseTimeoutSeconds &&
               StreamInactivityTimeoutSeconds is > 0 and <= MaximumResponseTimeoutSeconds;
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
