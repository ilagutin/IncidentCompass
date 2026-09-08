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
    [property: JsonPropertyName("errorCode"), JsonPropertyOrder(12)] string? ErrorCode);
