using IncidentCompass.Application.Investigation.Retention;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Investigation;

internal sealed class PostgresAttemptArtifactRetentionRepository(
    PostgresDataSourceProvider dataSourceProvider) : IAttemptArtifactRetentionRepository
{
    public Task<int> ReapAsync(
        DateTimeOffset createdBeforeUtc,
        int maxRows,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "reap stale attempt triage artifacts",
            () => ReapCoreAsync(createdBeforeUtc, maxRows, cancellationToken));

    private async Task<int> ReapCoreAsync(
        DateTimeOffset createdBeforeUtc,
        int maxRows,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);

        // One statement, so a run is atomic and safe to interrupt: it either deletes its whole batch
        // or none of it, and the next run picks up from the same predicate. `LIMIT` bounds what a run
        // deletes, not what it reads to find those rows; the measured plans are in
        // `infra/postgres/init/028-signal-payload-and-artifact-retention.sql`.
        //
        // The exclusions, and why each one is here rather than left to the database:
        //
        //  - `attempt IS NOT NULL` keeps every job-level row. NULL is the deliberate sentinel
        //    documented in the `triage_artifacts` DDL for intake artifacts that stay valid across
        //    retries, so a job-level row is never reapable at any age.
        //  - `attempt <> job.attempt` is the whole of what "stale attempt" can mean here. A retry
        //    that does not consume an attempt reuses the number, so a run that went wrong and the
        //    run that replaced it share an attempt and both are kept. That is the safe direction.
        //    It is also what keeps the current-attempt tool output that post-report action history
        //    reads back after a job succeeds.
        //  - `kind NOT IN ('ProposedAction', 'ActionResult')` keeps the two artifact kinds that are
        //    an audit record of a governed external action rather than working evidence.
        //    `ProposedAction` additionally has a trigger that rejects DELETE outright, which would
        //    abort the whole batch, so excluding it by kind is what keeps the operation bounded
        //    rather than fragile.
        //  - the `triage_evidence` exclusion keeps anything a published report cites. That column is
        //    a foreign key with no ON DELETE, so a cited row would throw.
        //  - the `action_approvals` exclusion keeps the proposal artifact of an approval row, which
        //    is likewise a foreign key.
        //  - the `action_approval_provenance` exclusion is the one with no database backing at all.
        //    `source_id` there is polymorphic over reports and artifacts, so it carries no foreign
        //    key: deleting a cited row would succeed silently and leave a sealed, immutable
        //    provenance chain pointing at nothing. This predicate is the only guard that exists.
        //
        // What the concurrency primitives here actually cover, which is narrower than the shape of
        // the statement suggests:
        //
        //  - `SKIP LOCKED` covers one thing. A candidate another transaction already holds a row
        //    lock on is passed over rather than waited for, so two runs never block each other and
        //    neither can be starved by the other's locks.
        //  - the three `NOT EXISTS` exclusions are not locks. Each is evaluated once, against this
        //    statement's snapshot, over rows this statement does not lock. Nothing stops a citation
        //    being committed after that snapshot is taken and before the DELETE reaches the row.
        //  - for `triage_evidence` and `action_approvals` the foreign key is the backstop, and it is
        //    checked at a later snapshot than the exclusion was. A citation committed inside that
        //    window therefore aborts the whole batch with a foreign-key violation and rolls it back;
        //    it does not skip the one row. That direction is safe rather than lossy - nothing is
        //    deleted, and the next run sees the citation and excludes the row - but it is a batch
        //    failure, not a skip.
        //  - `action_approval_provenance` has no foreign key, so the same window would leave a
        //    silent orphan instead of an aborted batch. What closes it today is not the database but
        //    `PostgresActionProvenanceGrounder`, which refuses to ground a provenance row on an
        //    artifact that is not already persisted evidence of the origin report. So an artifact
        //    this statement's snapshot sees as uncited cannot acquire a provenance row without
        //    acquiring a `triage_evidence` row first, and that one has the foreign key. It is
        //    unreachable because of how the only writer behaves, not because it cannot happen: a
        //    future writer that grounded provenance on an uncited artifact would reopen it.
        //
        // Idempotence needs nothing extra: a reaped row is gone from the candidate set.
        await using var command = new NpgsqlCommand(
            """
            WITH candidate AS (
                SELECT artifact.id
                FROM incidentcompass.triage_artifacts AS artifact
                JOIN incidentcompass.triage_jobs AS job ON job.id = artifact.job_id
                WHERE artifact.attempt IS NOT NULL
                  AND artifact.attempt <> job.attempt
                  AND artifact.created_at_utc < @created_before
                  AND artifact.kind NOT IN ('ProposedAction', 'ActionResult')
                  AND NOT EXISTS (
                      SELECT 1
                      FROM incidentcompass.triage_evidence AS evidence
                      WHERE evidence.artifact_id = artifact.id)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM incidentcompass.action_approvals AS approval
                      WHERE approval.proposal_artifact_id = artifact.id)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM incidentcompass.action_approval_provenance AS provenance
                      WHERE provenance.source_type = 'artifact'
                        AND provenance.source_id = artifact.id)
                ORDER BY artifact.created_at_utc, artifact.id
                LIMIT @max_rows
                FOR UPDATE OF artifact SKIP LOCKED
            )
            DELETE FROM incidentcompass.triage_artifacts AS target
            USING candidate
            WHERE target.id = candidate.id;
            """,
            connection);
        command.AddParameter("created_before", createdBeforeUtc);
        command.AddParameter("max_rows", maxRows);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
