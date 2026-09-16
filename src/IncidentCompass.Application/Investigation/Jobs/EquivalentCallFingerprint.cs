using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// The identity two calls share when they ask for the same thing within one attempt: a worker tool
/// call by its tool name and the canonical JSON of its validated, sanitized arguments, a delegate by
/// its role and its task with whitespace collapsed. Only the SHA-256 of that identity is kept, so the
/// tracker holds no argument or task text, and only <see cref="ShortHash"/> ever leaves the process.
/// </summary>
/// <param name="Key">The full lowercase SHA-256 hex of the identity; the tracker's dictionary key.</param>
internal readonly record struct EquivalentCallFingerprint(string Key)
{
    /// <summary>Characters of <see cref="Key"/> written to the ledger and logs.</summary>
    internal const int ShortHashLength = 16;

    private const string WorkerToolNamespace = "worker_tool";

    private const string DelegateNamespace = "delegate";

    /// <summary>A prefix of the hash that identifies the call in the ledger without carrying its text.</summary>
    public string ShortHash => Key[..ShortHashLength];

    public static EquivalentCallFingerprint ForWorkerTool(string toolName, JsonElement sanitizedArguments)
    {
        var canonicalArguments = CanonicalJsonSerializer.Canonicalize(JsonNode.Parse(sanitizedArguments.GetRawText()));
        return Create(WorkerToolNamespace, toolName, canonicalArguments);
    }

    public static EquivalentCallFingerprint ForDelegate(string roleName, string task)
    {
        var normalizedTask = string.Join(
            ' ',
            task.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return Create(DelegateNamespace, roleName, normalizedTask);
    }

    /// <summary>
    /// The namespace keeps a worker tool and a role that happen to share a name apart, and the
    /// canonical JSON string encoding of each part keeps the boundaries between parts unambiguous.
    /// </summary>
    private static EquivalentCallFingerprint Create(string kind, string name, string payload)
    {
        var identity = JsonSerializer.Serialize(new[] { kind, name, payload });
        return new EquivalentCallFingerprint(CanonicalJsonSerializer.ComputeSha256Hex(identity));
    }
}
