using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// Reads earlier branch pushes for the same incident, the same tool and the same binding.
/// </summary>
/// <remarks>
/// <para>
/// It is scoped by adapter binding as well as by incident, so actions taken while the host pointed at
/// a different repository or a different base branch are not treated as history for this one. An
/// earlier success under another binding says nothing about whether this branch exists here.
/// </para>
/// <para>
/// The confirmed commit comes out of the compact audit projection on the earlier row rather than out
/// of its result payload. That projection exists so this question is a column read, and reading a
/// column keeps this path from having to parse a result document to decide whether to write.
/// </para>
/// </remarks>
internal sealed class PostgresBranchPushActionHistory(
    PostgresDataSourceProvider dataSourceProvider) : IBranchPushActionHistory
{
    private const int MaximumHistory = 16;

    public Task<BranchPushActionHistorySnapshot> ReadPriorAsync(
        Guid actionId,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "read prior branch push history",
            () => ReadAsync(actionId, cancellationToken));

    private async Task<BranchPushActionHistorySnapshot> ReadAsync(
        Guid actionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH current_action AS (
                SELECT tenant_id, fault_id, tool_id, adapter_binding_fingerprint,
                       created_at_utc, id
                FROM incidentcompass.action_approvals
                WHERE id = @action_id AND category = @category
            )
            SELECT a.state, a.mode, a.failure_code, a.result_payload, a.result_summary,
                   a.external_resource_kind, a.external_resource_id
            FROM incidentcompass.action_approvals a
            CROSS JOIN current_action c
            WHERE a.tenant_id = c.tenant_id AND a.fault_id = c.fault_id
              AND a.tool_id = c.tool_id AND a.id <> c.id
              AND a.adapter_binding_fingerprint = c.adapter_binding_fingerprint
              AND (a.created_at_utc, a.id) < (c.created_at_utc, c.id)
            ORDER BY a.created_at_utc DESC, a.id DESC
            LIMIT @limit;
            """, connection);
        command.AddParameter("action_id", actionId);
        command.AddParameter("category", ActionCategory.BranchPush.ToStorageValue());
        command.AddParameter("limit", MaximumHistory + 1);
        byte[]? confirmedPayload = null;
        string? confirmedSummary = null;
        string? confirmedCommitSha = null;
        var hasPending = false;
        var hasOutcomeUnknown = false;
        var count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            count++;
            if (count > MaximumHistory)
            {
                continue;
            }

            var state = reader.GetString(0);
            var mode = reader.GetString(1);
            var failureCode = reader.IsDBNull(2) ? null : reader.GetString(2);
            var resultPayload = reader.IsDBNull(3) ? null : (byte[])reader[3];
            if (confirmedPayload is null &&
                state == ActionApprovalState.Executed.ToStorageValue() &&
                mode == ActionExecutionMode.Live.ToStorageValue() && resultPayload is not null)
            {
                confirmedPayload = resultPayload;
                confirmedSummary = reader.IsDBNull(4) ? null : reader.GetString(4);
                confirmedCommitSha = !reader.IsDBNull(5) && !reader.IsDBNull(6) &&
                    reader.GetString(5) == ExternalActionAuditProjection.GitBranchKind
                        ? reader.GetString(6)
                        : null;
            }
            else if (state == ActionApprovalState.Failed.ToStorageValue() &&
                failureCode == CodePublicationCodes.OutcomeUnknown)
            {
                hasOutcomeUnknown = true;
            }
            else if (state == ActionApprovalState.Requested.ToStorageValue() ||
                state == ActionApprovalState.Approved.ToStorageValue())
            {
                hasPending = true;
            }
        }

        return new BranchPushActionHistorySnapshot(
            confirmedPayload,
            confirmedSummary,
            confirmedCommitSha,
            hasPending,
            hasOutcomeUnknown,
            count > MaximumHistory);
    }
}
