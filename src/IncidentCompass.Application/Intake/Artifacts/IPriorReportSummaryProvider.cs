namespace IncidentCompass.Application.Intake.Artifacts;

public interface IPriorReportSummaryProvider
{
    // Given a fault id (the recurrence_of target), returns the most recent prior report's
    // summary/limitations if one exists, else null. The shipped adapter is
    // NoPriorReportSummaryProvider, which always returns null: report persistence exists, but the
    // richer report-summary lookup does not. This port stays in place so grounded-facts assembly is
    // complete and testable and a real lookup only has to swap the adapter.
    Task<PriorReportSummary?> FindLatestAsync(Guid faultId, CancellationToken cancellationToken);
}
