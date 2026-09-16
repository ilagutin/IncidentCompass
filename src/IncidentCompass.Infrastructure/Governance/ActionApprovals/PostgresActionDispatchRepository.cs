using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Testing;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Governance.ActionApprovals;

internal sealed class PostgresActionDispatchRepository(
    PostgresDataSourceProvider dataSourceProvider,
    IActionApprovalTransactionFaultInjector faultInjector) : IActionDispatchRepository
{
    private readonly PostgresActionDispatchTransaction transitions = new(faultInjector);
    private readonly PostgresActionDispatchRecovery recovery = new(faultInjector);

    public Task<IReadOnlyList<ActionDispatchCandidate>> FindCandidatesAsync(
        int limit,
        CancellationToken cancellationToken) =>
        FindAsync(PostgresActionDispatchQueries.FindClaimAsync, limit, cancellationToken);

    public Task<IReadOnlyList<ActionDispatchCandidate>> FindExpiryCandidatesAsync(
        int limit,
        CancellationToken cancellationToken) =>
        FindAsync(PostgresActionDispatchQueries.FindExpiryAsync, limit, cancellationToken);

    public Task<IReadOnlyList<ActionDispatchCandidate>> FindSupersededCandidatesAsync(
        int limit,
        CancellationToken cancellationToken) =>
        FindAsync(PostgresActionDispatchQueries.FindSupersededAsync, limit, cancellationToken);

    public Task<IReadOnlyList<ActionDispatchCandidate>> FindRecoveryCandidatesAsync(
        int limit,
        CancellationToken cancellationToken) =>
        FindAsync(PostgresActionDispatchQueries.FindRecoveryAsync, limit, cancellationToken);

    public Task<ActionDispatchClaim?> TryClaimAsync(
        Guid actionId,
        string dispatchOwner,
        Func<string, TimeSpan> timeoutWithRecoveryGraceForTool,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dispatchOwner) || dispatchOwner.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(dispatchOwner), "Dispatch claim identity or deadline is invalid.");
        }

        ArgumentNullException.ThrowIfNull(timeoutWithRecoveryGraceForTool);
        return RunAsync(
            actionId,
            (connection, transaction, action, token) =>
            {
                var timeoutWithRecoveryGrace = timeoutWithRecoveryGraceForTool(action.ToolId);
                if (timeoutWithRecoveryGrace <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(timeoutWithRecoveryGraceForTool), "Dispatch claim identity or deadline is invalid.");
                }

                return transitions.TryClaimAsync(
                    connection, transaction, action, dispatchOwner, timeoutWithRecoveryGrace, token);
            },
            cancellationToken);
    }

    public Task<bool> CompleteAsync(
        ActionTerminalRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(
            request.ActionId,
            (connection, transaction, action, token) => transitions.CompleteAsync(
                connection, transaction, action, request, token),
            cancellationToken);

    public Task<bool> TryExpireAsync(Guid actionId, CancellationToken cancellationToken) =>
        RunAsync(actionId, transitions.ExpireAsync, cancellationToken);

    public Task<bool> TryFailSupersededAsync(Guid actionId, CancellationToken cancellationToken) =>
        RunAsync(actionId, transitions.FailSupersededAsync, cancellationToken);

    public Task<bool> TryFailOutcomeUnknownAsync(Guid actionId, CancellationToken cancellationToken) =>
        RunAsync(actionId, recovery.FailOutcomeUnknownAsync, cancellationToken);

    private async Task<IReadOnlyList<ActionDispatchCandidate>> FindAsync(
        Func<NpgsqlConnection, int, CancellationToken, Task<IReadOnlyList<ActionDispatchCandidate>>> query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return await PostgresOperation.ExecuteAsync(
            "scan action approval candidates",
            async () =>
            {
                await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
                return await query(connection, limit, cancellationToken);
            });
    }

    private Task<TResult> RunAsync<TResult>(
        Guid actionId,
        Func<NpgsqlConnection, NpgsqlTransaction, ActionApprovalRecord, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "transition action approval",
            () => RunTransactionAsync(actionId, operation, cancellationToken));

    private async Task<TResult> RunTransactionAsync<TResult>(
        Guid actionId,
        Func<NpgsqlConnection, NpgsqlTransaction, ActionApprovalRecord, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var candidate = await PostgresActionApprovalQueries.FindForWorkerAsync(
                connection, transaction, actionId, false, cancellationToken);
            if (candidate is null)
            {
                return default!;
            }

            await PostgresFaultTransactionLock.LockAsync(connection, transaction, candidate.FaultId, cancellationToken);
            var action = await PostgresActionApprovalQueries.FindForWorkerAsync(
                connection, transaction, actionId, true, cancellationToken);
            var result = action is null ? default! : await operation(connection, transaction, action, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
