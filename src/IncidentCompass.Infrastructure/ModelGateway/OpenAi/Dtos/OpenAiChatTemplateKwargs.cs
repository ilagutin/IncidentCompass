using System.Text.Json.Serialization;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi.Dtos;

internal sealed record OpenAiChatTemplateKwargs(
    [property: JsonPropertyName("enable_thinking")] bool EnableThinking);
