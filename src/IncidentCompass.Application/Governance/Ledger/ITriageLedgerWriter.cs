using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Governance.Ledger;

public interface ITriageLedgerWriter
{
    Task<TriageLedgerEntry> AppendAsync(
        TriageLedgerAppendRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TriageLedgerEntry>> AppendBatchAsync(
        IReadOnlyList<TriageLedgerAppendRequest> requests,
        CancellationToken cancellationToken);
}
