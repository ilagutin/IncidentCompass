using System.Text.Json;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <remarks>
/// <c>RedactionApplied</c> is whether the redactor removed anything from <c>Output</c> on its way
/// here. The committer writes it onto the <c>ToolResult</c> artifact it creates from that output,
/// because that artifact is citable evidence and the caller is the last holder of the pre-redaction
/// document. It travels on the request rather than being recomputed downstream: after redaction a
/// replaced value and connector text that already spelled out the literal are the same bytes, so the
/// answer is only available where both documents are still in hand.
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
