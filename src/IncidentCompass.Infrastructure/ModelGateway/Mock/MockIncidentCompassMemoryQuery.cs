using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Memory;

namespace IncidentCompass.Infrastructure.ModelGateway.Mock;

internal static class MockIncidentCompassMemoryQuery
{
    /// <summary>
    /// The mock role's query is the backend's own fault query, read back out of the prompt: the
    /// service, the error type and the error message, with the summary standing in for a blank message,
    /// composed by <see cref="MemoryFaultQuery.Compose" />. That is also what the memory role
    /// instructions tell a real role to search with.
    /// <para>
    /// One case differs. The fault query leaves out a summary intake synthesized itself, which it
    /// recognizes by recomputing the synthesis from the signal's operation and route, and the prompt
    /// carries neither, so for a structured signal with an error type and no message the mock still
    /// sends that synthesized summary. The mock's query only decides admission; the band is decided
    /// against the backend's fault query either way.
    /// </para>
    /// </summary>
    public static string CreateSearchArguments(AiModelRequest request)
    {
        var prompt = request.Messages.LastOrDefault(static message => message.Role == AiMessageRole.User)?.Content ?? string.Empty;
        var query = MemoryFaultQuery.Compose(
            ReadPromptValue(prompt, "- service:"),
            ReadPromptValue(prompt, "- errorType:"),
            ReadPromptValue(prompt, "- errorMessage:"),
            ReadPromptValue(prompt, "- summary:"));

        return JsonSerializer.Serialize(new
        {
            query = string.IsNullOrWhiteSpace(query) ? "incident memory" : query
        });
    }

    private static string? ReadPromptValue(string prompt, string prefix)
    {
        foreach (var line in prompt.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return DecodePromptValue(line[prefix.Length..].Trim());
            }
        }

        return null;
    }

    /// <summary>
    /// The prompt writes every untrusted scalar as a JSON string literal, so the value arrives wrapped
    /// in quotes and with some of its characters still escaped. A letter of any Basic Multilingual
    /// Plane script now arrives as itself, but the quote, the backslash, every control character, the
    /// separators, the format characters and every supplementary-plane character do not, and the
    /// surrounding quotes are always there. A real model reads that literal and emits its query inside
    /// JSON tool arguments, where the remaining escapes decode again; the mock has to perform the same
    /// decode or the query would reach memory_search carrying the quotes and Latin <c>u0022</c>
    /// fragments, which count against lexical coverage. A value that is not a valid JSON string literal
    /// is used exactly as written.
    /// </summary>
    private static string DecodePromptValue(string value)
    {
        if (!value.StartsWith('"'))
        {
            return value;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(value) ?? value;
        }
        catch (JsonException)
        {
            return value;
        }
    }
}
