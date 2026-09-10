namespace IncidentCompass.Worker;

/// <summary>
/// The in-flight work one worker pump has claimed, together with the per-item cancellation source
/// that pump created for it. Every pump owns exactly one of these and binds it to its own logger
/// and its own pair of failure log events, so the event ids documented in
/// <c>docs/observability.md</c> stay distinct per pump while the concurrency behaviour stays
/// single-sourced.
/// </summary>
/// <remarks>
/// This type does not claim, renew or dispatch anything; it only observes work that is already
/// running and disposes each item's cancellation source exactly once, whichever way the item ends.
/// </remarks>
internal sealed class ClaimedTaskSet(
    ILogger logger,
    Action<ILogger, Exception> logFailureAfterClaim,
    Action<ILogger, Exception> logFailureWhileDraining)
{
    private readonly List<(Task Work, CancellationTokenSource Cancellation)> claimed = [];

    public int Count => claimed.Count;

    public void Add(Task work, CancellationTokenSource cancellation) => claimed.Add((work, cancellation));

    /// <summary>
    /// Reaps the items that have already finished and leaves the rest running. Cancellation caused
    /// by host shutdown is rethrown for the pump loop to act on; every other ending is reported
    /// through the after-claim failure log.
    /// </summary>
    public async Task ObserveCompletedAsync(CancellationToken cancellationToken)
    {
        for (var index = claimed.Count - 1; index >= 0; index--)
        {
            var item = claimed[index];
            if (!item.Work.IsCompleted)
            {
                continue;
            }

            claimed.RemoveAt(index);
            try
            {
                await item.Work;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Deliberate, and the one place the three pumps used to disagree: a cancellation the
                // host did not ask for is logged as a failure here rather than swallowed. Nothing in
                // this type can cancel an item that is still being observed - each item's source is
                // linked to the pump token, and DrainAsync takes items out of this list before it
                // cancels them - so an OperationCanceledException arriving while the host token is
                // untouched means claimed, leased work was abandoned for a reason nobody here
                // requested. A provider or database client timeout surfacing as
                // TaskCanceledException is the common case, and it has to stay visible: the item
                // holds a lease that will now expire on its own.
                logFailureAfterClaim(logger, exception);
            }
            finally
            {
                item.Cancellation.Dispose();
            }
        }
    }

    /// <summary>
    /// Cancels every remaining item and waits for it to stop. Cancellation is the outcome this
    /// method asked for, so it is not reported; anything else is reported through the draining
    /// failure log.
    /// </summary>
    public async Task DrainAsync()
    {
        var running = claimed.ToArray();
        claimed.Clear();
        foreach (var item in running)
        {
            item.Cancellation.Cancel();
        }

        foreach (var item in running)
        {
            try
            {
                await item.Work;
            }
            catch (OperationCanceledException)
            {
                // Requested above, so it is the expected ending and not a failure.
            }
            catch (Exception exception)
            {
                logFailureWhileDraining(logger, exception);
            }
            finally
            {
                item.Cancellation.Dispose();
            }
        }
    }

    public async Task WaitForNextWakeAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (claimed.Count == 0)
        {
            await Task.Delay(delay, cancellationToken);
            return;
        }

        await WorkerWakeDelay.WaitAsync(
            delay, claimed.Select(static item => item.Work), cancellationToken);
    }
}
