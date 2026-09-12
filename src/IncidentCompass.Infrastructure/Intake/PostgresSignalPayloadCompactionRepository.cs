using IncidentCompass.Application.Intake.Retention;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Intake;

internal sealed class PostgresSignalPayloadCompactionRepository(
    PostgresDataSourceProvider dataSourceProvider) : ISignalPayloadCompactionRepository
{
    public Task<int> CompactAsync(
        DateTimeOffset receivedBeforeUtc,
        int maxRows,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "compact aged signal payloads",
            () => CompactCoreAsync(receivedBeforeUtc, maxRows, cancellationToken));

    private async Task<int> CompactCoreAsync(
        DateTimeOffset receivedBeforeUtc,
        int maxRows,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);

        // One statement, so a run is atomic and an interrupted run leaves no half-compacted row.
        //
        // `attributes` and `body` are the only columns emptied. Everything the pipeline derived from
        // the raw payload before persisting it - fingerprint, service, environment, severity, error
        // type, summary, trace ids, the fault link - is left alone, and the row itself is never
        // deleted because `faults.trigger_signal_id` references it.
        //
        // `payload_compacted_at_utc` is what makes the result legible afterwards: an emptied payload
        // and a payload that arrived empty are the same '{}' bytes, so the marker column, not the
        // payload, is the claim that retention ran here.
        //
        // Aging is by `received_at_utc`, which intake wrote, not by `observed_at_utc`, which arrives
        // inside the signal and would let a source choose its own retention.
        //
        // The candidate select is ordered, bounded and SKIP LOCKED, so two runs never wait on each
        // other and neither can be starved by the other's locks. `payload_compacted_at_utc IS NULL`
        // makes the whole thing idempotent: a compacted row is no longer a candidate.
        await using var command = new NpgsqlCommand(
            """
            WITH candidate AS (
                SELECT id
                FROM incidentcompass.signals
                WHERE payload_compacted_at_utc IS NULL
                  AND received_at_utc < @received_before
                ORDER BY received_at_utc, id
                LIMIT @max_rows
                FOR UPDATE SKIP LOCKED
            )
            UPDATE incidentcompass.signals AS signal
            SET attributes = '{}'::jsonb,
                body = '{}'::jsonb,
                payload_compacted_at_utc = clock_timestamp()
            FROM candidate
            WHERE signal.id = candidate.id;
            """,
            connection);
        command.AddParameter("received_before", receivedBeforeUtc);
        command.AddParameter("max_rows", maxRows);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
