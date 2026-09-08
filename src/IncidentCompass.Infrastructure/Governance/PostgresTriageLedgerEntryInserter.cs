using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Governance;

internal static class PostgresTriageLedgerEntryInserter
{
    public static async Task<TriageLedgerEntry> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TriageLedgerAppendRequest request,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.triage_ledger (
                fault_id, job_id, attempt, event_type, role, tool_name, rationale,
                decision, decision_reason, tool_status, tokens_delta, workers_delta,
                payload_ref, config_hash, created_at_utc)
            VALUES (
                @fault_id, @job_id, @attempt, @event_type, @role, @tool_name, @rationale,
                @decision, @decision_reason, @tool_status, @tokens_delta, @workers_delta,
                @payload_ref, @config_hash, @created_at_utc)
            RETURNING id;
            """, connection, transaction);

        command.AddParameter("fault_id", request.FaultId);
        command.AddParameter("job_id", request.JobId);
        command.AddParameter("attempt", request.Attempt);
        command.AddParameter("event_type", request.EventType.ToDbString());
        command.AddParameter("role", request.Role);
        command.AddParameter("tool_name", request.ToolName);
        command.AddParameter("rationale", request.Rationale);
        command.AddParameter("decision", request.Decision?.ToDbString());
        command.AddParameter("decision_reason", request.DecisionReason);
        command.AddParameter("tool_status", request.ToolStatus?.ToDbString());
        command.AddParameter("tokens_delta", request.TokensDelta);
        command.AddParameter("workers_delta", request.WorkersDelta);
        command.AddParameter("payload_ref", request.PayloadRef);
        command.AddParameter("config_hash", request.ConfigHash);
        command.AddParameter("created_at_utc", createdAtUtc);

        var id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        return new TriageLedgerEntry(
            id,
            request.FaultId,
            request.JobId,
            request.Attempt,
            request.EventType,
            request.Role,
            request.ToolName,
            request.Rationale,
            request.Decision,
            request.DecisionReason,
            request.PayloadRef,
            request.ConfigHash,
            createdAtUtc,
            request.ToolStatus,
            request.TokensDelta,
            request.WorkersDelta);
    }
}
