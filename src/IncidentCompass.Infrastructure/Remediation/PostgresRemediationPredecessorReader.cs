using System.Security.Cryptography;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// Reads the one executed, live <c>code_write</c> action a report may have.
/// </summary>
/// <remarks>
/// The predicate is the whole contract: the same tenant, the same report, the remediation apply tool,
/// the code-write category, the executed state, live mode, and a result payload that exists. Two rows
/// matching means durable state does not name one change, and the read returns nothing rather than
/// choosing; a dry run is excluded because it never reached an adapter and applied nothing.
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
            () => ReadAsync(tenantId, originReportId, cancellationToken));

    private async Task<RemediationPredecessor?> ReadAsync(
        string tenantId,
        Guid originReportId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, canonical_payload, result_payload, completed_at_utc
            FROM incidentcompass.action_approvals
            WHERE tenant_id = @tenant_id AND origin_report_id = @origin_report_id
              AND tool_id = @tool_id AND category = @category AND state = @state AND mode = @mode
              AND result_payload IS NOT NULL AND completed_at_utc IS NOT NULL
            ORDER BY completed_at_utc DESC, id DESC
            LIMIT 2;
            """, connection);
        command.AddParameter("tenant_id", tenantId);
        command.AddParameter("origin_report_id", originReportId);
        command.AddParameter("tool_id", RemediationApplyToolDescriptor.ToolId);
        command.AddParameter("category", ActionCategory.CodeWrite.ToStorageValue());
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
                reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return found.Count == 1 ? found[0] : null;
    }
}
