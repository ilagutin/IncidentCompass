using System.Text;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi.Dtos;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi;

/// <summary>
/// One tool call being assembled from the fragments a stream delivers for its index.
/// </summary>
/// <remarks>
/// The id, the type and the function name are taken from the first fragment that carries each and
/// may be repeated unchanged later; a fragment that names a different value for any of them does not
/// merge. Argument pieces are appended in arrival order. Whether the assembled call is a valid one is
/// decided afterwards by the same response mapping a non-streamed answer goes through.
/// </remarks>
internal sealed class OpenAiStreamingToolCallBuilder(string id)
{
    private readonly StringBuilder arguments = new();
    private string? type;
    private string? name;
    private bool hasArguments;

    /// <summary>Merges a fragment for this call's index.</summary>
    /// <returns><see langword="false" /> when the fragment contradicts what the call already holds.</returns>
    public bool TryMerge(OpenAiToolCallDelta fragment)
    {
        if (!string.IsNullOrEmpty(fragment.Id) && !string.Equals(fragment.Id, id, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryTake(ref type, fragment.Type) || !TryTake(ref name, fragment.Function?.Name))
        {
            return false;
        }

        if (fragment.Function?.Arguments is { } piece)
        {
            arguments.Append(piece);
            hasArguments = true;
        }

        return true;
    }

    public OpenAiToolCall Build()
    {
        return new OpenAiToolCall(
            id,
            type,
            new OpenAiToolCallFunction(name, hasArguments ? arguments.ToString() : null));
    }

    private static bool TryTake(ref string? current, string? incoming)
    {
        if (string.IsNullOrEmpty(incoming))
        {
            return true;
        }

        if (current is null)
        {
            current = incoming;
            return true;
        }

        return string.Equals(current, incoming, StringComparison.Ordinal);
    }
}
