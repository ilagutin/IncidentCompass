using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Everything one remediation pass runs on. All of it is durable state the backend read, and none of
/// it is model-selected.
/// </summary>
/// <param name="Job">The triage job that produced the report, and the subject of the attempt budget.</param>
/// <param name="Fault">The fault, which supplies the tenant and the service the checkout is selected by.</param>
/// <param name="Configuration">
/// The job's own rehydrated configuration snapshot. It supplies the route, the current release and
/// the reprompt allowance, so a pass runs under the configuration the report was produced under
/// rather than under whatever is current now.
/// </param>
/// <param name="RouteId">Which configured route answers the request.</param>
/// <param name="Instructions">
/// The operator-authored system instructions for the pass. Backend text: it never comes from a
/// model, a signal or a tool.
/// </param>
/// <param name="ReportId">The grounded report the change is derived from.</param>
/// <param name="Report">That report's content.</param>
/// <param name="SourceEvidence">
/// The cited source-code artifacts the investigation actually read. A pass with none of these is
/// refused: a diff written against files nobody looked at is not grounded in anything.
/// </param>
/// <param name="StartedAtUtc">
/// When the pass started, which is what the attempt wall-clock bound is measured from. The token
/// budget stays the job attempt's own, so remediation spends the same incident's allowance rather
/// than a second one nobody bounded.
/// </param>
internal sealed record RemediationRequest(
    TriageJob Job,
    Fault Fault,
    TriageConfiguration Configuration,
    string RouteId,
    string Instructions,
    Guid ReportId,
    TriageReport Report,
    IReadOnlyList<TriageArtifact> SourceEvidence,
    DateTimeOffset StartedAtUtc);
