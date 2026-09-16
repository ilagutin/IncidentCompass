using System.Text.Json.Serialization;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi.Dtos;

internal sealed record OpenAiStreamOptions(
    [property: JsonPropertyName("include_usage")] bool IncludeUsage);
