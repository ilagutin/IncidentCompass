using IncidentCompass.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Pins the single cancellation decision the shared worker task set makes. The three pumps used to
/// disagree: the triage-job copy logged a cancellation the host had not requested as a failure,
/// while the action and post-report copies swallowed it. The shared type logs it, because nothing
/// inside the task set can cancel work that is still being observed, so such a cancellation means
/// claimed and leased work was abandoned for an unrequested reason.
/// </summary>
public sealed class ClaimedTaskSetTests
{
    [Fact]
    public async Task ObserveCompletedAsync_LogsCancellationTheHostDidNotRequest()
    {
        var afterClaim = new List<Exception>();
        var whileDraining = new List<Exception>();
        var taskSet = CreateTaskSet(afterClaim, whileDraining);
        taskSet.Add(Task.FromCanceled(new CancellationToken(true)), new CancellationTokenSource());

        await taskSet.ObserveCompletedAsync(CancellationToken.None);

        var logged = Assert.Single(afterClaim);
        Assert.True(logged is OperationCanceledException);
        Assert.Empty(whileDraining);
    }

    [Fact]
    public async Task ObserveCompletedAsync_RethrowsCancellationCausedByHostShutdown()
    {
        var afterClaim = new List<Exception>();
        var whileDraining = new List<Exception>();
        var taskSet = CreateTaskSet(afterClaim, whileDraining);
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();
        taskSet.Add(Task.FromCanceled(shutdown.Token), new CancellationTokenSource());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => taskSet.ObserveCompletedAsync(shutdown.Token));

        Assert.Empty(afterClaim);
        Assert.Empty(whileDraining);
    }

    [Fact]
    public async Task ObserveCompletedAsync_LogsAFailureThatIsNotCancellation()
    {
        var afterClaim = new List<Exception>();
        var whileDraining = new List<Exception>();
        var taskSet = CreateTaskSet(afterClaim, whileDraining);
        taskSet.Add(
            Task.FromException(new InvalidOperationException("processing failed")),
            new CancellationTokenSource());

        await taskSet.ObserveCompletedAsync(CancellationToken.None);

        Assert.IsType<InvalidOperationException>(Assert.Single(afterClaim));
        Assert.Empty(whileDraining);
    }

    [Fact]
    public async Task DrainAsync_DoesNotReportTheCancellationItRequested()
    {
        var afterClaim = new List<Exception>();
        var whileDraining = new List<Exception>();
        var taskSet = CreateTaskSet(afterClaim, whileDraining);
        var cancellation = new CancellationTokenSource();
        taskSet.Add(Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token), cancellation);

        await taskSet.DrainAsync();

        Assert.Empty(afterClaim);
        Assert.Empty(whileDraining);
    }

    [Fact]
    public async Task DrainAsync_ReportsAFailureThatIsNotCancellation()
    {
        var afterClaim = new List<Exception>();
        var whileDraining = new List<Exception>();
        var taskSet = CreateTaskSet(afterClaim, whileDraining);
        taskSet.Add(
            Task.FromException(new InvalidOperationException("drain failed")),
            new CancellationTokenSource());

        await taskSet.DrainAsync();

        Assert.Empty(afterClaim);
        Assert.IsType<InvalidOperationException>(Assert.Single(whileDraining));
    }

    private static ClaimedTaskSet CreateTaskSet(List<Exception> afterClaim, List<Exception> whileDraining) =>
        new(
            NullLogger.Instance,
            (_, exception) => afterClaim.Add(exception),
            (_, exception) => whileDraining.Add(exception));
}
