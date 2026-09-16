using IncidentCompass.Application.Core.Resilience;

namespace IncidentCompass.Application.Core.Errors;

/// <summary>
/// The single map from a classified provider failure to the durable error code that names it.
/// </summary>
/// <remarks>
/// Both the model-call accounting path and the job runner's retry/dead-letter decision read this map,
/// and the runner branches on the code it gets back. Two copies of the mapping would mean a new
/// <see cref="ProviderFailureKind"/> classified specifically on one path and as the generic
/// <c>provider_failure</c> on the other, which is a silent change of disposition rather than a
/// cosmetic difference in a stored string.
/// <para>
/// A <see cref="ProviderFailureKind.GenerationTimeout"/> keeps one kind, and so one disposition, for
/// every provider call limit. The code names which limit fired when the adapter says so, and only
/// from the closed set below, so an adapter cannot introduce a new stored code by accident.
/// </para>
/// </remarks>
internal static class ProviderErrorCodes
{
    public const string GenerationTimeout = "provider_generation_timeout";

    /// <summary>The connection to the provider was not established within the connect limit.</summary>
    public const string ConnectTimeout = "provider_connect_timeout";

    /// <summary>The provider's response did not start within the first-output limit.</summary>
    public const string FirstOutputTimeout = "provider_first_output_timeout";

    /// <summary>The provider's response started and then delivered nothing for the inactivity limit.</summary>
    public const string StreamInactivityTimeout = "provider_stream_inactivity_timeout";

    public static string For(ProviderFailureKind failureKind, Exception exception) =>
        failureKind switch
        {
            ProviderFailureKind.Unavailable => "provider_unavailable",
            ProviderFailureKind.RejectedRequest => "provider_request_rejected",
            ProviderFailureKind.GenerationTimeout => ForTimeout(exception),
            ProviderFailureKind.OutputLimitReached => "provider_output_limit_reached",
            ProviderFailureKind.AmbiguousInterruption => "provider_dispatch_outcome_unknown",
            ProviderFailureKind.InvalidResponse =>
                ProviderOutageExceptionClassifier.FindSafeErrorCode(exception) ?? "provider_invalid_response",
            ProviderFailureKind.ConfigurationRequired =>
                ProviderOutageExceptionClassifier.FindSafeErrorCode(exception) ?? "provider_configuration_required",
            _ => ProviderOutageExceptionClassifier.FindSafeErrorCode(exception) ?? "provider_failure"
        };

    private static string ForTimeout(Exception exception) =>
        ProviderOutageExceptionClassifier.FindSafeErrorCode(exception) switch
        {
            ConnectTimeout => ConnectTimeout,
            FirstOutputTimeout => FirstOutputTimeout,
            StreamInactivityTimeout => StreamInactivityTimeout,
            _ => GenerationTimeout
        };
}
