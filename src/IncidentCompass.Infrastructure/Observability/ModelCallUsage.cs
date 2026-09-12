namespace IncidentCompass.Infrastructure.Observability;

/// <summary>
/// One <c>ModelCall</c> ledger row as the cost rollup reads it.
/// </summary>
/// <param name="Provider">The adapter that answered, which is not on its own a payer.</param>
/// <param name="ProviderId">The configured provider entry the route named, or <see langword="null"/> when the row does not say.</param>
/// <param name="Model">The model that answered.</param>
/// <param name="UsageSource">Where the token counts came from: <c>provider</c>, <c>estimate</c> or <c>unknown</c>.</param>
/// <param name="InputTokens">The recorded input tokens.</param>
/// <param name="OutputTokens">The recorded output tokens.</param>
/// <param name="TotalTokens">The recorded total tokens.</param>
internal sealed record ModelCallUsage(
    string Provider,
    string? ProviderId,
    string Model,
    string UsageSource,
    int InputTokens,
    int OutputTokens,
    int TotalTokens);
