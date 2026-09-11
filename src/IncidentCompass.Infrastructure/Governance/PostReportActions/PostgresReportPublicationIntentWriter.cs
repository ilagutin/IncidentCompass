using System.Text;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;
namespace IncidentCompass.Infrastructure.Governance.PostReportActions;

internal sealed class PostgresReportPublicationIntentWriter(
    PostReportActionWorkflowCatalog catalog) : ITriageReportPublicationIntentWriter
{
    /// <summary>
    /// The only workflow version the schema admits, and the one a successor entry is written at.
    /// </summary>
    private const int SuccessorWorkflowVersion = 1;

    public async Task WriteAsync(
        TriageJob job,
        Guid reportId,
        string tenantId,
        string serviceName,
        string environment,
        string? severity,
        DateTimeOffset createdAtUtc,
        Func<PostReportActionIntent, CancellationToken, Task> persistAsync,
        CancellationToken cancellationToken)
    {
        foreach (var workflow in catalog.Workflows)
        {
            var selection = await workflow.SelectAsync(
                tenantId, reportId, job.FaultId, job.Id, job.Attempt, job.ConfigHash,
                serviceName, environment, severity, cancellationToken);
            if (!selection.ShouldEnqueue)
            {
                continue;
            }
            if ((workflow.Category == ActionCategory.Notification) != (selection.RouteId is not null) ||
                (selection.RouteId is not null && !AgentToolIdentity.IsValid(selection.RouteId)))
            {
                throw new InvalidOperationException(
                    $"Post-report workflow '{workflow.ToolId}' returned an invalid route selection.");
            }
            var input = new JsonObject
            {
                ["originReportId"] = reportId.ToString("N"),
                ["toolId"] = workflow.ToolId,
                ["workflowVersion"] = workflow.WorkflowVersion
            };
            if (selection.RouteId is not null)
            {
                input["routeId"] = selection.RouteId;
            }
            var canonicalInput = Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(input));
            if (canonicalInput.Length is < 1 or > 8192)
            {
                throw new InvalidOperationException(
                    $"Post-report workflow '{workflow.ToolId}' produced an invalid canonical input.");
            }
            var intent = new PostReportActionIntent(
                Guid.NewGuid(), tenantId, reportId, job.FaultId, job.Id, job.Attempt,
                workflow.ToolId, workflow.WorkflowVersion, selection.RouteId, job.ConfigHash,
                $"post-report:v1:{reportId:N}:{workflow.ToolId}", canonicalInput,
                PostReportActionIntentState.Pending, null, null, null, 0, null, null,
                createdAtUtc, null);
            await persistAsync(intent, cancellationToken);
        }
    }

    internal static async Task<(string TenantId, string ServiceName, string Environment, string? Severity)> ReadFaultContextAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid faultId,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT tenant_id, service_name, environment, severity
            FROM incidentcompass.faults
            WHERE id = @fault_id;
            """, connection, transaction);
        command.AddParameter("fault_id", faultId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Fault '{faultId}' was not found during report publication.");
        }
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    internal static async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostReportActionIntent intent,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.post_report_action_intents (
                id, tenant_id, origin_report_id, fault_id, job_id, attempt, tool_id,
                workflow_version, route_id, config_hash, proposal_key, workflow_input,
                state, attempt_count, created_at_utc)
            VALUES (
                @id, @tenant_id, @origin_report_id, @fault_id, @job_id, @attempt, @tool_id,
                @workflow_version, @route_id, @config_hash, @proposal_key, @workflow_input,
                'pending', 0, @created_at_utc)
            ON CONFLICT (tenant_id, origin_report_id, tool_id) DO NOTHING;
            """, connection, transaction);
        command.AddParameter("id", intent.Id);
        command.AddParameter("tenant_id", intent.TenantId);
        command.AddParameter("origin_report_id", intent.OriginReportId);
        command.AddParameter("fault_id", intent.FaultId);
        command.AddParameter("job_id", intent.JobId);
        command.AddParameter("attempt", intent.Attempt);
        command.AddParameter("tool_id", intent.ToolId);
        command.AddParameter("workflow_version", intent.WorkflowVersion);
        command.AddParameter("route_id", intent.RouteId);
        command.AddParameter("config_hash", intent.ConfigHash);
        command.AddParameter("proposal_key", intent.ProposalKey);
        command.AddParameter("workflow_input", intent.WorkflowInput);
        command.AddParameter("created_at_utc", intent.CreatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Enqueues the evaluation of an action that another action, having actually executed, schedules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this runs inside the predecessor's terminal transaction.</b> A proposal's origin is a
    /// report and cannot be an action, so nothing in the approval contract expresses "after". Writing
    /// the successor's queue entry in the same transaction that records the predecessor as executed
    /// expresses it in the only place that cannot be wrong: the entry exists if and only if that
    /// transaction committed. A poller that looked for settled actions instead would make the ordering
    /// a property of its own timing, and a window in which a push could be proposed for a code write
    /// that had not happened.
    /// </para>
    /// <para>
    /// <b>It is one statement and it cannot enqueue twice.</b> Everything the row needs is already on
    /// the action and its job, so there is no read to race, and the unique key on tenant, report and
    /// tool makes a replayed transaction a no-op rather than a second queue entry. A conflict is
    /// therefore success, which is why nothing is returned.
    /// </para>
    /// </remarks>
    internal static async Task InsertSuccessorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActionApprovalRecord action,
        string successorToolId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        var input = new JsonObject
        {
            ["originReportId"] = action.OriginReportId.ToString("N"),
            ["toolId"] = successorToolId,
            ["workflowVersion"] = SuccessorWorkflowVersion
        };
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.post_report_action_intents (
                id, tenant_id, origin_report_id, fault_id, job_id, attempt, tool_id,
                workflow_version, route_id, config_hash, proposal_key, workflow_input,
                state, attempt_count, created_at_utc)
            SELECT @id, a.tenant_id, a.origin_report_id, a.fault_id, a.job_id, a.attempt,
                   @tool_id, @workflow_version, NULL, j.config_hash, @proposal_key,
                   @workflow_input, 'pending', 0, @created_at_utc
            FROM incidentcompass.action_approvals a
            JOIN incidentcompass.triage_jobs j ON j.id = a.job_id
            WHERE a.id = @action_id
            ON CONFLICT (tenant_id, origin_report_id, tool_id) DO NOTHING;
            """, connection, transaction);
        command.AddParameter("id", Guid.NewGuid());
        command.AddParameter("tool_id", successorToolId);
        command.AddParameter("workflow_version", SuccessorWorkflowVersion);
        command.AddParameter(
            "proposal_key", $"post-report:v1:{action.OriginReportId:N}:{successorToolId}");
        command.AddParameter(
            "workflow_input",
            Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(input)));
        command.AddParameter("created_at_utc", createdAtUtc);
        command.AddParameter("action_id", action.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<Guid?> ReadFaultIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid intentId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT fault_id FROM incidentcompass.post_report_action_intents WHERE id = @id;",
            connection, transaction);
        command.AddParameter("id", intentId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid value ? value : null;
    }

    internal static async Task<bool?> PrepareExhaustedClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid intentId,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            WITH exhausted AS MATERIALIZED (
                SELECT i.id, EXISTS (
                    SELECT 1 FROM incidentcompass.action_approvals a
                    WHERE a.tenant_id = i.tenant_id
                      AND a.origin_report_id = i.origin_report_id
                      AND a.tool_id = i.tool_id
                      AND a.proposal_key = i.proposal_key) AS has_proposal
                FROM incidentcompass.post_report_action_intents i
                WHERE i.id = @id AND i.state = 'processing'
                  AND i.claim_until_utc <= clock_timestamp() AND i.attempt_count >= @maximum
            ), dead_lettered AS (
                UPDATE incidentcompass.post_report_action_intents i
                SET state = 'dead_lettered', claim_owner = NULL, claim_fence = NULL,
                    claim_until_utc = NULL, next_attempt_at_utc = NULL,
                    last_error_code = 'attempts_exhausted', completed_at_utc = clock_timestamp()
                FROM exhausted e
                WHERE i.id = e.id AND (NOT e.has_proposal OR i.last_error_code = 'internal_proposal_recovery')
                RETURNING i.id
            )
            SELECT CASE
                WHEN NOT EXISTS (SELECT 1 FROM exhausted) THEN NULL
                WHEN EXISTS (SELECT 1 FROM dead_lettered) THEN FALSE
                ELSE TRUE
            END;
            """, connection, transaction);
        command.AddParameter("id", intentId);
        command.AddParameter("maximum", maximumAttempts);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is bool recovery ? recovery : null;
    }

    internal static PostReportActionIntent ReadIntent(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4),
        reader.GetInt32(5), reader.GetString(6), reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetString(9), reader.GetString(10), (byte[])reader[11], ParseState(reader.GetString(12)),
        reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetGuid(14),
        reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15), reader.GetInt32(16),
        reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
        reader.IsDBNull(18) ? null : reader.GetString(18), reader.GetFieldValue<DateTimeOffset>(19),
        reader.IsDBNull(20) ? null : reader.GetFieldValue<DateTimeOffset>(20));
    internal static void ValidateClaim(string owner, TimeSpan leaseDuration)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128 || leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(owner));
        }
    }

    internal static void ValidateError(string errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        }
    }

    private static PostReportActionIntentState ParseState(string state) => state switch
    {
        "pending" => PostReportActionIntentState.Pending,
        "processing" => PostReportActionIntentState.Processing,
        "retry_pending" => PostReportActionIntentState.RetryPending,
        "completed" => PostReportActionIntentState.Completed,
        "dead_lettered" => PostReportActionIntentState.DeadLettered,
        _ => throw new InvalidOperationException("Post-report action intent state is invalid.")
    };
}
