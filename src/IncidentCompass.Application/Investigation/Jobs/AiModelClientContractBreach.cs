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
/// <b>Why this exists at all.</b> <see cref="InvestigationModelCaller" /> raises
/// <see cref="InvestigationModelCallFailureException" /> for a failed call, and that exception is
/// the only thing carrying two facts its callers need: which provider failure kind ended the call,
/// and what durable <c>ModelCall</c> record is still owed for a call that may already have been
/// billed. An adapter that threw something the caller did not recognize used to leave both facts
/// unstated, which made every caller treat that exception as non-exhaustive and defeated the reason
/// it exists. Synthesizing this one turns an adapter defect into the ordinary recorded, classified
/// outcome, so the guarantee holds for every client the caller is ever handed - a test double and a
/// future adapter included - rather than only for the two that normalize correctly today.
/// </para>
/// <para>
/// <b>Why the caller and not a composition-time decorator.</b> A decorator wrapped around the
/// shipped client selection would be bypassed by exactly the clients most likely to break the
/// contract: tests and future hosts replace the <see cref="IAiModelClient" /> registration outright
/// rather than layering onto it. The bounded caller is the one place every governed model call goes
/// through no matter what is registered, so it is where a contract that must always hold is kept.
/// </para>
/// <para>
/// <b>Why <see cref="ProviderFailureKind.Unknown" /> and not a classified kind.</b> Nothing about a
/// contract breach says what happened at the provider; the request may have been refused before
/// dispatch or generated and billed in full. <c>Unknown</c> is the kind that states exactly that,
/// and its disposition is the right one for a defect whose blast radius is not known:
/// <see cref="ModelRouteFallbackPolicy" /> does not fail it over, so a broken adapter cannot spend a
/// second provider's money on every call, and the job runner charges it against the normal finite
/// attempt budget, so a host running a broken adapter stops rather than retrying forever.
/// </para>
/// <para>
/// Two qualifications, because that last sentence is not absolute.
/// </para>
/// <para>
/// The kind set here is the kind the runner sees only when nothing else in the chain is classified.
/// <see cref="Core.Resilience.ProviderOutageExceptionClassifier.FindFailureKind" /> walks the whole
/// chain and returns the first classified provider failure it meets, skipping <c>Unknown</c>, and
/// the offender is chained beneath this exception. So a client that raises or nests an already
/// classified <see cref="ProviderException" /> that is not an <see cref="AiModelException" /> - an
/// <see cref="Core.Embeddings.EmbeddingClientException" /> is the one other concrete subclass -
/// keeps that kind and is dispositioned on it. That is coherent rather than a leak: the failure
/// really was classified, only the wrapper was wrong, and the error code still says the wrapper was
/// wrong. What it means is that this type decides the error code always and the kind only sometimes.
/// </para>
/// <para>
/// The finite-budget claim holds when the breach is what ended the call. A breach on a fail-over leg
/// does not end the call; the primary's failure does, and by the documented fail-over rule that is
/// the kind the runner reads. When the primary was genuinely <c>Unavailable</c>, the attempt takes
/// the outage branch and is retried without consuming an attempt for as long as that provider stays
/// down, breaching fallback or not. That is the outage branch behaving correctly for a provider that
/// really is unreachable, and the breach is still visible in the fallback leg's own ledger row.
/// </para>
/// <para>
/// <b>Why the provider is not named.</b> The configured provider id is recorded separately by
/// <see cref="ModelCallLedgerAccountant" /> and stays truthful; this is the adapter identity, and
/// there is no honest value for it, because the adapter that answered is precisely the thing that
/// misbehaved. <see cref="UnknownProvider" /> is used rather than the route's provider so the row
/// asserts nothing it cannot support. Cost accounting keys on the configured provider id, not on
/// this field, so the value here cannot make an unpriced row match a shipped price by accident. See
/// <c>docs/cost-tracking.md</c>, "Provider Versus Provider ID".
/// </para>
/// <para>
/// <b>Why the message says nothing about the original failure.</b> The offending exception can carry
/// provider response text, credentials or rendered prompt content, and the ledger metadata built
/// from this exception is durable and readable through the API. So the message is a fixed sentence
/// about the contract, the error code is a fixed closed-vocabulary value, and the original exception
/// survives only as <see cref="Exception.InnerException" />, where a developer reading a stack trace
/// finds it and no persisted row does. The failing type name is already logged by the caller's own
/// failure event, which is the one detail about it that is safe to record.
/// </para>
/// <para>
/// <b>Two costs this containment has, both deliberate.</b> The caller matches cancellation on the
/// exception it is handed, so an <see cref="OperationCanceledException" /> a client wrapped inside
/// another exception is not seen as cancellation and lands here instead: during host shutdown that
/// turns a clean drain into an attempt that fails and dead-letters, blamed on the adapter. And a
/// normalized failure that is not the first entry
/// of an <see cref="AggregateException" /> is not found either, because the search walks
/// <see cref="Exception.InnerException" /> and an aggregate exposes only its first inner exception
/// there; a real billed call then produces an unpriced row with its kind, code, provider and
/// provider-reported tokens all dropped.
/// </para>
/// <para>
/// Neither is worth widening the search for, and the reason is the same in both cases: a client that
/// hides a cancellation or aggregates a normalized failure behind other exceptions is itself in
/// breach. The port asks for the normalized exception, raised, and teaching the caller to dig it out
/// of arbitrary shapes would make the contract mean whatever the next adapter happens to do. Both
/// behaviours are pinned by tests so they stay recorded decisions rather than surprises.
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
