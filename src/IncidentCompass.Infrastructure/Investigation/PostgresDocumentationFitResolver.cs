using IncidentCompass.Application.Investigation.Reports;

namespace IncidentCompass.Infrastructure.Investigation;

internal sealed class PostgresDocumentationFitResolver
{
    private const string MultipleCurrentDocumentsLimitation =
        "Multiple current documents were cited; their compatibility requires operator review.";
    private const string UnassessableDocumentLimitation =
        "Cited documentation is unversioned or service-mismatched, so its currentness cannot be assessed.";

    public TriageReport ValidateAndApply(
        TriageReport report,
        IReadOnlyList<GroundedReportEvidence> evidence)
    {
        var documents = evidence
            .Where(static item => item.MemoryItemId is not null && item.DocumentationStatus is not null)
            .GroupBy(static item => item.MemoryItemId!.Value)
            .Select(static group => group.First())
            .ToArray();
        var documentationFit = DocumentationFitCalculator.Resolve(
            documents.Select(static item => item.DocumentationStatus));
        if (report.DocumentationFit != documentationFit)
        {
            throw new TriageReportValidationException(DocumentationFitDiagnostics.Mismatch(documentationFit));
        }

        return documentationFit switch
        {
            DocumentationFitStatus.MultipleCurrentDocuments => AddLimitation(report, MultipleCurrentDocumentsLimitation),
            DocumentationFitStatus.Missing when documents.Any(static item =>
                item.DocumentationStatus is "Unversioned" or "ServiceMismatch") =>
                AddLimitation(report, UnassessableDocumentLimitation),
            _ => report
        };
    }

    private static TriageReport AddLimitation(TriageReport report, string limitation)
    {
        return report.Limitations.Contains(limitation, StringComparer.Ordinal)
            ? report
            : report with { Limitations = [.. report.Limitations, limitation] };
    }
}
