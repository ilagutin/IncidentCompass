using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;
namespace IncidentCompass.Infrastructure.Governance.PostReportActions;

internal sealed class PostgresPostReportActionIntentRepository(
    PostgresDataSourceProvider dataSourceProvider) : IPostReportActionIntentRepository
{
    public Task<IReadOnlyList<PostReportActionIntentCandidate>> FindCandidatesAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        return PostgresOperation.ExecuteAsync("scan post-report action intents", async () =>
        {
            await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT id, fault_id
                FROM incidentcompass.post_report_action_intents
                WHERE state = 'pending'
                   OR (state = 'retry_pending' AND next_attempt_at_utc <= clock_timestamp())
                   OR (state = 'processing' AND claim_until_utc <= clock_timestamp())
                ORDER BY created_at_utc, id
                LIMIT @limit;
                """, connection);
            command.AddParameter("limit", limit);
            var rows = new List<PostReportActionIntentCandidate>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new(reader.GetGuid(0), reader.GetGuid(1)));
            }
            return (IReadOnlyList<PostReportActionIntentCandidate>)rows;
        });
    }

    public Task<PostReportActionIntentClaim?> TryClaimAsync(
        Guid intentId,
        string claimOwner,
        TimeSpan leaseDuration,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        PostgresReportPublicationIntentWriter.ValidateClaim(claimOwner, leaseDuration);
        if (maximumAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }
        return PostgresOperation.ExecuteAsync(
            "claim post-report action intent",
            () => ClaimTransactionAsync(
                intentId, claimOwner, leaseDuration, maximumAttempts, cancellationToken));
    }

    public Task<bool> RenewLeaseAsync(
        Guid intentId,
        string claimOwner,
        Guid claimFence,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        PostgresReportPublicationIntentWriter.ValidateClaim(claimOwner, leaseDuration);
        return TransitionAsync(intentId, claimFence, """
            UPDATE incidentcompass.post_report_action_intents
            SET claim_until_utc = clock_timestamp() + @duration
            WHERE id = @id AND state = 'processing' AND claim_owner = @owner
              AND claim_fence = @fence AND claim_until_utc > clock_timestamp();
            """, cancellationToken, ("owner", claimOwner), ("duration", leaseDuration));
    }

    public Task<bool> CompleteAsync(
        Guid intentId,
        Guid claimFence,
        string? resultCode,
        CancellationToken cancellationToken) =>
        TransitionAsync(intentId, claimFence, """
            UPDATE incidentcompass.post_report_action_intents
            SET state = 'completed', claim_owner = NULL, claim_fence = NULL,
                claim_until_utc = NULL, next_attempt_at_utc = NULL,
                last_error_code = @code, completed_at_utc = clock_timestamp()
            WHERE id = @id AND state = 'processing' AND claim_fence = @fence
              AND claim_until_utc > clock_timestamp();
            """, cancellationToken, ("code", resultCode));
    public Task<bool> RetryAsync(
        Guid intentId,
        Guid claimFence,
        string errorCode,
        TimeSpan retryDelay,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        PostgresReportPublicationIntentWriter.ValidateError(errorCode);
        if (retryDelay <= TimeSpan.Zero || maximumAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }
        return TransitionAsync(intentId, claimFence, """
            UPDATE incidentcompass.post_report_action_intents
            SET state = CASE WHEN attempt_count >= @maximum THEN 'dead_lettered' ELSE 'retry_pending' END,
                claim_owner = NULL, claim_fence = NULL, claim_until_utc = NULL,
                next_attempt_at_utc = CASE WHEN attempt_count >= @maximum THEN NULL ELSE clock_timestamp() + @delay END,
                last_error_code = @code,
                completed_at_utc = CASE WHEN attempt_count >= @maximum THEN clock_timestamp() ELSE NULL END
            WHERE id = @id AND state = 'processing' AND claim_fence = @fence
              AND claim_until_utc > clock_timestamp();
            """, cancellationToken, ("code", errorCode), ("delay", retryDelay), ("maximum", maximumAttempts));
    }

    public Task<bool> DeadLetterAsync(
        Guid intentId,
        Guid claimFence,
        string errorCode,
        CancellationToken cancellationToken)
    {
        PostgresReportPublicationIntentWriter.ValidateError(errorCode);
        return TransitionAsync(intentId, claimFence, """
            UPDATE incidentcompass.post_report_action_intents
            SET state = 'dead_lettered', claim_owner = NULL, claim_fence = NULL,
                claim_until_utc = NULL, next_attempt_at_utc = NULL,
                last_error_code = @code, completed_at_utc = clock_timestamp()
            WHERE id = @id AND state = 'processing' AND claim_fence = @fence
              AND claim_until_utc > clock_timestamp();
            """, cancellationToken, ("code", errorCode));
    }

    private async Task<PostReportActionIntentClaim?> ClaimTransactionAsync(Guid intentId,
        string owner,
        TimeSpan leaseDuration,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var faultId = await PostgresReportPublicationIntentWriter.ReadFaultIdAsync(
                connection, transaction, intentId, cancellationToken);
            if (faultId is null)
            {
                return null;
            }
            await PostgresFaultTransactionLock.LockAsync(connection, transaction, faultId.Value, cancellationToken);
            var proposalRecovery = await PostgresReportPublicationIntentWriter.PrepareExhaustedClaimAsync(connection,
                transaction, intentId, maximumAttempts, cancellationToken);
            if (proposalRecovery == false)
            {
                await transaction.CommitAsync(cancellationToken); return null;
            }
            var fence = Guid.NewGuid();
            await using var command = new NpgsqlCommand("""
                UPDATE incidentcompass.post_report_action_intents
                SET state = 'processing', claim_owner = @owner, claim_fence = @fence,
                    claim_until_utc = clock_timestamp() + @duration,
                    attempt_count = attempt_count + CASE WHEN @proposal_recovery THEN 0 ELSE 1 END,
                    next_attempt_at_utc = NULL,
                    last_error_code = CASE WHEN @proposal_recovery THEN 'internal_proposal_recovery' ELSE NULL END
                WHERE id = @id AND (
                    state = 'pending' OR
                    (state = 'retry_pending' AND next_attempt_at_utc <= clock_timestamp()) OR
                    (state = 'processing' AND claim_until_utc <= clock_timestamp()))
                RETURNING id, tenant_id, origin_report_id, fault_id, job_id, attempt, tool_id,
                          workflow_version, route_id, config_hash, proposal_key, workflow_input, state,
                          claim_owner, claim_fence, claim_until_utc, attempt_count, next_attempt_at_utc,
                          last_error_code, created_at_utc, completed_at_utc;
                """, connection, transaction);
            command.AddParameter("id", intentId);
            command.AddParameter("owner", owner);
            command.AddParameter("fence", fence);
            command.AddParameter("duration", leaseDuration); command.AddParameter("proposal_recovery", proposalRecovery == true);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var intent = await reader.ReadAsync(cancellationToken)
                ? PostgresReportPublicationIntentWriter.ReadIntent(reader)
                : null;
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return intent is null ? null : new(intent, fence);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<bool> TransitionAsync(Guid intentId,
        Guid fence,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters) =>
        await PostgresOperation.ExecuteAsync("transition post-report action intent", async () =>
        {
            await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.AddParameter("id", intentId);
            command.AddParameter("fence", fence);
            foreach (var parameter in parameters)
            {
                command.AddParameter(parameter.Name, parameter.Value);
            }

            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        });
}
