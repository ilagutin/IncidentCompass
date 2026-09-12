using System.Text.Json;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.PostReportActions.Testing;
using IncidentCompass.Application.Investigation.Jobs.Testing;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure.Governance.PostReportActions;
using IncidentCompass.Infrastructure.Postgres;
using Microsoft.Extensions.Logging;
using Npgsql;
namespace IncidentCompass.Infrastructure.Investigation;

internal sealed partial class PostgresTriageReportRepository(
    PostgresDataSourceProvider dataSourceProvider,
    ITriageReportFinalCommitFaultInjector faultInjector,
    ITriageReportPublicationIntentWriter publicationIntentWriter,
    ITriageReportPublicationIntentFaultInjector publicationIntentFaultInjector,
    PostgresReportEvidenceGrounder evidenceGrounder,
    PostgresDocumentationFitResolver documentationFitResolver,
    TimeProvider timeProvider,
    ILogger<PostgresTriageReportRepository> logger) : ITriageReportRepository
{
    public Task<Guid> PublishAsync(
        TriageJob job,
        string workerId,
        TriageReport report,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "publish triage report",
            () => PublishTransactionAsync(job, workerId, report, cancellationToken));
    private async Task<Guid> PublishTransactionAsync(
        TriageJob job,
        string workerId,
        TriageReport report,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await PostgresFaultTransactionLock.LockAsync(connection, transaction, job.FaultId, cancellationToken);
            var evidence = await evidenceGrounder.GroundAsync(connection, transaction, job, report.Evidence, cancellationToken);
            if (job.IsReTriage && !evidence.Any(evidenceItem => evidenceItem.Kind == "RecurrenceState"))
            {
                throw new TriageReportValidationException("A re-triage report must cite recurrence state evidence.");
            }
            report = documentationFitResolver.ValidateAndApply(report, evidence);
            var isMassIssue = await ReadIsMassIssueAsync(connection, transaction, job.Id, cancellationToken);
            var updated = await MarkJobSucceededAsync(connection, transaction, job, workerId, now, cancellationToken);
            if (!updated)
            {
                throw new InvalidOperationException($"Triage job '{job.Id}' could not be completed for attempt {job.Attempt}.");
            }
            var supersedesReportId = await PostgresReportLifecycleWriter.FindLatestReportForUpdateAsync(
                connection, transaction, job.FaultId, cancellationToken);
            if (job.IsReTriage && supersedesReportId != job.SupersedesReportId)
            {
                throw new InvalidOperationException($"Re-triage job '{job.Id}' no longer has its scheduled predecessor.");
            }
            // Derived here rather than accumulated during the run, and derived from the ledger
            // rather than from anything the orchestrator said: the ledger already holds, per call,
            // which provider and model actually answered.
            var modelProvenance = await PostgresReportModelProvenanceReader.ReadAsync(
                connection, transaction, job, cancellationToken);
            var reportId = await InsertReportAsync(
                connection, transaction, job, report, isMassIssue, supersedesReportId, modelProvenance, now, cancellationToken);
            await PostgresTriageEvidenceWriter.ReplaceAsync(connection, transaction, reportId, evidence, now, cancellationToken);
            await MarkFaultTerminalAsync(connection, transaction, job, report.Status, now, cancellationToken);
            await faultInjector.BeforeReportPublishedLedgerEventAsync(cancellationToken);
            await PostgresReportPublishedEventWriter.InsertAsync(connection, transaction, job, reportId, report.Summary, now, cancellationToken);
            if (report.Status == TriageReportStatus.Completed)
            {
                var context = await PostgresReportPublicationIntentWriter.ReadFaultContextAsync(
                    connection, transaction, job.FaultId, cancellationToken);
                await publicationIntentWriter.WriteAsync(
                    job, reportId, context.TenantId, context.ServiceName, context.Environment,
                    context.Severity, now,
                    (intent, token) => PostgresReportPublicationIntentWriter.InsertAsync(
                        connection, transaction, intent, token), cancellationToken);
                await publicationIntentFaultInjector.AfterIntentInsertedAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return reportId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<bool> MarkJobSucceededAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TriageJob job,
        string workerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE incidentcompass.triage_jobs
            SET status = 'Succeeded',
                locked_by = NULL,
                locked_until_utc = NULL,
                next_attempt_at_utc = NULL,
                updated_at_utc = @now
            WHERE id = @job_id
              AND attempt = @attempt
              AND locked_by = @worker_id
              AND locked_until_utc > @now
              AND status = 'Processing';
            """, connection, transaction);
        command.AddParameter("now", now);
        command.AddParameter("job_id", job.Id);
        command.AddParameter("attempt", job.Attempt);
        command.AddParameter("worker_id", workerId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<bool?> ReadIsMassIssueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT CASE
                       WHEN jsonb_typeof(redacted_payload->'isMassIssue') = 'boolean'
                       THEN (redacted_payload->>'isMassIssue')::boolean
                       ELSE NULL
                   END
            FROM incidentcompass.triage_artifacts
            WHERE job_id = @job_id
              AND attempt IS NULL
              AND kind = 'NeighborSet'
            ORDER BY created_at_utc DESC, id DESC
            LIMIT 1;
            """, connection, transaction);
        command.AddParameter("job_id", jobId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull or null ? null : (bool)value;
    }

    private static async Task<Guid> InsertReportAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TriageJob job,
        TriageReport report,
        bool? isMassIssue,
        Guid? supersedesReportId,
        IReadOnlyList<TriageReportModelParticipant> modelProvenance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.triage_reports (
                id, job_id, fault_id, supersedes_report_id, status, summary, classification, confidence,
                is_mass_issue, recommended_next_action, limitations, documentation_fit, model_provenance,
                config_hash, created_at_utc)
            VALUES (
                @id, @job_id, @fault_id, @supersedes_report_id, @status, @summary, @classification, @confidence,
                @is_mass_issue, @recommended_next_action, @limitations, @documentation_fit, @model_provenance,
                @config_hash, @created_at_utc)
            RETURNING id;
            """, connection, transaction);
        command.AddParameter("id", Guid.NewGuid());
        command.AddParameter("job_id", job.Id);
        command.AddParameter("supersedes_report_id", supersedesReportId);
        command.AddParameter("fault_id", job.FaultId);
        command.AddParameter("status", report.Status.ToDbString());
        command.AddParameter("summary", report.Summary);
        command.AddParameter("classification", report.Classification);
        command.AddParameter("confidence", report.Confidence);
        command.AddParameter("is_mass_issue", isMassIssue);
        command.AddParameter("recommended_next_action", report.RecommendedNextAction);
        command.AddParameter("limitations", report.Limitations.ToArray());
        command.AddParameter("documentation_fit", report.DocumentationFit.ToString());
        command.AddJsonbParameter("model_provenance", JsonSerializer.Serialize(modelProvenance));
        command.AddParameter("config_hash", job.ConfigHash);
        command.AddParameter("created_at_utc", now);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task MarkFaultTerminalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TriageJob job,
        TriageReportStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE incidentcompass.faults
            SET status = @status,
                completed_at_utc = @now
            WHERE id = @fault_id
              AND status IN ('Queued', 'Analyzing');
            """, connection, transaction);
        command.AddParameter("status", status == TriageReportStatus.InsufficientEvidence ? "InsufficientEvidence" : "Completed");
        command.AddParameter("now", now);
        command.AddParameter("fault_id", job.FaultId);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated == 1 || (updated == 0 && job.IsReTriage))
        {
            return;
        }
        LogFaultNotTerminalized(logger, job.FaultId, job.Id);
        throw new InvalidOperationException($"Fault '{job.FaultId}' could not be marked terminal while publishing triage report.");
    }

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Error,
        Message = "Fault {FaultId} was not terminalized while publishing triage report for job {JobId}.")]
    private static partial void LogFaultNotTerminalized(ILogger logger, Guid faultId, Guid jobId);
}
