using IncidentCompass.Application.Intake.Artifacts;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Intake;

internal sealed class PostgresTriageArtifactRepository(PostgresDataSourceProvider dataSourceProvider, PostgresIntakeTransactionContext transactionContext) : ITriageArtifactRepository
{
    public async Task InsertAsync(TriageArtifact artifact, CancellationToken cancellationToken)
    {
        await using var lease = await transactionContext.OpenConnectionAsync(dataSourceProvider, cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc,
                redaction_applied)
            VALUES (
                @id, @job_id, @attempt, @kind, @domain_ref, @redacted_payload, @content_hash, @created_at_utc,
                @redaction_applied);
            """, lease.Connection, lease.Transaction);

        AddArtifactParameters(command, artifact);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceJobLevelAsync(TriageArtifact artifact, CancellationToken cancellationToken)
    {
        if (artifact.Attempt is not null)
        {
            throw new ArgumentException("Only job-level artifacts can be replaced.", nameof(artifact));
        }

        await using var lease = await transactionContext.OpenConnectionAsync(dataSourceProvider, cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc,
                redaction_applied)
            VALUES (
                @id, @job_id, @attempt, @kind, @domain_ref, @redacted_payload, @content_hash, @created_at_utc,
                @redaction_applied)
            ON CONFLICT (id)
            DO UPDATE SET
                domain_ref = EXCLUDED.domain_ref,
                redacted_payload = EXCLUDED.redacted_payload,
                content_hash = EXCLUDED.content_hash,
                created_at_utc = EXCLUDED.created_at_utc,
                redaction_applied = EXCLUDED.redaction_applied;
            """, lease.Connection, lease.Transaction);

        AddArtifactParameters(command, artifact);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddArtifactParameters(NpgsqlCommand command, TriageArtifact artifact)
    {
        command.AddParameter("id", artifact.Id);
        command.AddParameter("job_id", artifact.JobId);
        command.AddParameter("attempt", artifact.Attempt);
        command.AddParameter("kind", artifact.Kind.ToDbString());
        command.AddParameter("domain_ref", artifact.DomainRef);
        command.AddJsonbParameter("redacted_payload", artifact.RedactedPayload.GetRawText());
        command.AddParameter("content_hash", artifact.ContentHash);
        command.AddParameter("created_at_utc", artifact.CreatedAtUtc);
        command.AddParameter("redaction_applied", artifact.RedactionApplied);
    }
}
