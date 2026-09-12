using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Governance.ActionApprovals;

internal static class PostgresActionApprovalQueries
{
    public static async Task<IReadOnlyList<ActionApprovalRecord>> ListAsync(
        NpgsqlConnection connection,
        ActionApprovalListFilter filter,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var externalResourcePredicate = filter.ExternalResourceKind is null
            ? string.Empty
            : " AND a.external_resource_kind = @external_resource_kind" +
              " AND a.external_resource_id = @external_resource_id";
        // The fault predicate is the inverse direction of the exact external-resource lookup: it
        // answers "which external resources did this incident touch" from the same rows that answer
        // "which incident produced this external resource". Like the external-resource predicate it
        // is appended only when it is used, because a `@fault_id IS NULL OR ...` form would keep the
        // planner off `ix_action_approvals_tenant_fault`. Tenant scoping is not part of it: the
        // `a.tenant_id` predicate below already scopes every shape of this query, so a fault id
        // belonging to another tenant simply matches nothing.
        var faultPredicate = filter.FaultId is null
            ? string.Empty
            : " AND a.fault_id = @fault_id";
        var sortColumn = filter.ExternalResourceKind is null
            ? "a.created_at_utc"
            : "a.completed_at_utc";
        await using var command = new NpgsqlCommand(
            "SELECT " + PostgresActionApprovalReader.Columns + """
            FROM incidentcompass.action_approvals a
            WHERE a.tenant_id = @tenant_id
              AND (@status::text IS NULL OR a.state = @status)
            """ + externalResourcePredicate + faultPredicate +
            " AND (@before_created::timestamptz IS NULL OR " + sortColumn + " < @before_created" +
            " OR (" + sortColumn + " = @before_created AND a.id < @before_id::uuid))" +
            " ORDER BY " + sortColumn + " DESC, a.id DESC " + """
            LIMIT @limit;
            """, connection);
        command.AddParameter("tenant_id", tenantId);
        command.AddParameter("status", filter.Status?.ToStorageValue());
        if (filter.ExternalResourceKind is not null)
        {
            command.AddParameter("external_resource_kind", filter.ExternalResourceKind);
            command.AddParameter("external_resource_id", filter.ExternalResourceId);
        }

        if (filter.FaultId is not null)
        {
            command.AddParameter("fault_id", filter.FaultId.Value);
        }

        command.AddParameter("before_created", filter.BeforeCreatedAtUtc);
        command.AddParameter("before_id", filter.BeforeActionId);
        command.AddParameter("limit", filter.Limit);
        var rows = new List<ActionApprovalRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(PostgresActionApprovalReader.Read(reader));
        }

        return rows;
    }

    public static async Task<ActionApprovalRecord?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid actionId,
        string tenantId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var lockClause = forUpdate ? " FOR UPDATE OF a" : string.Empty;
        await using var command = new NpgsqlCommand(
            "SELECT " + PostgresActionApprovalReader.Columns + """
            FROM incidentcompass.action_approvals a
            WHERE a.id = @action_id AND a.tenant_id = @tenant_id
            """ + lockClause + ";", connection, transaction);
        command.AddParameter("action_id", actionId);
        command.AddParameter("tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? PostgresActionApprovalReader.Read(reader) : null;
    }

    public static async Task<IReadOnlyList<ActionApprovalProvenance>> ReadProvenanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid actionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT ordinal, source_type, source_id, artifact_kind, trust_class
            FROM incidentcompass.action_approval_provenance
            WHERE action_id = @action_id
            ORDER BY ordinal;
            """, connection, transaction);
        command.AddParameter("action_id", actionId);
        var rows = new List<ActionApprovalProvenance>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ActionApprovalProvenance(
                reader.GetInt32(0), reader.GetString(1), reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                ActionApprovalVocabulary.ParseTrust(reader.GetString(4))));
        }

        return rows;
    }

    public static async Task<ActionApprovalRecord?> FindForWorkerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actionId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var lockClause = forUpdate ? " FOR UPDATE OF a" : string.Empty;
        await using var command = new NpgsqlCommand(
            "SELECT " + PostgresActionApprovalReader.Columns + """
            FROM incidentcompass.action_approvals a
            WHERE a.id = @action_id
            """ + lockClause + ";", connection, transaction);
        command.AddParameter("action_id", actionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? PostgresActionApprovalReader.Read(reader) : null;
    }

    public static async Task<bool> IsCurrentReportAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActionApprovalRecord action,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id = @report_id
            FROM incidentcompass.triage_reports
            WHERE fault_id = @fault_id
            ORDER BY created_at_utc DESC, id DESC
            LIMIT 1;
            """, connection, transaction);
        command.AddParameter("report_id", action.OriginReportId);
        command.AddParameter("fault_id", action.FaultId);
        return (bool?)(await command.ExecuteScalarAsync(cancellationToken)) == true;
    }
}
