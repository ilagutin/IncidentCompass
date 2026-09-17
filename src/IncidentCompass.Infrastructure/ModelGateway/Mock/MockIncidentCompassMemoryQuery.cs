using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;

namespace IncidentCompass.Infrastructure.ModelGateway.Mock;

internal static class MockIncidentCompassMemoryQuery
{
    public static string CreateSearchArguments(AiModelRequest request)
    {
        var prompt = request.Messages.LastOrDefault(static message => message.Role == AiMessageRole.User)?.Content ?? string.Empty;
        var query = string.Join(' ', new[]
        {
            ReadPromptValue(prompt, "- service:"),
            ReadPromptValue(prompt, "- summary:"),
            ReadPromptValue(prompt, "- errorType:"),
            ReadPromptValue(prompt, "- errorMessage:")
        }.Where(static value => !string.IsNullOrWhiteSpace(value)));

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
