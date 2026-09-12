using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure.Postgres;

namespace IncidentCompass.Infrastructure.Governance;

internal sealed class PostgresTriageLedgerWriter(
    PostgresDataSourceProvider dataSourceProvider,
    TimeProvider timeProvider) : ITriageLedgerWriter
{
    public Task<TriageLedgerEntry> AppendAsync(
        TriageLedgerAppendRequest request,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "append triage ledger entry",
            () => AppendTransactionAsync(request, cancellationToken));

    public Task<IReadOnlyList<TriageLedgerEntry>> AppendBatchAsync(
        IReadOnlyList<TriageLedgerAppendRequest> requests,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "append triage ledger entry batch",
            () => AppendBatchTransactionAsync(requests, cancellationToken));

    private async Task<TriageLedgerEntry> AppendTransactionAsync(TriageLedgerAppendRequest request, CancellationToken cancellationToken)
    {
        var createdAtUtc = timeProvider.GetUtcNow();
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var entry = await PostgresTriageLedgerEntryInserter.InsertAsync(
                connection,
                transaction,
                request,
                createdAtUtc,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return entry;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<IReadOnlyList<TriageLedgerEntry>> AppendBatchTransactionAsync(
        IReadOnlyList<TriageLedgerAppendRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        var createdAtUtc = timeProvider.GetUtcNow();
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var entries = new List<TriageLedgerEntry>(requests.Count);
            foreach (var request in requests)
            {
                entries.Add(await PostgresTriageLedgerEntryInserter.InsertAsync(
                    connection,
                    transaction,
                    request,
                    createdAtUtc,
                    cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);
            return entries;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

}
