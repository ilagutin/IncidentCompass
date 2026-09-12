using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// Reads the pull-request number one report's governed chain recorded, from the audit projection.
/// </summary>
/// <remarks>
/// The query is the shared one, and it is also used inside the proposal transaction so that what a
/// workflow proposed and what the proposal boundary re-checks are the same read. Ambiguity returns
/// nothing rather than choosing, and a kind other than a pull request returns nothing rather than
/// treating an issue number as one.
/// </remarks>
internal sealed class PostgresConfirmedPullRequestReader(
    PostgresDataSourceProvider dataSourceProvider) : IConfirmedPullRequestReader
{
    public Task<string?> FindConfirmedPullRequestNumberAsync(
        string tenantId,
        Guid originReportId,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "read confirmed pull request number",
            async () =>
            {
                await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
                return await ReadAsync(
                    connection, null, tenantId, originReportId, cancellationToken);
            });

    internal static async Task<string?> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        Guid originReportId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || originReportId == Guid.Empty)
        {
            return null;
        }

        await using var command = new NpgsqlCommand("""
            SELECT external_resource_id
            FROM incidentcompass.action_approvals
            WHERE tenant_id = @tenant_id AND origin_report_id = @origin_report_id
              AND tool_id = @tool_id AND category = @category AND state = @state AND mode = @mode
              AND external_resource_kind = @resource_kind AND external_resource_id IS NOT NULL
            ORDER BY id
            LIMIT 2;
            """, connection, transaction);
        command.AddParameter("tenant_id", tenantId);
        command.AddParameter("origin_report_id", originReportId);
        command.AddParameter("tool_id", PullRequestToolDescriptor.ToolId);
        command.AddParameter("category", ActionCategory.PrCreate.ToStorageValue());
        command.AddParameter("state", ActionApprovalState.Executed.ToStorageValue());
        command.AddParameter("mode", ActionExecutionMode.Live.ToStorageValue());
        command.AddParameter("resource_kind", ExternalActionAuditProjection.GitHubPullRequestKind);
        var found = new List<string>(2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(reader.GetString(0));
        }

        return found.Count == 1 &&
            ExternalActionAuditProjection.IsValidResourceIdentity(
                ExternalActionAuditProjection.GitHubPullRequestKind, found[0])
            ? found[0]
            : null;
    }
}
