using System.Text.Json;
using System.Text.Json.Serialization;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi.Dtos;

/// <summary>
/// One server-sent <c>data</c> event of a streamed chat completion. <see cref="Error" /> stays a raw
/// element so a provider that sends it in an unexpected shape is still recognised as an error event
/// rather than rejected as malformed JSON.
/// </summary>
internal sealed record OpenAiChatCompletionChunk(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChunkChoice?>? Choices,
    [property: JsonPropertyName("usage")] OpenAiUsage? Usage,
    [property: JsonPropertyName("error")] JsonElement? Error);
