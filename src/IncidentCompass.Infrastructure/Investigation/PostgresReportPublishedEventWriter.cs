using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Investigation;

internal static class PostgresReportPublishedEventWriter
{
    public static async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TriageJob job,
        Guid reportId,
        TriageReport report,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The rationale is the report summary. Only a report the backend wrote itself carries the
        // authorship marker; a model-authored summary that opens with it is refused before it gets
        // here, and is refused again here so no caller can write a forged marker into the ledger.
        if (!report.BackendAuthored && ReservedReportText.StartsWithBackendMarker(report.Summary))
        {
            throw new TriageReportValidationException(ReservedReportText.ReservedTextRefusal);
        }

        var rationale = report.BackendAuthored
            ? ReservedReportText.BackendAuthoredLedgerPrefix + report.Summary
            : report.Summary;
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.triage_ledger (
                fault_id, job_id, attempt, event_type, role, tool_name, rationale,
                decision, decision_reason, tool_status, tokens_delta, workers_delta,
                payload_ref, config_hash, created_at_utc)
            VALUES (
                @fault_id, @job_id, @attempt, 'ReportPublished', NULL, 'publish_report', @rationale,
                NULL, NULL, NULL, NULL, NULL, @payload_ref, @config_hash, @created_at_utc);
            """, connection, transaction);
        command.AddParameter("fault_id", job.FaultId);
        command.AddParameter("job_id", job.Id);
        command.AddParameter("attempt", job.Attempt);
        command.AddParameter("rationale", rationale);
        command.AddParameter("payload_ref", "report:" + reportId);
        command.AddParameter("config_hash", job.ConfigHash);
        command.AddParameter("created_at_utc", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

}
