namespace IncidentCompass.Application.Investigation.Reports.Redaction;

/// <summary>
/// What the redaction boundary recorded about one artifact a report cites: whether the redactor
/// removed anything from that artifact's payload on its way to durable state.
/// </summary>
/// <param name="ArtifactId">The cited artifact.</param>
/// <param name="RedactionApplied">
/// <see langword="true" /> when the redactor changed the payload, <see langword="false" /> when it
/// ran and changed nothing, and <see langword="null" /> when no boundary recorded an outcome for
/// that row. Only <see langword="true" /> is a claim that something was withheld; the other two are
/// deliberately different from each other and neither is evidence that nothing was.
/// </param>
public sealed record CitedEvidenceRedaction(Guid ArtifactId, bool? RedactionApplied);
