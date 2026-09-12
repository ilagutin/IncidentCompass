using System.Text;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Testing;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Domain.Incidents.Statuses;
using IncidentCompass.Infrastructure.Governance.PostReportActions;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Governance.ActionApprovals;

internal sealed class PostgresActionDispatchTransaction(IActionApprovalTransactionFaultInjector faultInjector)
{
    public async Task<ActionDispatchClaim?> TryClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActionApprovalRecord action,
        string owner,
        TimeSpan timeoutWithRecoveryGrace,
        CancellationToken cancellationToken)
    {
        if (action.State != ActionApprovalState.Approved || action.DispatchStartedAtUtc is not null ||
            !await PostgresActionApprovalQueries.IsCurrentReportAsync(connection, transaction, action, cancellationToken))
        {
            return null;
        }
        var fence = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            WITH current_clock AS (SELECT clock_timestamp() AS now)
            UPDATE incidentcompass.action_approvals a
            SET dispatch_owner = @owner, dispatch_fence = @fence,
                dispatch_started_at = current_clock.now,
                dispatch_deadline_at = current_clock.now + @deadline
            FROM current_clock
            WHERE a.id = @action_id AND a.state = 'approved' AND a.dispatch_started_at IS NULL
            RETURNING a.dispatch_started_at, a.dispatch_deadline_at;
            """, connection, transaction);
        command.AddParameter("owner", owner);
        command.AddParameter("fence", fence);
        command.AddParameter("deadline", timeoutWithRecoveryGrace);
        command.AddParameter("action_id", action.Id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var started = reader.GetDateTimeOffset(0);
        var deadline = reader.GetDateTimeOffset(1);
        await reader.DisposeAsync();
        await faultInjector.BeforeDispatchLedgerAsync(cancellationToken);
        var origin = await PostgresActionOriginContext.ReadAsync(connection, transaction, action, cancellationToken);
        await PostgresActionLedgerWriter.InsertAsync(
            connection, transaction, origin, TriageLedgerEventType.ActionDispatchStarted, action.ToolId,
            owner, "dispatch_claimed", null, null, "action:" + action.Id, started, cancellationToken);
        return new ActionDispatchClaim(
            action with
            {
                DispatchOwner = owner,
                DispatchFence = fence,
                DispatchStartedAtUtc = started,
                DispatchDeadlineAtUtc = deadline
            },
            fence);
    }

    public async Task<bool> CompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActionApprovalRecord action,
        ActionTerminalRequest request,
        CancellationToken cancellationToken)
    {
        ActionTerminalValidator.ValidateForAction(action, request);
        await using var command = new NpgsqlCommand("""
            UPDATE incidentcompass.action_approvals
            SET state = @state, result_payload = @result_payload, result_summary = @result_summary,
                failure_code = @failure_code,
                external_resource_kind = @external_resource_kind,
                external_resource_id = @external_resource_id,
                external_before_state = @external_before_state,
                external_after_state = @external_after_state,
                completed_at_utc = clock_timestamp()
            WHERE id = @action_id AND state = 'approved' AND dispatch_fence = @fence
              AND dispatch_started_at IS NOT NULL AND dispatch_deadline_at > clock_timestamp()
            RETURNING completed_at_utc;
            """, connection, transaction);
        command.AddParameter("state", request.TerminalState.ToStorageValue());
        command.AddParameter("result_payload", request.ResultPayload);
        command.AddParameter("result_summary", request.ResultSummary);
        command.AddParameter("failure_code", request.FailureCode);
        command.AddParameter("external_resource_kind", request.AuditProjection?.ResourceKind);
        command.AddParameter("external_resource_id", request.AuditProjection?.ResourceId);
        command.AddParameter("external_before_state", request.AuditProjection?.BeforeState);
        command.AddParameter("external_after_state", request.AuditProjection?.AfterState);
        command.AddParameter("action_id", action.Id);
        command.AddParameter("fence", request.DispatchFence);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null)
        {
            return false;
        }
        var completed = value is DateTimeOffset offset
            ? offset
            : new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc));
        var artifactId = await PostgresActionResultWriter.InsertAsync(
            connection, transaction, action, request.ResultPayload, request.ResultSummary,
            request.FailureCode, completed, cancellationToken);
        await faultInjector.BeforeTerminalLedgerAsync(cancellationToken);
        var origin = await PostgresActionOriginContext.ReadAsync(connection, transaction, action, cancellationToken);
        var status = request.TerminalState == ActionApprovalState.Executed
            ? TriageLedgerToolStatus.Succeeded
            : TriageLedgerToolStatus.Failed;
        await PostgresActionLedgerWriter.InsertAsync(
            connection, transaction, origin, TriageLedgerEventType.ActionCompleted, action.ToolId,
            action.DispatchOwner, request.ResultSummary, null, status,
            "artifact:" + artifactId, completed, cancellationToken);

        // A governed action that actually executed may schedule exactly one successor, and this is
        // where that happens: inside the transaction that records the execution, so the successor's
        // queue entry cannot exist before the predecessor succeeded and cannot be lost after it did.
        // ActionSuccessorIntents is the whole of the policy; a tool that schedules nothing gets null
        // here and this costs one branch.
        if (ActionSuccessorIntents.SuccessorToolId(
                action.ToolId, action.Category, action.Mode, request.TerminalState) is { } successor)
        {
            await PostgresReportPublicationIntentWriter.InsertSuccessorAsync(
                connection, transaction, action, successor, completed, cancellationToken);
        }

        return true;
    }

    public async Task<bool> FailSupersededAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActionApprovalRecord action,
        CancellationToken cancellationToken)
    {
        if (action.State is not (ActionApprovalState.Requested or ActionApprovalState.Approved) ||
            action.DispatchStartedAtUtc is not null ||
            await PostgresActionApprovalQueries.IsCurrentReportAsync(connection, transaction, action, cancellationToken))
        {
            return false;
        }

        var resultPayload = Encoding.UTF8.GetBytes("{\"code\":\"origin_report_superseded\"}");
        await using var command = new NpgsqlCommand("""
            UPDATE incidentcompass.action_approvals
            SET state = 'failed', result_payload = @result, result_summary = @summary,
                failure_code = @code, completed_at_utc = clock_timestamp()
            WHERE id = @action_id AND state IN ('requested', 'approved') AND dispatch_started_at IS NULL
            RETURNING completed_at_utc;
            """, connection, transaction);
        command.AddParameter("result", resultPayload);
        command.AddParameter("summary", "Origin report was superseded before dispatch.");
        command.AddParameter("code", "origin_report_superseded");
        command.AddParameter("action_id", action.Id);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null)
        {
            return false;
        }
        var completed = value is DateTimeOffset offset
            ? offset
            : new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc));
        var artifactId = await PostgresActionResultWriter.InsertAsync(
            connection, transaction, action, resultPayload,
            "Origin report was superseded before dispatch.", "origin_report_superseded",
            completed, cancellationToken);
        await faultInjector.BeforeTerminalLedgerAsync(cancellationToken);
        var origin = await PostgresActionOriginContext.ReadAsync(connection, transaction, action, cancellationToken);
        await PostgresActionLedgerWriter.InsertAsync(
            connection, transaction, origin, TriageLedgerEventType.ActionCompleted, action.ToolId,
            "system:supersession", "origin_report_superseded", null, TriageLedgerToolStatus.Failed,
            "artifact:" + artifactId, completed, cancellationToken);
        return true;
    }

    public async Task<bool> ExpireAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActionApprovalRecord action,
        CancellationToken cancellationToken)
    {
        if (action.State != ActionApprovalState.Requested)
        {
            return false;
        }
        await using var command = new NpgsqlCommand("""
            UPDATE incidentcompass.action_approvals
            SET state = 'expired', decision_actor = 'system:expiry',
                decision_at_utc = clock_timestamp(), completed_at_utc = clock_timestamp()
            WHERE id = @action_id AND state = 'requested' AND expires_at_utc <= clock_timestamp()
            RETURNING completed_at_utc;
            """, connection, transaction);
        command.AddParameter("action_id", action.Id);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null)
        {
            return false;
        }

        var completed = value is DateTimeOffset offset
            ? offset
            : new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc));
        await faultInjector.BeforeDecisionLedgerAsync(cancellationToken);
        var origin = await PostgresActionOriginContext.ReadAsync(connection, transaction, action, cancellationToken);
        await PostgresActionLedgerWriter.InsertAsync(
            connection, transaction, origin, TriageLedgerEventType.ApprovalDecision, action.ToolId,
            "system:expiry", "expired", TriageLedgerDecision.Expired, null,
            "action:" + action.Id, completed, cancellationToken);
        return true;
    }
}
