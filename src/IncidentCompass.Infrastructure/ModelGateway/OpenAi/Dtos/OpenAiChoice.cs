using System.Text.Json.Serialization;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi.Dtos;

internal sealed record OpenAiChoice(
    [property: JsonPropertyName("message")] OpenAiResponseMessage? Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);
