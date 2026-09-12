using System.Security.Cryptography;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// Reads the one executed, live predecessor a report may have for each link in the publication chain.
/// </summary>
/// <remarks>
/// <para>
/// The predicate is the whole contract: the same tenant, the same report, the named tool, its own
/// category, the executed state, live mode, and a result payload that exists. Two rows matching means
/// durable state does not name one change, and the read returns nothing rather than choosing; a dry run
/// is excluded because it never reached an adapter and applied nothing.
/// </para>
/// <para>
/// Both links use the same query because the question is the same one, and a second copy of it would be
/// a second place for "executed" to quietly come to mean something else. What differs is the tool and
/// the category, which are parameters, and whether the caller has any use for the compact audit
/// projection and the report's confidence, which are read either way and cost nothing.
/// </para>
/// </remarks>
internal sealed class PostgresRemediationPredecessorReader(
    PostgresDataSourceProvider dataSourceProvider) : IRemediationPredecessorReader
{
    public Task<RemediationPredecessor?> FindExecutedCodeWriteAsync(
        string tenantId,
        Guid originReportId,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "read executed remediation code write",
            () => ReadAsync(
                tenantId, originReportId, RemediationApplyToolDescriptor.ToolId,
                ActionCategory.CodeWrite, cancellationToken));

    public Task<RemediationPredecessor?> FindExecutedBranchPushAsync(
        string tenantId,
        Guid originReportId,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "read executed remediation branch push",
            () => ReadAsync(
                tenantId, originReportId, BranchPushToolDescriptor.ToolId,
                ActionCategory.BranchPush, cancellationToken));

    private async Task<RemediationPredecessor?> ReadAsync(
        string tenantId,
        Guid originReportId,
        string toolId,
        ActionCategory category,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT a.id, a.canonical_payload, a.result_payload, a.completed_at_utc,
                   a.external_resource_id, r.confidence
            FROM incidentcompass.action_approvals a
            LEFT JOIN incidentcompass.triage_reports r ON r.id = a.origin_report_id
            WHERE a.tenant_id = @tenant_id AND a.origin_report_id = @origin_report_id
              AND a.tool_id = @tool_id AND a.category = @category AND a.state = @state
              AND a.mode = @mode
              AND a.result_payload IS NOT NULL AND a.completed_at_utc IS NOT NULL
            ORDER BY a.completed_at_utc DESC, a.id DESC
            LIMIT 2;
            """, connection);
        command.AddParameter("tenant_id", tenantId);
        command.AddParameter("origin_report_id", originReportId);
        command.AddParameter("tool_id", toolId);
        command.AddParameter("category", category.ToStorageValue());
        command.AddParameter("state", ActionApprovalState.Executed.ToStorageValue());
        command.AddParameter("mode", ActionExecutionMode.Live.ToStorageValue());
        var found = new List<RemediationPredecessor>(2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(new RemediationPredecessor(
                reader.GetGuid(0),
                (byte[])reader[1],
                Convert.ToHexStringLower(SHA256.HashData((byte[])reader[2])),
                reader.GetFieldValue<DateTimeOffset>(3))
            {
                ExternalResourceId = reader.IsDBNull(4) ? null : reader.GetString(4),
                ReportConfidence = reader.IsDBNull(5) ? null : reader.GetString(5)
            });
        }

        return found.Count == 1 ? found[0] : null;
    }
}
