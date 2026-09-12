using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Everything durable a remediation pass needs, read back for one published report.
/// </summary>
/// <remarks>
/// It is read in one go rather than assembled from four ports by the caller, because the four
/// pieces are only meaningful together: a report belongs to one attempt of one job against one
/// fault, and the evidence is the evidence that report cites. Reading them separately would let a
/// caller pair a report with a different attempt's artifacts and never notice.
/// </remarks>
/// <param name="Job">The triage job whose attempt published the report. Its attempt owns the budget.</param>
/// <param name="Fault">The fault, which supplies the tenant and the service the checkout is selected by.</param>
/// <param name="Report">The published report, as it stands after publication settled its sentences.</param>
/// <param name="SourceEvidence">
/// The artifacts this report cites that came from the source-read boundary, in citation order.
/// Evidence the report cites from anywhere else is deliberately not here: a diff is written against
/// files, and a memory item or a ticket is not a file.
/// </param>
internal sealed record RemediationPassContext(
    TriageJob Job,
    Fault Fault,
    TriageReport Report,
    IReadOnlyList<TriageArtifact> SourceEvidence);
