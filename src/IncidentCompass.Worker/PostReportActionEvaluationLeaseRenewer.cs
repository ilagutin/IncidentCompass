using IncidentCompass.Application.Governance.PostReportActions;

namespace IncidentCompass.Worker;

internal sealed partial class PostReportActionEvaluationLeaseRenewer(
    ILogger<PostReportActionEvaluationLeaseRenewer> logger)
{
    private static readonly TimeSpan MinimumRenewalInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Renews the evaluation lease until ownership is lost, renewal fails or the caller cancels.
    /// The returned task completing successfully is itself the lease-loss signal: the loop only
    /// exits when the repository reports that this worker no longer owns the fenced claim. It
    /// faults when renewal throws and cancels with <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task RenewUntilStoppedAsync(
        IPostReportActionIntentRepository repository,
        PostReportActionIntentClaim claim,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromTicks(leaseDuration.Ticks / 3);
        if (interval < MinimumRenewalInterval)
        {
            interval = MinimumRenewalInterval;
        }

        while (true)
        {
            await Task.Delay(interval, cancellationToken);
            if (!await repository.RenewLeaseAsync(
                    claim.Intent.Id, workerId, claim.Fence, leaseDuration, cancellationToken))
            {
                return;
            }
        }
    }

    public async Task<PostReportActionWorkflowResult?> EvaluateAsync(
        IPostReportActionIntentRepository repository,
        IPostReportActionWorkflow workflow,
        PostReportActionIntentClaim claim,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workflowTask = workflow.EvaluateAsync(claim.Intent, processing.Token);
        var renewalTask = RenewUntilStoppedAsync(
            repository, claim, workerId, leaseDuration, processing.Token);
        var first = await Task.WhenAny(workflowTask, renewalTask);
        if (first == renewalTask)
        {
            try
            {
                // The renewal loop only completes when the fenced lease is no longer held, so the
                // evaluation is abandoned without a result and another worker picks the intent up.
                await renewalTask;
                LogEvaluationLeaseLost(logger, claim.Intent.Id);
                return null;
            }
            finally
            {
                processing.Cancel();
                await ObserveAbandonedWorkflowAsync(workflowTask);
            }
        }

        try
        {
            return await workflowTask;
        }
        finally
        {
            processing.Cancel();
            await ObserveCancellationAsync(renewalTask);
        }
    }

    private async Task ObserveAbandonedWorkflowAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LogWorkflowFailedAfterLeaseLost(logger, exception);
        }
    }

    [LoggerMessage(EventId = 1701, Level = LogLevel.Warning, Message = "Post-report workflow failed after its evaluation lease was lost.")]
    private static partial void LogWorkflowFailedAfterLeaseLost(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1702, Level = LogLevel.Warning, Message = "Post-report action intent {IntentId} lost its evaluation lease; abandoning the attempt without writing a result.")]
    private static partial void LogEvaluationLeaseLost(ILogger logger, Guid intentId);

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
