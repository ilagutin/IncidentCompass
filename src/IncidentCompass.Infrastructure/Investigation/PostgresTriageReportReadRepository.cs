using System.Text.Json;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Investigation.Reports.Get;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Investigation;

internal sealed class PostgresTriageReportReadRepository(PostgresDataSourceProvider dataSourceProvider)
    : ITriageReportReadRepository
{

    public Task<TriageReportDetailsResponse?> FindByIdAsync(
        Guid reportId,
        string tenantId,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "read tenant-scoped triage report",
            () => FindByIdCoreAsync(reportId, tenantId, cancellationToken));

    public Task<TriageReportDetailsResponse?> FindLatestByFaultIdAsync(
        Guid faultId,
        string tenantId,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "read tenant-scoped latest triage report",
            () => FindLatestCoreAsync(faultId, tenantId, cancellationToken));
    private async Task<TriageReportDetailsResponse?> FindByIdCoreAsync(Guid reportId, string tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        return await LoadDetailsAsync(connection, () => LoadByIdAsync(connection, reportId, tenantId, cancellationToken), cancellationToken);
    }

    private async Task<TriageReportDetailsResponse?> FindLatestCoreAsync(Guid faultId, string tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        return await LoadDetailsAsync(connection, () => LoadLatestAsync(connection, faultId, tenantId, cancellationToken), cancellationToken);
    }

    private static async Task<TriageReportDetailsResponse?> LoadDetailsAsync(
        NpgsqlConnection connection,
        Func<Task<TriageReportDetailsResponse?>> loadReport,
        CancellationToken cancellationToken)
    {
        var report = await loadReport();
        if (report is null)
        {
            return null;
        }

        var evidence = await LoadEvidenceAsync(connection, report.Id, cancellationToken);
        return report with { Evidence = evidence };
    }

    private static async Task<TriageReportDetailsResponse?> LoadByIdAsync(
        NpgsqlConnection connection,
        Guid reportId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ReportSelect + " WHERE r.id = @report_id AND fault.tenant_id = @tenant_id;", connection);
        command.AddParameter("report_id", reportId);
        command.AddParameter("tenant_id", tenantId);
        return await ReadReportAsync(command, cancellationToken);
    }

    private static async Task<TriageReportDetailsResponse?> LoadLatestAsync(
        NpgsqlConnection connection,
        Guid faultId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ReportSelect + "\n" + """
            WHERE r.fault_id = @fault_id
              AND fault.tenant_id = @tenant_id
              AND NOT EXISTS (
                  SELECT 1
                  FROM incidentcompass.triage_reports successor
                  WHERE successor.supersedes_report_id = r.id)
            ORDER BY r.created_at_utc DESC, r.id DESC
            LIMIT 1;
            """, connection);
        command.AddParameter("fault_id", faultId);
        command.AddParameter("tenant_id", tenantId);
        return await ReadReportAsync(command, cancellationToken);
    }

    private static async Task<TriageReportDetailsResponse?> ReadReportAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        Guid? supersededByReportId = reader.IsDBNull(13) ? null : reader.GetGuid(13);
        return new TriageReportDetailsResponse(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetBoolean(7),
            reader.IsDBNull(8) ? string.Empty : reader.GetString(8), reader.GetFieldValue<string[]>(9),
            reader.GetString(10), reader.GetDateTimeOffset(11), reader.IsDBNull(12) ? null : reader.GetGuid(12),
            supersededByReportId, supersededByReportId is null, [],
            ReadModelProvenance(reader, 14));
    }

    /// <summary>
    /// Reads the stored model provenance, keeping a NULL column NULL. A report published before
    /// provenance was recorded is immutable and cannot be backfilled, so it says nothing here
    /// rather than claiming an empty list of models.
    /// </summary>
    private static IReadOnlyList<TriageReportModelParticipant>? ReadModelProvenance(
        NpgsqlDataReader reader,
        int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : JsonSerializer.Deserialize<IReadOnlyList<TriageReportModelParticipant>>(reader.GetString(ordinal));
    }

    private static async Task<IReadOnlyList<TriageReportEvidenceResponse>> LoadEvidenceAsync(
        NpgsqlConnection connection,
        Guid reportId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT e.id, e.kind, e.artifact_id, e.reference, e.quote, e.score,
                   a.kind, a.domain_ref, a.redacted_payload::text
            FROM incidentcompass.triage_evidence e
            JOIN incidentcompass.triage_artifacts a ON a.id = e.artifact_id
            WHERE e.report_id = @report_id
            ORDER BY e.created_at_utc, e.id;
            """, connection);
        command.AddParameter("report_id", reportId);

        var evidence = new List<TriageReportEvidenceResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var payload = JsonDocument.Parse(reader.GetString(8));
            evidence.Add(new TriageReportEvidenceResponse(
                reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), payload.RootElement.Clone()));
        }

        return evidence;
    }

    private const string ReportSelect = """
        SELECT r.id, r.fault_id, r.status, r.summary, r.classification, r.confidence, r.documentation_fit,
               r.is_mass_issue, r.recommended_next_action, r.limitations, r.config_hash, r.created_at_utc,
               r.supersedes_report_id, successor.id, r.model_provenance::text
        FROM incidentcompass.triage_reports r
        JOIN incidentcompass.faults fault ON fault.id = r.fault_id
        LEFT JOIN LATERAL (
            SELECT candidate.id
            FROM incidentcompass.triage_reports candidate
            WHERE candidate.supersedes_report_id = r.id
            ORDER BY candidate.created_at_utc DESC, candidate.id DESC
            LIMIT 1
        ) successor ON TRUE
        """;
}
