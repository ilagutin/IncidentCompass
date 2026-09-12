using System.Text.Json;

namespace IncidentCompass.Application.Governance.Tools;

/// <summary>
/// The model-visible tool output after redaction, together with what the pass did to it.
/// </summary>
/// <param name="Output">The redacted document, in the canonical form that is also stored.</param>
/// <param name="RedactionApplied">
/// Whether the redactor changed anything. It is returned beside the document rather than derived
/// from it, because once the pass has run a value the redactor replaced and connector text that
/// already contained the literal <c>[REDACTED]</c> are the same bytes. The comparison is made
/// against the pre-redaction document, at the one moment both are in hand.
/// </param>
internal sealed record RedactedToolOutput(JsonElement Output, bool RedactionApplied);
