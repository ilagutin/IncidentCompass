using System.Text.Json;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <remarks>
/// <c>RedactionApplied</c> is whether the redactor removed anything anywhere in this tool call: from
/// <c>Output</c> on its way here, or from any of the <c>AdditionalArtifacts</c> built from the same
/// call - their payloads and their domain references alike. The committer writes it onto the
/// <c>ToolResult</c> artifact it creates from that output.
/// <para>
/// The wider meaning is the correct one because the two are a pair. The <c>ToolResult</c> row and the
/// per-item rows are both citable, they carry the same connector text and they ground equally well,
/// so a report's withholding sentence must not depend on which of them the model chose to cite. A
/// flag answering only for <c>Output</c> would break exactly that: a domain reference is redacted and
/// is not part of the output, so a match whose path held a credential would leave one row saying
/// <see langword="true" /> and the other <see langword="false" />, and citing the quiet one would
/// suppress the sentence. Answering for the call keeps the pairing whole.
/// </para>
/// <para>
/// It travels on the request rather than being recomputed downstream: after redaction a replaced
/// value and connector text that already spelled out the literal are the same bytes, so the answer is
/// only available where the pre-redaction documents are still in hand, which is the caller.
/// </para>
/// </remarks>
internal sealed record TriageToolResultCommitRequest(
    TriageJob Job,
    string Role,
    string ToolName,
    JsonElement Output,
    string ContentHash,
    string Rationale,
    bool RedactionApplied,
    IReadOnlyCollection<TriageArtifact>? AdditionalArtifacts = null);
