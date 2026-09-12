namespace IncidentCompass.Application.Core.ModelClients;

/// <summary>
/// Defines the application-owned port for chat model completion adapters.
/// </summary>
/// <remarks>
/// Implementations must be safe to use from multiple concurrent request scopes and must not retain mutable request state between calls.
/// Provider SDK, HTTP and serialization failures must be normalized to application-level provider exceptions before crossing this boundary.
/// Implementations must throw <see cref="OperationCanceledException" /> only when the supplied cancellation token is canceled; provider timeouts and internal aborts must be reported as provider failures so retry and usage accounting stay deterministic.
/// The returned <see cref="AiModelResponse.Provider" /> value identifies the adapter that produced the response, and <see cref="AiModelResponse.ProposedToolCalls" /> must be empty when the provider did not propose tool calls.
/// <para>
/// The normalization sentence and the cancellation sentence directly above are the failure contract, and it has a stated consequence. An implementation that raises anything other than a normalized <c>AiModelException</c> or a caller-driven <see cref="OperationCanceledException" /> is in breach of this port. The governed caller does not trust adapter discipline to hold: it synthesizes the <c>AiModelException</c> the adapter should have raised, chains the offending exception beneath that synthesized one so the defect stays readable in a stack trace, and records the call in the triage ledger under the error code <c>provider_contract_violation</c>.
/// </para>
/// <para>
/// That is containment, not absolution, and the ledger row shows why. It names the route, the configured provider entry and the requested model, so a reader can tell which call it was. It does not name a real answering adapter, and it carries no token counts and charges nothing, because a breaching adapter reports no usage and none is invented: a provider that billed for the call is invisible to the cost roll-up. The failure kind is unclassified unless the offending exception chain happens to carry a classified provider failure of its own, in which case that kind is what retry and fail-over decisions use while the error code still names the breach. A correct adapter still has to normalize its own failures; the enforcement only guarantees that a defective one leaves an honest unpriced row instead of an exception carrying neither a kind nor a record of the call.
/// </para>
/// <para>
/// The cancellation sentence above is exact, and an implementation that wraps an <see cref="OperationCanceledException" /> inside another exception rather than raising it breaks it. The governed caller recognizes cancellation by the exception it is handed, so a wrapped one is not cancellation to it: it is a breach, and during host shutdown that turns what should be a clean drain into an attempt that fails and dead-letters, blamed on the adapter. Raise the cancellation; do not hide it.
/// </para>
/// </remarks>
public interface IAiModelClient
{
    /// <summary>
    /// Completes a chat request through the configured model provider.
    /// </summary>
    /// <remarks>
    /// Implementations must not log rendered prompt text, tool arguments containing user data, provider credentials or raw provider responses. There is no opt-in that relaxes this: the guarantee is that an implementation has no output sink at all, which <c>ModelGatewayLoggingGuardTests</c> asserts over the directories holding this contract and its adapters. See the Logging section of <c>docs/security-model.md</c>.
    /// If a provider returns usage metadata, implementations must preserve it in the response without inventing token counts.
    /// </remarks>
    Task<AiModelResponse> CompleteAsync(
        AiModelRequest request,
        CancellationToken cancellationToken);
}
