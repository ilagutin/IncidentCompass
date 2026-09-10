using System.Text.Json.Serialization;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Durable <c>ModelCall</c> triage-ledger rationale payload.
/// </summary>
/// <remarks>
/// The JSON property names, casing and order are a persisted contract: rows already written to
/// <c>incidentcompass.triage_ledger</c> and the hourly cost-rollup reader parse exactly these
/// names, so they are pinned with explicit <see cref="JsonPropertyNameAttribute"/> and
/// <see cref="JsonPropertyOrderAttribute"/> values rather than left to member-declaration order.
/// The payload carries bounded call metadata only - never rendered prompts, provider response
/// bodies, credentials or embedding vectors.
/// <para>
/// <c>RouteId</c> always names the route that was actually called. On a fail-over call that is the
/// fallback route, and <c>FallbackForRouteId</c> names the route it answered for; on every other
/// call the property is absent, so no already-written row changes shape.
/// </para>
/// <para>
/// <c>Provider</c> and <c>ProviderId</c> are two different facts and both are recorded.
/// <c>Provider</c> is the adapter that produced the answer, which is one string for every
/// OpenAI-compatible endpoint the host can reach; <c>ProviderId</c> is the entry in the triage
/// configuration's provider table that the called route named, which is what has its own endpoint,
/// its own credential and its own prices. Cost accounting therefore keys on <c>ProviderId</c>: two
/// configured providers answered by the same adapter are two payers and must not be added together.
/// The property is absent on a row written before it existed and on a call whose route named no
/// provider, so no already-written row changes shape and a reader has to decide what to do with a
/// row that names an adapter but no payer.
/// </para>
/// </remarks>
public sealed record ModelCallLedgerMetadata(
    [property: JsonPropertyName("kind"), JsonPropertyOrder(0)] string Kind,
    [property: JsonPropertyName("routeId"), JsonPropertyOrder(1)] string RouteId,
    [property: JsonPropertyName("model"), JsonPropertyOrder(2)] string Model,
    [property: JsonPropertyName("provider"), JsonPropertyOrder(3)] string Provider,
    [property: JsonPropertyName("usageSource"), JsonPropertyOrder(4)] string UsageSource,
    [property: JsonPropertyName("inputTokens"), JsonPropertyOrder(5)] int? InputTokens,
    [property: JsonPropertyName("outputTokens"), JsonPropertyOrder(6)] int? OutputTokens,
    [property: JsonPropertyName("totalTokens"), JsonPropertyOrder(7)] int? TotalTokens,
    [property: JsonPropertyName("durationMs"), JsonPropertyOrder(8)] long DurationMs,
    [property: JsonPropertyName("proposedToolCallCount"), JsonPropertyOrder(9)] int ProposedToolCallCount,
    [property: JsonPropertyName("callId"), JsonPropertyOrder(10)] Guid CallId,
    [property: JsonPropertyName("outcome"), JsonPropertyOrder(11)] string Outcome,
    [property: JsonPropertyName("errorCode"), JsonPropertyOrder(12)] string? ErrorCode,
    [property: JsonPropertyName("reasoningTokens"), JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ReasoningTokens = null,
    [property: JsonPropertyName("fallbackForRouteId"), JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FallbackForRouteId = null,
    [property: JsonPropertyName("providerId"), JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderId = null);
