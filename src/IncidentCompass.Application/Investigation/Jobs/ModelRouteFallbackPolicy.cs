using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Intake.Configuration;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Decides whether one failed model call is retried on the route's declared fallback, and resolves
/// that route from the configuration snapshot the job is pinned to.
/// <para>
/// Two questions are answered here and nowhere else, because the answer to both has to be the same
/// for every call site: which failures a different provider could plausibly answer, and which route
/// answers them.
/// </para>
/// </summary>
internal static class ModelRouteFallbackPolicy
{
    private const string ChatRouteKind = "Chat";

    /// <summary>
    /// The failure kinds a second provider is retried for.
    /// <para>
    /// <see cref="ProviderFailureKind.Unavailable" /> is the case the feature exists for: the
    /// provider positively did not accept the request - 429, 503, a refused connection, a name that
    /// did not resolve, a TLS handshake that failed - so nothing was generated and a different
    /// endpoint is the one thing that can plausibly answer.
    /// <see cref="ProviderFailureKind.GenerationTimeout" /> is the other: the provider owned the
    /// deadline and did not answer inside it, and a different provider, or the smaller model a
    /// fallback route usually names, plausibly answers inside what is left.
    /// </para>
    /// <para>
    /// Everything else is deliberately absent, and the reason is one of two.
    /// </para>
    /// <para>
    /// The first reason is that the failure is the model's own answer being wrong, where a second
    /// call is money spent twice for the same outcome. <see cref="ProviderFailureKind.RejectedRequest" />
    /// describes a request that this request, sent again unchanged, reproduces.
    /// <see cref="ProviderFailureKind.OutputLimitReached" /> is a completion that ran into its
    /// ceiling. <see cref="ProviderFailureKind.InvalidResponse" /> covers an empty completion and a
    /// malformed tool call as well as an unparsable body; the three share one kind, and a kind is
    /// the unit a disposition is decided on, so the failure-over decision has to hold for the whole
    /// kind or not be made. It is not made.
    /// </para>
    /// <para>
    /// The second reason is that the outcome is not known.
    /// <see cref="ProviderFailureKind.AmbiguousInterruption" /> means the request may already have
    /// been accepted and generated; it dead-letters today precisely because the backend cannot prove
    /// it was not, and issuing a second call is the opposite of that stance.
    /// <see cref="ProviderFailureKind.Unknown" /> is unclassified by definition.
    /// <see cref="ProviderFailureKind.TransportFailure" /> is not produced on the chat path at all -
    /// an exhausted chat transport fault is classified as <c>Unavailable</c> or
    /// <c>AmbiguousInterruption</c> - so the day something does produce it, the decision belongs
    /// with whatever produces it rather than with a guess made here in advance.
    /// </para>
    /// </summary>
    public static bool IsWorthFailingOver(ProviderFailureKind failureKind) =>
        failureKind is ProviderFailureKind.Unavailable or ProviderFailureKind.GenerationTimeout;

    /// <summary>
    /// Returns the fallback for a failed call, or <see langword="null" /> when the call is not
    /// failed over: the route declares none, the failure is not one of the kinds above, or the
    /// declaration does not resolve to a usable chat route in the snapshot this job is pinned to.
    /// </summary>
    /// <remarks>
    /// The resolution checks are the same ones the configuration load validator already enforces, so
    /// reaching one of them means a snapshot got past load unvalidated. The answer then is no
    /// fallback and the primary's own disposition, which is exactly the behaviour of a route that
    /// declared nothing - never a throw of a different shape from inside a failure path, and never a
    /// second hop by way of a fallback that names another fallback.
    /// </remarks>
    public static ModelRouteFallback? TryResolve(
        TriageJobCallContext context,
        TriageRouteSettings route,
        Exception failure)
    {
        if (string.IsNullOrWhiteSpace(route.FallbackRouteId) ||
            string.Equals(route.FallbackRouteId, context.RouteId, StringComparison.Ordinal))
        {
            return null;
        }

        if (ProviderOutageExceptionClassifier.FindFailureKind(failure) is not { } failureKind ||
            !IsWorthFailingOver(failureKind))
        {
            return null;
        }

        if (!context.Configuration.Routes.TryGetValue(route.FallbackRouteId, out var fallbackRoute) ||
            !string.Equals(fallbackRoute.Kind, ChatRouteKind, StringComparison.Ordinal))
        {
            return null;
        }

        return new ModelRouteFallback(route.FallbackRouteId, fallbackRoute);
    }
}
