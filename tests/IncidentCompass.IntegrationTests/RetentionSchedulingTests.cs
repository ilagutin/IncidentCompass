using IncidentCompass.Application.Core.Configuration;
using IncidentCompass.Application.Intake.Retention;
using IncidentCompass.Application.Investigation.Retention;
using IncidentCompass.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The scheduling half of retention: what one pass does when half of it fails, what it does when the
/// host is shutting down, and what the off switch actually stops. The two operations themselves are
/// covered against a real database by <c>RetentionLifecycleTests</c>; these drive the pump over
/// recording ports instead, because what is under test is the loop around the operations rather than
/// the statements inside them.
/// </summary>
public sealed class RetentionSchedulingTests
{
    [Fact]
    public async Task RunOnceAsync_ReapsArtifactsEvenWhenCompactionFails()
    {
        var compaction = new RecordingSignalPayloadCompactionRepository { Failure = () => new InvalidOperationException("compaction failed") };
        var reaping = new RecordingAttemptArtifactRetentionRepository();
        using var provider = BuildProvider(compaction, reaping);
        var pump = ActivatorUtilities.CreateInstance<RetentionPump>(provider);

        await pump.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, compaction.CallCount);
        Assert.Equal(1, reaping.CallCount);
    }

    [Fact]
    public async Task RunOnceAsync_CompactsPayloadsEvenWhenReapingFails()
    {
        var compaction = new RecordingSignalPayloadCompactionRepository();
        var reaping = new RecordingAttemptArtifactRetentionRepository { Failure = () => new InvalidOperationException("reap failed") };
        using var provider = BuildProvider(compaction, reaping);
        var pump = ActivatorUtilities.CreateInstance<RetentionPump>(provider);

        await pump.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, compaction.CallCount);
        Assert.Equal(1, reaping.CallCount);
    }

    /// <summary>
    /// Cancellation is the one failure the pass does not absorb. A cancelled compaction ends the pass
    /// there rather than being logged as a database error and followed by a reap that would be
    /// cancelled too.
    /// </summary>
    [Fact]
    public async Task RunOnceAsync_PropagatesCancellationRatherThanTreatingItAsAFailure()
    {
        var compaction = new RecordingSignalPayloadCompactionRepository { ObserveCancellation = true };
        var reaping = new RecordingAttemptArtifactRetentionRepository();
        using var provider = BuildProvider(compaction, reaping);
        var pump = ActivatorUtilities.CreateInstance<RetentionPump>(provider);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pump.RunOnceAsync(cancellation.Token));

        Assert.Equal(1, compaction.CallCount);
        Assert.Equal(0, reaping.CallCount);
    }

    /// <summary>
    /// Shutdown has to be observed while the worker is sitting in its interval delay, which is the
    /// state it is in almost all of the time. The stop is given a generous timeout that must not be
    /// reached: if the delay were not cancelable, <c>StopAsync</c> would return on that timeout with
    /// the loop still running, so the assertions are that the timeout did not fire and that the
    /// worker's task ended as cancelled rather than as a failed pass.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ObservesShutdownWhileWaitingForTheNextPass()
    {
        var passCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compaction = new RecordingSignalPayloadCompactionRepository();
        var reaping = new RecordingAttemptArtifactRetentionRepository { OnCall = () => passCompleted.TrySetResult() };
        using var provider = BuildProvider(compaction, reaping);
        var worker = CreateWorker(provider, new RetentionScheduleOptions { IntervalMinutes = 1440 });

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await passCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.StopAsync(stopTimeout.Token);

        Assert.False(stopTimeout.IsCancellationRequested);
        Assert.NotNull(worker.ExecuteTask);
        Assert.True(worker.ExecuteTask.IsCanceled);
        Assert.Equal(1, compaction.CallCount);
        Assert.Equal(1, reaping.CallCount);
        worker.Dispose();
    }

    /// <summary>
    /// Off has to mean "runs nothing", not "is not composed": the hosted service still starts, so the
    /// startup log distinguishes retention being switched off from retention being broken.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_RunsNeitherOperationWhenRetentionIsDisabled()
    {
        var compaction = new RecordingSignalPayloadCompactionRepository();
        var reaping = new RecordingAttemptArtifactRetentionRepository();
        using var provider = BuildProvider(compaction, reaping);
        var worker = CreateWorker(
            provider,
            new RetentionScheduleOptions { Enabled = false, IntervalMinutes = 1 });

        await worker.StartAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(worker.ExecuteTask);
        await worker.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, compaction.CallCount);
        Assert.Equal(0, reaping.CallCount);
        worker.Dispose();
    }

    private static ServiceProvider BuildProvider(
        ISignalPayloadCompactionRepository compaction,
        IAttemptArtifactRetentionRepository reaping)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new RetentionOptions()));
        services.AddSingleton(compaction);
        services.AddSingleton(reaping);
        services.AddScoped<AgedSignalPayloadCompactor>();
        services.AddScoped<StaleAttemptArtifactReaper>();
        return services.BuildServiceProvider();
    }

    private static RetentionWorker CreateWorker(
        IServiceProvider provider,
        RetentionScheduleOptions schedule) =>
        new(
            provider.GetRequiredService<ILogger<RetentionWorker>>(),
            Options.Create(schedule),
            ActivatorUtilities.CreateInstance<RetentionPump>(provider));

    private sealed class RecordingSignalPayloadCompactionRepository : ISignalPayloadCompactionRepository
    {
        public int CallCount { get; private set; }

        public Func<Exception>? Failure { get; init; }

        public bool ObserveCancellation { get; init; }

        public Task<int> CompactAsync(
            DateTimeOffset receivedBeforeUtc,
            int maxRows,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (ObserveCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Failure is null ? Task.FromResult(0) : throw Failure();
        }
    }

    private sealed class RecordingAttemptArtifactRetentionRepository : IAttemptArtifactRetentionRepository
    {
        public int CallCount { get; private set; }

        public Func<Exception>? Failure { get; init; }

        public Action? OnCall { get; init; }

        public Task<int> ReapAsync(
            DateTimeOffset createdBeforeUtc,
            int maxRows,
            CancellationToken cancellationToken)
        {
            CallCount++;
            OnCall?.Invoke();
            return Failure is null ? Task.FromResult(0) : throw Failure();
        }
    }
}
