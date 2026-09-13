namespace IncidentCompass.Application.Core.Embeddings;

/// <summary>
/// Carries a single text embedding request to an embedding adapter.
/// </summary>
/// <remarks>
/// Application workflows must apply length limits and authorization before creating this request; adapters must treat <see cref="Input" /> as sensitive content and avoid logging it.
/// </remarks>
/// <param name="Input">The validated text that will be embedded.</param>
/// <param name="Model">The resolved provider embedding model name.</param>
/// <param name="CorrelationId">The optional application correlation identifier to pass through to provider metadata when supported.</param>
/// <param name="Kind">Whether <see cref="Input" /> is a search query or a passage stored in the corpus. It is required and has no default, because a query embedded as a passage is the mistake a model that distinguishes the two punishes without failing. The caller states the purpose only; the adapter, not the caller, owns whatever model-specific form that purpose takes, such as a text prefix, and an adapter whose model makes no distinction ignores it.</param>
/// <param name="ProviderId">The optional configured provider identifier after route resolution. It carries the route's provider identity only; adapters resolve that identity to an endpoint and a credential on their own side, so no credential ever travels on this contract.</param>
public sealed record EmbeddingRequest(
    string Input,
    string Model,
    string? CorrelationId,
    EmbeddingInputKind Kind,
    string? ProviderId = null);
