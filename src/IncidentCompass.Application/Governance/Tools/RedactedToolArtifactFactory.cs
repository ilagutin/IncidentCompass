using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.Redaction;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Governance.Tools;

/// <summary>
/// The single place where tool-produced text becomes durable triage state. Everything a worker tool
/// reads from memory, source or a ticket provider passes through here before it is written to
/// <c>triage_artifacts.redacted_payload</c>, and therefore before any later prompt built from that
/// column.
/// <para>
/// Redaction runs on the parsed JSON tree, not on serialized text: string values are rewritten and
/// the object/array shape is rebuilt node by node, so a redacted payload is still a valid JSON
/// document with the same keys. Value kinds survive except where a property name itself looks like a
/// secret holder, which replaces the value with the string <c>[REDACTED]</c> whatever its original
/// kind was. The content hash is computed over the redacted form, which is the form that is stored,
/// so the hash keeps describing the row.
/// </para>
/// </summary>
internal static class RedactedToolArtifactFactory
{
    /// <summary>
    /// Redacts a draft and records, in <see cref="TriageArtifact.RedactionApplied" />, whether the
    /// pass actually removed anything.
    /// <para>
    /// This is the only moment at which that question can be answered honestly. Afterwards only the
    /// redacted document survives, and a value the redactor replaced is byte-identical to connector
    /// text that already contained the literal <c>[REDACTED]</c>, so a marker derived later by
    /// reading the stored payload would be one that the author of a ticket or a source file could
    /// raise at will. Comparing against the pre-redaction document has neither failure: text that
    /// already held the literal survives the pass unchanged and is correctly reported as untouched,
    /// and a value the redactor really did remove cannot be made to look untouched, because the
    /// comparison is against the input rather than against a pattern in the output.
    /// </para>
    /// </summary>
    public static TriageArtifact Create(
        TriageJob job,
        ToolArtifactDraft draft,
        RedactionSettings redaction,
        DateTimeOffset createdAtUtc)
    {
        var redacted = SecretRedactor.RedactJsonNode(draft.Payload, redaction);
        var canonical = CanonicalJsonSerializer.Canonicalize(redacted);
        return new TriageArtifact(
            draft.Id,
            job.Id,
            job.Attempt,
            draft.Kind,
            draft.DomainRef,
            CanonicalJsonSerializer.ToElement(redacted),
            CanonicalJsonSerializer.ComputeSha256Hex(canonical),
            createdAtUtc)
        {
            RedactionApplied = !JsonNode.DeepEquals(draft.Payload, redacted)
        };
    }

    /// <summary>
    /// Redacts the model-visible tool output. The same connector text reaches the model twice: once
    /// as this turn's tool message and again through the stored <c>ToolResult</c> artifact, so both
    /// have to be the redacted form or the immediate turn would leak what the durable row hides.
    /// <para>
    /// The outcome is returned beside the document for the same reason
    /// <see cref="Create" /> records one: the <c>ToolResult</c> artifact the committer builds from
    /// this output is citable evidence, so the report marker has to be able to speak for it, and
    /// this is the last point at which the pre-redaction document still exists to compare against.
    /// A <c>ToolResult</c> row left silent would let the model choose whether the marker appears, by
    /// citing the tool result instead of the per-item artifact holding the same redacted text.
    /// </para>
    /// </summary>
    public static RedactedToolOutput RedactOutput(JsonElement output, RedactionSettings redaction)
    {
        var parsed = JsonNode.Parse(output.GetRawText());
        if (parsed is null)
        {
            // A JSON null output has no values to rewrite, so the pass is a no-op rather than an
            // unrecorded outcome: false is the honest answer, not a guess.
            return new RedactedToolOutput(output.Clone(), false);
        }

        var redacted = SecretRedactor.RedactJsonNode(parsed, redaction);
        return new RedactedToolOutput(
            CanonicalJsonSerializer.ToElement(redacted),
            !JsonNode.DeepEquals(parsed, redacted));
    }

    /// <summary>
    /// Redacts a free-text reason that a tool produced, such as a failed execution's error message.
    /// No shipped tool builds that message out of connector text today, but the message is written
    /// verbatim into the model's tool message and into <c>triage_ledger.rationale</c>, so it is the
    /// one remaining way a tool could hand connector text to both surfaces without meeting the
    /// redactor. It goes through the same rules as a payload rather than a private path.
    /// </summary>
    public static string RedactReason(string reason, RedactionSettings redaction) =>
        // RedactText returns null only for a null input, so the fallback is unreachable today; it is
        // written as the empty string rather than as the original text so that a later change to the
        // redactor's contract fails closed instead of passing the raw reason through.
        SecretRedactor.RedactText(reason, redaction) ?? string.Empty;
}
