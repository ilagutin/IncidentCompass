namespace IncidentCompass.Application.Investigation.Reports;

public sealed record TriageReport(
    TriageReportStatus Status,
    string Summary,
    string Classification,
    string Confidence,
    IReadOnlyList<TriageReportEvidenceReference> Evidence,
    IReadOnlyList<string> Limitations,
    string RecommendedNextAction)
{
    public DocumentationFitStatus DocumentationFit { get; init; } = DocumentationFitStatus.Missing;

    /// <summary>
    /// Whether the backend wrote this report itself rather than publishing a model's
    /// <c>publish_report</c>. Only <c>NoProgressTerminationReport.Create</c> sets it, and the
    /// repository turns it into the <c>backend_authored:</c> prefix of the <c>ReportPublished</c>
    /// ledger rationale, the durable authorship marker.
    /// </summary>
    public bool BackendAuthored { get; init; }
}
