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
    public Task AddAsync(RemediationDiff diff, CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "insert remediation diff",
            () => AddCoreAsync(diff, cancellationToken));

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
