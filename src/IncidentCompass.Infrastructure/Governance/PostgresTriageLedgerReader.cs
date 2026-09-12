using System.Globalization;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Governance;

internal sealed class PostgresTriageLedgerReader(PostgresDataSourceProvider dataSourceProvider) : ITriageLedgerReader
{
    public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(
        TriageJob job,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "read triage budget usage",
            () => ReadBudgetUsageCoreAsync(job, cancellationToken));

    private async Task<TriageBudgetLedgerUsage> ReadBudgetUsageCoreAsync(TriageJob job, CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT COALESCE(SUM(tokens_delta), 0), COALESCE(SUM(workers_delta), 0)
            FROM incidentcompass.triage_ledger
            WHERE event_type = 'BudgetEvent'
            """ + ScopePredicate(ToolRuleScope.Attempt) + ";",
            connection);
        AddScopeParameters(command, job, ToolRuleScope.Attempt);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new TriageBudgetLedgerUsage(TokensSpent: 0, WorkerCalls: 0);
        }

        return new TriageBudgetLedgerUsage(Convert.ToInt32(reader.GetInt64(0)), Convert.ToInt32(reader.GetInt64(1)));
    }

    public Task<int> CountPolicyDecisionsAsync(
        TriageJob job,
        string toolName,
        ToolRuleScope scope,
        TriageLedgerDecision decision,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "count triage policy decisions",
            () => CountPolicyDecisionsCoreAsync(job, toolName, scope, decision, cancellationToken));

    private async Task<int> CountPolicyDecisionsCoreAsync(
        TriageJob job,
        string toolName,
        ToolRuleScope scope,
        TriageLedgerDecision decision,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT COUNT(*)
            FROM incidentcompass.triage_ledger
            WHERE event_type = 'PolicyDecision'
              AND tool_name = @tool_name
              AND decision = @decision
            """ + ScopePredicate(scope),
            connection);
        AddScopeParameters(command, job, scope);
        command.AddParameter("tool_name", toolName);
        command.AddParameter("decision", decision.ToDbString());

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public Task<bool> HasSuccessfulToolResultAsync(
        TriageJob job,
        string toolName,
        ToolRuleScope scope,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "read successful tool result",
            () => HasSuccessfulToolResultCoreAsync(job, toolName, scope, cancellationToken));

    private async Task<bool> HasSuccessfulToolResultCoreAsync(
        TriageJob job,
        string toolName,
        ToolRuleScope scope,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM incidentcompass.triage_ledger
                WHERE event_type = 'ToolResult'
                  AND tool_name = @tool_name
                  AND tool_status = @tool_status
            """ + ScopePredicate(scope) + ");",
            connection);
        AddScopeParameters(command, job, scope);
        command.AddParameter("tool_name", toolName);
        command.AddParameter("tool_status", TriageLedgerToolStatus.Succeeded.ToDbString());

        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }



    public Task<IReadOnlyList<FaultLedgerEntry>> ReadByFaultIdAsync(
        Guid faultId,
        string tenantId,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "read tenant-scoped fault ledger",
            () => ReadByFaultIdCoreAsync(faultId, tenantId, cancellationToken));

    // The payload-state CASE resolves each event's reference against `triage_artifacts` without ever
    // constraining which ledger rows come back. It is a scalar expression over the row, not a join
    // condition, so a reaped payload still yields its event - which is the behaviour the whole
    // reconstruction requirement rests on, and turning this into a join would quietly undo it.
    //
    // The regex guard is what makes the `::uuid` cast safe rather than a way to reject rows. Only
    // `PostgresTriageToolResultCommitter` writes an `artifact:` reference and it always writes a
    // GUID, but `payload_ref` is unconstrained text, so a row that does not carry a parseable
    // artifact id must be answered rather than raise. `NotReapable` is the honest answer for it:
    // retention removes attempt artifacts, and this reference does not name one. PostgreSQL only
    // hoists constant subexpressions out of a CASE branch, and both the cast and the EXISTS depend
    // on the row, so the guard really does run first.
    private const string PayloadStateExpression = """
        CASE
            WHEN ledger.payload_ref IS NULL THEN 'None'
            WHEN ledger.payload_ref !~ '^artifact:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
                THEN 'NotReapable'
            WHEN EXISTS (
                SELECT 1
                FROM incidentcompass.triage_artifacts artifact
                WHERE artifact.id = substring(ledger.payload_ref FROM 10)::uuid) THEN 'Retained'
            ELSE 'Reaped'
        END
        """;

    private async Task<IReadOnlyList<FaultLedgerEntry>> ReadByFaultIdCoreAsync(Guid faultId, string tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT ledger.id, ledger.fault_id, ledger.job_id, ledger.attempt, ledger.event_type, ledger.role, ledger.tool_name, ledger.rationale,
                   ledger.decision, ledger.decision_reason, ledger.payload_ref, ledger.config_hash, ledger.created_at_utc,
                   ledger.tool_status, ledger.tokens_delta, ledger.workers_delta,
            """ + PayloadStateExpression + """

            FROM incidentcompass.triage_ledger ledger
            JOIN incidentcompass.faults fault ON fault.id = ledger.fault_id
            WHERE ledger.fault_id = @fault_id
              AND fault.tenant_id = @tenant_id
            ORDER BY ledger.id;
            """,
            connection);
        command.AddParameter("fault_id", faultId);
        command.AddParameter("tenant_id", tenantId);

        var entries = new List<FaultLedgerEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new FaultLedgerEntry(
                new TriageLedgerEntry(
                    reader.GetInt64(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetInt32(3),
                    Enum.Parse<TriageLedgerEventType>(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : Enum.Parse<TriageLedgerDecision>(reader.GetString(8)),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.GetString(11),
                    reader.GetDateTimeOffset(12),
                    reader.IsDBNull(13) ? null : Enum.Parse<TriageLedgerToolStatus>(reader.GetString(13)),
                    reader.IsDBNull(14) ? null : reader.GetInt32(14),
                    reader.IsDBNull(15) ? null : reader.GetInt32(15)),
                Enum.Parse<TriageLedgerPayloadState>(reader.GetString(16))));
        }

        return entries;
    }

    /// <summary>
    /// The scope predicate, derived from the one answer rather than from a switch of its own.
    /// </summary>
    /// <remarks>
    /// This used to switch on the configured string, which meant it also decided what an
    /// unrecognized scope meant - it narrowed to the attempt, while the post-report action reader
    /// widened to the job for the same input - and carried a <c>fault</c> branch nothing could
    /// reach. The engine now parses once and denies a window it does not evaluate, so what is left
    /// here is the single question <see cref="ToolRuleScopes.NarrowsToAttempt" /> answers for every
    /// reader.
    /// </remarks>
    private static string ScopePredicate(ToolRuleScope scope) =>
        ToolRuleScopes.NarrowsToAttempt(scope)
            ? " AND job_id = @job_id AND attempt = @attempt"
            : " AND job_id = @job_id";

    private static void AddScopeParameters(NpgsqlCommand command, TriageJob job, ToolRuleScope scope)
    {
        command.AddParameter("job_id", job.Id);
        if (ToolRuleScopes.NarrowsToAttempt(scope))
        {
            command.AddParameter("attempt", job.Attempt);
        }
    }
}
