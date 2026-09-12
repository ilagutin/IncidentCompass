using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Describes an <see cref="IAiModelClient" /> that broke the one obligation its port states: a
/// provider failure reaches the governed caller as a normalized <see cref="AiModelException" />, or
/// it does not cross the boundary at all.
/// </summary>
/// <remarks>
/// <para>
/// The kind is <see cref="ProviderFailureKind.Unknown" /> because a breach says nothing about what
/// happened at the provider: the request may have been refused before dispatch or generated and
/// billed in full. It is also the disposition an unknown blast radius wants -
/// <see cref="ModelRouteFallbackPolicy" /> declines to fail it over, so a broken adapter cannot spend
/// a second provider's money on every call, and the job runner charges it against the normal finite
/// attempt budget, so a host running one stops rather than retrying forever.
/// </para>
/// <para>
/// Containment costs two things, both deliberate and both pinned by tests. The caller matches
/// cancellation on the exception it is handed, so an <see cref="OperationCanceledException" /> a
/// client wrapped inside another exception is not seen as cancellation and lands here instead. And a
/// normalized failure that is not the first inner exception of an <see cref="AggregateException" />
/// is not found, because the search walks <see cref="Exception.InnerException" />. A client that
/// does either is in breach on its own, which is why neither widens the search.
/// </para>
/// <para>
/// The message and the error code are fixed values. The offending exception can carry provider
/// response text, credentials or rendered prompt content, and the ledger metadata built from this
/// exception is durable and readable through the API, so nothing from it reaches a persisted row:
/// the original survives only as <see cref="Exception.InnerException" />. The rest of the rationale
/// is in <c>docs/model-gateway.md</c>, "Provider Response And Failure Boundary".
/// </para>
/// </remarks>
internal static class AiModelClientContractBreach
{
    /// <summary>
    /// The durable error code a contract breach is recorded under.
    /// </summary>
    /// <remarks>
    /// It is lowercase letters and underscores only and well under 80 characters, which is what
    /// <see cref="Core.Resilience.ProviderOutageExceptionClassifier.FindSafeErrorCode" /> requires,
    /// so this is the code that reaches the ledger and the job's <c>lastErrorCode</c> rather than
    /// the generic <c>provider_failure</c> fallback an unclassified kind would otherwise get.
    /// </remarks>
    internal const string ErrorCode = "provider_contract_violation";

    /// <summary>
    /// The adapter identity recorded when the adapter itself is what failed.
    /// </summary>
    internal const string UnknownProvider = "unknown";

    /// <summary>
    /// The whole message. It names the broken contract and nothing about the call that broke it.
    /// </summary>
    private const string BreachMessage =
        "The model client raised a failure that was not normalized to an application-level provider exception.";

    /// <summary>
    /// Wraps <paramref name="breach" /> in the normalized exception its adapter should have raised.
    /// </summary>
    /// <remarks>
    /// The caller uses one instance for both the failure accounting and the inner exception of the
    /// failure it throws, so the original exception stays reachable through this one and the chain
    /// still holds a <see cref="ProviderException" /> for the outage classifier to find.
    /// </remarks>
    public static AiModelException Describe(Exception breach) =>
        new(
            UnknownProvider,
            BreachMessage,
            errorCode: ErrorCode,
            innerException: breach,
            failureKind: ProviderFailureKind.Unknown);
}
