using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Jobs.Testing;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Investigation;

internal sealed class PostgresTriageToolResultCommitter(
    PostgresDataSourceProvider dataSourceProvider,
    ITriageToolResultCommitFaultInjector faultInjector,
    TimeProvider timeProvider) : ITriageToolResultCommitter
{
    public Task<TriageArtifact> CommitSucceededAsync(
        TriageToolResultCommitRequest request,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "commit triage tool result",
            () => CommitSucceededTransactionAsync(request, cancellationToken));

    private async Task<TriageArtifact> CommitSucceededTransactionAsync(TriageToolResultCommitRequest request, CancellationToken cancellationToken)
    {
        var createdAtUtc = timeProvider.GetUtcNow();
        var artifact = new TriageArtifact(
            Guid.NewGuid(),
            request.Job.Id,
            request.Job.Attempt,
            ArtifactKind.ToolResult,
            "tool:" + request.ToolName,
            request.Output.Clone(),
            request.ContentHash,
            createdAtUtc);

        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureActiveOwnershipAsync(connection, transaction, request.Job, createdAtUtc, cancellationToken);
            foreach (var additionalArtifact in request.AdditionalArtifacts ?? [])
            {
                ValidateAdditionalArtifact(request, additionalArtifact);
                await InsertArtifactAsync(connection, transaction, additionalArtifact, cancellationToken);
            }

            await InsertArtifactAsync(connection, transaction, artifact, cancellationToken);
            await faultInjector.AfterArtifactInsertedAsync(cancellationToken);
            await InsertToolResultEventAsync(connection, transaction, request, artifact, createdAtUtc, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return artifact;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task EnsureActiveOwnershipAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TriageJob job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM incidentcompass.triage_jobs
            WHERE id = @job_id AND status = 'Processing' AND attempt = @attempt
              AND locked_by = @worker_id AND locked_until_utc > @now;
            """, connection, transaction);
        command.AddParameter("job_id", job.Id);
        command.AddParameter("attempt", job.Attempt);
        command.AddParameter("worker_id", job.LockedBy ?? throw new InvalidOperationException("Tool result requires a claimed job owner."));
        command.AddParameter("now", now);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new InvalidOperationException($"Triage job '{job.Id}' is no longer owned by attempt {job.Attempt}.");
        }
    }

    private static void ValidateAdditionalArtifact(
        TriageToolResultCommitRequest request,
        TriageArtifact artifact)
    {
        if (artifact.JobId != request.Job.Id || artifact.Attempt != request.Job.Attempt)
        {
            throw new InvalidOperationException("Additional tool artifacts must belong to the current job attempt.");
        }

        if (artifact.Kind == ArtifactKind.ToolResult)
        {
            throw new InvalidOperationException("Additional tool artifacts must not use ToolResult kind.");
        }
    }

    private static async Task InsertArtifactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TriageArtifact artifact,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES (
                @id, @job_id, @attempt, @kind, @domain_ref, @redacted_payload, @content_hash, @created_at_utc);
            """, connection, transaction);

        command.AddParameter("id", artifact.Id);
        command.AddParameter("job_id", artifact.JobId);
        command.AddParameter("attempt", artifact.Attempt);
        command.AddParameter("kind", artifact.Kind.ToDbString());
        command.AddParameter("domain_ref", artifact.DomainRef);
        command.AddJsonbParameter("redacted_payload", artifact.RedactedPayload.GetRawText());
        command.AddParameter("content_hash", artifact.ContentHash);
        command.AddParameter("created_at_utc", artifact.CreatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertToolResultEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TriageToolResultCommitRequest request,
        TriageArtifact artifact,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.triage_ledger (
                fault_id, job_id, attempt, event_type, role, tool_name, rationale,
                decision, decision_reason, tool_status, payload_ref, config_hash, created_at_utc)
            VALUES (
                @fault_id, @job_id, @attempt, 'ToolResult', @role, @tool_name, @rationale,
                NULL, NULL, @tool_status, @payload_ref, @config_hash, @created_at_utc);
            """, connection, transaction);

        command.AddParameter("fault_id", request.Job.FaultId);
        command.AddParameter("job_id", request.Job.Id);
        command.AddParameter("attempt", request.Job.Attempt);
        command.AddParameter("role", request.Role);
        command.AddParameter("tool_name", request.ToolName);
        command.AddParameter("rationale", request.Rationale);
        command.AddParameter("tool_status", TriageLedgerToolStatus.Succeeded.ToDbString());
        command.AddParameter("payload_ref", "artifact:" + artifact.Id);
        command.AddParameter("config_hash", request.Job.ConfigHash);
        command.AddParameter("created_at_utc", createdAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }


}
