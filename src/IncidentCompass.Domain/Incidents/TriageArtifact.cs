using System.Text.Json;

namespace IncidentCompass.Domain.Incidents;

public sealed record TriageArtifact(
    Guid Id,
    Guid JobId,
    int? Attempt,
    ArtifactKind Kind,
    string? DomainRef,
    JsonElement RedactedPayload,
    string ContentHash,
    DateTimeOffset CreatedAtUtc)
{
    /// <summary>
    /// Whether the redactor removed anything from this artifact's payload on its way to durable
    /// state, recorded by the boundary that ran the redaction rather than read back out of the
    /// stored bytes. <see langword="null" /> means no boundary recorded an outcome for this row and
    /// is not the same claim as <see langword="false" />.
    /// <para>
    /// It is an <c>init</c> property rather than a positional member because most artifact writers
    /// have nothing to say here: only a writer that holds the pre-redaction document can answer, and
    /// a writer that cannot answer should leave the row silent instead of being forced to guess.
    /// </para>
    /// </summary>
    public bool? RedactionApplied { get; init; }
}
