using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IncidentCompass.Application.Core.Serialization;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// The identity of a result for repetition and progress: what it says, not which call said it.
/// </summary>
/// <remarks>
/// <para>
/// A stored content hash is not that identity. Every artifact gets a fresh id when it is drafted, and
/// tools name those ids in their own output (<c>memory_search</c>, <c>ticket_search</c> and
/// <c>source_lookup</c> put an <c>artifactId</c> on every match), and a worker output echoes them. Two
/// identical searches finding the same matches would therefore always hash differently.
/// </para>
/// <para>
/// The definition: take the redacted payload, and inside every string replace each artifact id this
/// attempt created with the identity of the artifact it names, then hash the canonical JSON. A
/// draft-backed artifact's identity is the content hash of its redacted payload, which carries no id;
/// a <c>ToolResult</c> or <c>WorkerOutput</c> artifact's identity is this identity of its own payload.
/// So an id is replaced by what it points at, whether it stands alone or inside a reference such as
/// <c>artifact:&lt;id&gt;</c>, and a GUID that no artifact of this attempt carries (a memory item id,
/// a ticket number) is data and stays. Timestamps in a payload are connector data, not call metadata,
/// and stay too; nothing the backend stamps per call (creation time, attempt, job id) is part of any
/// payload hashed here.
/// </para>
/// </remarks>
internal static partial class EvidenceResultIdentity
{
    private const string ArtifactReferencePrefix = "artifact-content:";

    public static string Compute(JsonElement payload, IReadOnlyDictionary<string, string> artifactIdentities) =>
        Compute(JsonNode.Parse(payload.GetRawText()), artifactIdentities);

    public static string Compute(string json, IReadOnlyDictionary<string, string> artifactIdentities) =>
        Compute(JsonNode.Parse(json), artifactIdentities);

    private static string Compute(JsonNode? node, IReadOnlyDictionary<string, string> artifactIdentities)
    {
        var substituted = artifactIdentities.Count == 0 ? node : Substitute(node, artifactIdentities);
        return CanonicalJsonSerializer.ComputeSha256Hex(CanonicalJsonSerializer.Canonicalize(substituted));
    }

    private static JsonNode? Substitute(JsonNode? node, IReadOnlyDictionary<string, string> artifactIdentities)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                var copiedObject = new JsonObject();
                foreach (var property in jsonObject)
                {
                    copiedObject[property.Key] = Substitute(property.Value, artifactIdentities);
                }

                return copiedObject;
            case JsonArray jsonArray:
                var copiedArray = new JsonArray();
                foreach (var item in jsonArray)
                {
                    copiedArray.Add(Substitute(item, artifactIdentities));
                }

                return copiedArray;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var replaced = GuidPattern().Replace(
                    value.GetValue<string>(),
                    match => artifactIdentities.TryGetValue(match.Value, out var identity)
                        ? ArtifactReferencePrefix + identity
                        : match.Value);
                return JsonValue.Create(replaced);
            default:
                return node?.DeepClone();
        }
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();
}
