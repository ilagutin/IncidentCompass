using System.Globalization;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Governance.ActionApprovals;

internal sealed class PostgresActionToolRuleFactReader(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    GroundedActionProposalContext origin) : IToolRuleFactReader
{
    public Task<int> CountAcceptedUsesAsync(
        string toolName,
        ToolRuleScope scope,
        CancellationToken cancellationToken) =>
        CountAsync("ActionProposed", toolName, scope, cancellationToken);

    public async Task<bool> HasSuccessfulToolResultAsync(
        string toolName,
        ToolRuleScope scope,
        CancellationToken cancellationToken)
    {
        var attemptPredicate = ScopePredicate(scope);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM incidentcompass.triage_ledger
                WHERE event_type = 'ToolResult'
                  AND tool_status = 'Succeeded'
                  AND job_id = @job_id
                  AND tool_name = @tool_name
            """ + attemptPredicate + ");", connection, transaction);
        AddParameters(command, toolName, attemptPredicate);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<int> CountAsync(
        string eventType,
        string toolName,
        ToolRuleScope scope,
        CancellationToken cancellationToken)
    {
        var attemptPredicate = ScopePredicate(scope);
        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM incidentcompass.triage_ledger
            WHERE event_type = @event_type
              AND job_id = @job_id
              AND tool_name = @tool_name
            """ + attemptPredicate + ";", connection, transaction);
        command.AddParameter("event_type", eventType);
        AddParameters(command, toolName, attemptPredicate);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private void AddParameters(NpgsqlCommand command, string toolName, string attemptPredicate)
    {
        command.AddParameter("job_id", origin.JobId);
        command.AddParameter("tool_name", toolName);
        if (attemptPredicate.Length > 0)
        {
            command.AddParameter("attempt", origin.Attempt);
        }
    }

    /// <summary>
    /// The attempt half of the window. Every query here already constrains <c>job_id</c> to this
    /// proposal's origin job, so the job window is the base and the attempt window narrows it.
    /// </summary>
    /// <remarks>
    /// This used to treat anything that was not the literal <c>attempt</c> as the job window, which
    /// silently widened a scope it did not recognize while the triage-ledger reader silently
    /// narrowed the same input. Neither refused it. The window is now decided once, by
    /// <see cref="ToolRuleScopes.NarrowsToAttempt" />, and the engine denies a scope nothing can
    /// evaluate before either reader is reached.
    /// </remarks>
    private static string ScopePredicate(ToolRuleScope scope) =>
        ToolRuleScopes.NarrowsToAttempt(scope) ? " AND attempt = @attempt" : string.Empty;
}
