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
/// </remarks>
internal static class ProviderErrorCodes
{
    public static string For(ProviderFailureKind failureKind, Exception exception) =>
        failureKind switch
        {
            ProviderFailureKind.Unavailable => "provider_unavailable",
            ProviderFailureKind.RejectedRequest => "provider_request_rejected",
            ProviderFailureKind.GenerationTimeout => "provider_generation_timeout",
            ProviderFailureKind.OutputLimitReached => "provider_output_limit_reached",
            ProviderFailureKind.AmbiguousInterruption => "provider_dispatch_outcome_unknown",
            ProviderFailureKind.InvalidResponse =>
                ProviderOutageExceptionClassifier.FindSafeErrorCode(exception) ?? "provider_invalid_response",
            _ => ProviderOutageExceptionClassifier.FindSafeErrorCode(exception) ?? "provider_failure"
        };
}
