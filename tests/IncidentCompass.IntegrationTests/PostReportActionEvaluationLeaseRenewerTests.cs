using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Worker;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// the renewal loop only returns when the fenced lease is gone, so
/// <see cref="PostReportActionEvaluationLeaseRenewer.EvaluateAsync"/> must abandon the evaluation
/// without a result, cancel the in-flight workflow and make the loss audit-visible. This pins that
/// behaviour after the renewer stopped returning a value that could only ever be one thing.
/// </summary>
public sealed class PostReportActionEvaluationLeaseRenewerTests
{
    [Fact]
    public async Task EvaluateAsync_WhenLeaseIsLost_AbandonsWorkflowWithoutResultAndLogsTheLoss()
    {
        var logger = new CapturingLogger();
        var renewer = new PostReportActionEvaluationLeaseRenewer(logger);
        var repository = new LeaseLosingIntentRepository();
        var workflow = new BlockingPostReportActionWorkflow();
        var claim = new PostReportActionIntentClaim(Intent(), Guid.NewGuid());

        var result = await renewer.EvaluateAsync(
            repository,
            workflow,
            claim,
            "lease-renewer-test",
            TimeSpan.FromMilliseconds(300),
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.True(repository.RenewalCalls > 0);
        await workflow.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains(logger.Events, entry => entry.Id.Id == 1702 && entry.Level == LogLevel.Warning);
    }

    private static PostReportActionIntent Intent() => new(
        Guid.NewGuid(),
        "local",
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        1,
        "synthetic_tool",
        1,
        null,
        "lease-renewer-config",
        "lease-renewer-proposal",
        [],
        PostReportActionIntentState.Processing,
        "lease-renewer-test",
        Guid.NewGuid(),
        DateTimeOffset.UtcNow.AddSeconds(1),
        1,
        null,
        null,
        DateTimeOffset.UtcNow,
        null);

    private sealed class LeaseLosingIntentRepository : IPostReportActionIntentRepository
    {
        private int renewalCalls;

        public int RenewalCalls => Volatile.Read(ref renewalCalls);

        public Task<bool> RenewLeaseAsync(
            Guid intentId,
            string claimOwner,
            Guid claimFence,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref renewalCalls);
            return Task.FromResult(false);
        }

        public Task<IReadOnlyList<PostReportActionIntentCandidate>> FindCandidatesAsync(
            int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PostReportActionIntentClaim?> TryClaimAsync(
            Guid intentId,
            string claimOwner,
            TimeSpan leaseDuration,
            int maximumAttempts,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> CompleteAsync(
            Guid intentId,
            Guid claimFence,
            string? resultCode,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RetryAsync(
            Guid intentId,
            Guid claimFence,
            string errorCode,
            TimeSpan retryDelay,
            int maximumAttempts,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeadLetterAsync(
            Guid intentId,
            Guid claimFence,
            string errorCode,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class BlockingPostReportActionWorkflow : IPostReportActionWorkflow
    {
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ToolId => "synthetic_tool";

        public int WorkflowVersion => 1;

        public IncidentCompass.Domain.Incidents.Actions.ActionCategory Category =>
            IncidentCompass.Domain.Incidents.Actions.ActionCategory.Notification;

        public string LogicalTargetId => "synthetic-target";

        public Task<(bool ShouldEnqueue, string? RouteId)> SelectAsync(
            string tenantId,
            Guid originReportId,
            Guid faultId,
            Guid jobId,
            int attempt,
            string configHash,
            string serviceName,
            string environment,
            string? severity,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<PostReportActionWorkflowResult> EvaluateAsync(
            PostReportActionIntent intent,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            return PostReportActionWorkflowResult.Completed();
        }
    }

    private sealed class CapturingLogger : ILogger<PostReportActionEvaluationLeaseRenewer>
    {
        private readonly List<(EventId Id, LogLevel Level)> recorded = [];

        public IReadOnlyList<(EventId Id, LogLevel Level)> Events
        {
            get
            {
                lock (recorded)
                {
                    return [.. recorded];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (recorded)
            {
                recorded.Add((eventId, logLevel));
            }
        }
    }
}
