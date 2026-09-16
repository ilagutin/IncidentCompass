using System.Text.Json.Serialization;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi.Dtos;

/// <summary>
/// The incremental part of a streamed choice. Reasoning deltas are not read: an event that carries
/// only reasoning still counts as output for the stall limits, but its text is never kept.
/// </summary>
internal sealed record OpenAiChunkDelta(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<OpenAiToolCallDelta?>? ToolCalls);
