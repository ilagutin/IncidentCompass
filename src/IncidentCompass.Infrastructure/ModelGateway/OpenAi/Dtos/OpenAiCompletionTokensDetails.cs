using System.Text.Json.Serialization;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi.Dtos;

internal sealed record OpenAiCompletionTokensDetails(
    [property: JsonPropertyName("reasoning_tokens")] int? ReasoningTokens);
