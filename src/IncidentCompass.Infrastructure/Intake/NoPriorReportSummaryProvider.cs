using IncidentCompass.Application.Intake.Artifacts;

namespace IncidentCompass.Infrastructure.Intake;

// Report publication writes a `triage_reports` row, but recurrence grounding still needs the
// richer report-summary lookup that this adapter deliberately does not implement yet.
internal sealed class NoPriorReportSummaryProvider : IPriorReportSummaryProvider
{
    public Task<PriorReportSummary?> FindLatestAsync(Guid faultId, CancellationToken cancellationToken) =>
        Task.FromResult<PriorReportSummary?>(null);
}
