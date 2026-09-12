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
/// <param name="ProviderId">The optional configured provider identifier after route resolution. It carries the route's provider identity only; adapters resolve that identity to an endpoint and a credential on their own side, so no credential ever travels on this contract.</param>
public sealed record EmbeddingRequest(
    string Input,
    string Model,
    string? CorrelationId,
    string? ProviderId = null);
