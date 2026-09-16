using System.Text.Json.Serialization;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi.Dtos;

internal sealed record OpenAiChunkChoice(
    [property: JsonPropertyName("index")] int? Index,
    [property: JsonPropertyName("delta")] OpenAiChunkDelta? Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);
