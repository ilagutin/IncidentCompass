using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// Writes produced remediation diffs to <c>incidentcompass.remediation_diffs</c>.
/// </summary>
/// <remarks>
/// Insert only. A diff is a statement about a base that existed at one instant, so a second pass
/// against a moved checkout writes a second row rather than editing the first; an update would
/// repoint an identity a reviewer may already be reading. The table's own checks restate the bounds
/// the Application layer already enforced, so a defect on either side is a refused write rather than
/// a row that quietly disagrees with the rules.
/// </remarks>
internal sealed class PostgresRemediationDiffRepository(PostgresDataSourceProvider dataSourceProvider)
    : IRemediationDiffRepository
{
    private const string Columns =
        """
        id, tenant_id, report_id, job_id, attempt, service_name, source_release,
        base_tree_identity, result_tree_identity, files_changed, patch_bytes, patch_text,
        route_id, model, validation_code, test_command_id, test_outcome, created_at_utc
        """;

    public Task AddAsync(RemediationDiff diff, CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "insert remediation diff",
            () => AddCoreAsync(diff, cancellationToken));

    public Task<IReadOnlyList<RemediationDiff>> FindForReportAsync(
        string tenantId,
        Guid reportId,
        int maximum,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "read remediation diffs for report",
            () => FindForReportCoreAsync(tenantId, reportId, maximum, cancellationToken));

    /// <summary>
    /// Reads the report's diffs newest first, bounded, with the tenant in the predicate.
    /// </summary>
    /// <remarks>
    /// This is the read the covering index added with the table was built for. The bound is what
    /// lets a caller answer "exactly one" in one round trip: asking for two rows and getting two is
    /// the whole of the ambiguity check, and no count over an append-only table is needed for it.
    /// </remarks>
    private async Task<IReadOnlyList<RemediationDiff>> FindForReportCoreAsync(
        string tenantId,
        Guid reportId,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT {Columns}
            FROM incidentcompass.remediation_diffs
            WHERE tenant_id = @tenant_id AND report_id = @report_id
            ORDER BY created_at_utc DESC, id DESC
            LIMIT @maximum;
            """,
            connection);
        command.AddParameter("tenant_id", tenantId);
        command.AddParameter("report_id", reportId);
        command.AddParameter("maximum", maximum);
        var rows = new List<RemediationDiff>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RemediationDiff(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.GetString(13),
                reader.GetString(14),
                reader.GetFieldValue<DateTimeOffset>(17))
            {
                TestCommandId = reader.IsDBNull(15) ? null : reader.GetString(15),
                TestOutcome = reader.GetString(16)
            });
        }

        return rows;
    }

    private async Task AddCoreAsync(RemediationDiff diff, CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO incidentcompass.remediation_diffs (
                id, tenant_id, report_id, job_id, attempt, service_name, source_release,
                base_tree_identity, result_tree_identity, files_changed, patch_bytes, patch_text,
                route_id, model, validation_code, test_command_id, test_outcome, created_at_utc)
            VALUES (
                @id, @tenant_id, @report_id, @job_id, @attempt, @service_name, @source_release,
                @base_tree_identity, @result_tree_identity, @files_changed, @patch_bytes, @patch_text,
                @route_id, @model, @validation_code, @test_command_id, @test_outcome, @created_at_utc);
            """,
            connection);

        command.AddParameter("id", diff.Id);
        command.AddParameter("tenant_id", diff.TenantId);
        command.AddParameter("report_id", diff.ReportId);
        command.AddParameter("job_id", diff.JobId);
        command.AddParameter("attempt", diff.Attempt);
        command.AddParameter("service_name", diff.ServiceName);
        command.AddParameter("source_release", diff.Release);
        command.AddParameter("base_tree_identity", diff.BaseTreeIdentity);
        command.AddParameter("result_tree_identity", diff.ResultTreeIdentity);
        command.AddParameter("files_changed", diff.FilesChanged);
        command.AddParameter("patch_bytes", diff.PatchBytes);
        command.AddParameter("patch_text", diff.PatchText);
        command.AddParameter("route_id", diff.RouteId);
        command.AddParameter("model", diff.Model);
        command.AddParameter("validation_code", diff.ValidationCode);
        command.AddParameter("test_command_id", diff.TestCommandId);
        command.AddParameter("test_outcome", diff.TestOutcome);
        command.AddParameter("created_at_utc", diff.CreatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
