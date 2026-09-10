using IncidentCompass.Application.Governance.ActionApprovals;

namespace IncidentCompass.Worker;

internal sealed class WorkerActionPump(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<WorkerActionPump> logger)
{
    private readonly WorkerActionTaskSet activeActions = new(logger);

    public int ActiveActionCount => activeActions.Count;

    public Task ObserveCompletedAsync(CancellationToken cancellationToken) =>
        activeActions.ObserveCompletedAsync(cancellationToken);

    public Task DrainAsync() => activeActions.DrainAsync();

    public async Task SweepAsync(
        ActionDispatchOptions options,
        CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>();
        await dispatcher.SweepAsync(options.BatchSize, cancellationToken);
    }

    public async Task<int> FillAvailableSlotsAsync(
        string workerId,
        ActionDispatchOptions options,
        CancellationToken cancellationToken)
    {
        var available = options.BatchSize - activeActions.Count;
        if (available <= 0)
        {
            return 0;
        }

        IReadOnlyList<ActionDispatchCandidate> candidates;
        using (var scope = serviceScopeFactory.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>();
            candidates = await dispatcher.FindCandidatesAsync(available, cancellationToken);
        }

        foreach (var candidate in candidates)
        {
            var dispatchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                activeActions.Add(
                    ProcessCandidateAsync(workerId, options, candidate, dispatchCancellation.Token),
                    dispatchCancellation);
            }
            catch
            {
                dispatchCancellation.Dispose();
                throw;
            }
        }

        return candidates.Count;
    }

    public Task WaitForNextWakeAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        activeActions.WaitForNextWakeAsync(delay, cancellationToken);

    private async Task ProcessCandidateAsync(
        string workerId,
        ActionDispatchOptions options,
        ActionDispatchCandidate candidate,
        CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>();
        var adapterTimeout = TimeSpan.FromSeconds(options.AdapterTimeoutSeconds);
        var claim = await dispatcher.TryClaimAsync(
            candidate.ActionId,
            workerId,
            adapterTimeout,
            cancellationToken);
        if (claim is not null)
        {
            await dispatcher.DispatchAsync(claim, adapterTimeout, cancellationToken);
        }
    }
}
