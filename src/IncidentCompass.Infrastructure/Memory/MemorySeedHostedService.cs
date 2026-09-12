using IncidentCompass.Application.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Memory;

internal sealed partial class MemorySeedHostedService(
    IOptions<MemorySeedOptions> options,
    IServiceScopeFactory scopeFactory,
    MemorySeedSyncStatus syncStatus,
    MemorySeedSyncStatusPersistence statusPersistence,
    TimeProvider timeProvider,
    ILogger<MemorySeedHostedService> logger,
    IRuntimeTelemetry? telemetry = null) : IHostedService, IDisposable
{
    private CancellationTokenSource? resyncCancellation;
    private Task? resyncTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        syncStatus.Configure(settings.Enabled, settings.RuntimeResyncEnabled);
        if (!settings.Enabled)
        {
            return;
        }

        MemorySeedOptionsValidator.Validate(settings);
        await statusPersistence.SaveAsync(syncStatus.Snapshot, cancellationToken);
        await SynchronizeAsync(cancellationToken);
        if (!settings.RuntimeResyncEnabled)
        {
            return;
        }

        resyncCancellation = new CancellationTokenSource();
        resyncTask = RunResyncLoopAsync(resyncCancellation.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (resyncCancellation is null)
        {
            return;
        }

        await resyncCancellation.CancelAsync();
        try
        {
            if (resyncTask is not null)
            {
                try
                {
                    await resyncTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (resyncCancellation.IsCancellationRequested)
                {
                }
            }
        }
        finally
        {
            resyncCancellation.Dispose();
            resyncCancellation = null;
            resyncTask = null;
        }
    }

    private async Task RunResyncLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.RuntimeResyncIntervalSeconds);
        while (true)
        {
            await Task.Delay(interval, timeProvider, cancellationToken);
            try
            {
                await SynchronizeAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogRuntimeSyncFailed(logger, exception.GetType().Name);
            }
        }
    }

    private async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        using var memorySyncTelemetry = telemetry?.StartMemorySync();
        syncStatus.RecordAttempt(timeProvider.GetUtcNow());
        await statusPersistence.SaveAsync(syncStatus.Snapshot, cancellationToken);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var synchronizer = scope.ServiceProvider.GetRequiredService<MemorySeedSynchronizer>();
            var outcome = await synchronizer.SynchronizeAsync(MemorySeedSyncMode.Incremental, cancellationToken);
            if (!outcome.Published)
            {
                await RecordRebuildRequiredAsync(outcome, cancellationToken);
                return;
            }

            syncStatus.RecordSuccess(timeProvider.GetUtcNow(), outcome.Generation!.Value);
            await statusPersistence.SaveAsync(syncStatus.Snapshot, cancellationToken);
            telemetry?.RecordMemorySync(RuntimeTelemetryOutcome.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetry?.RecordMemorySync(RuntimeTelemetryOutcome.Cancelled);
            throw;
        }
        catch
        {
            syncStatus.RecordFailure();
            await statusPersistence.TrySaveFailureAsync(syncStatus.Snapshot, cancellationToken);
            telemetry?.RecordMemorySync(RuntimeTelemetryOutcome.Failed);
            throw;
        }
    }

    /// <summary>
    /// Reports a route change without failing the host start.
    /// </summary>
    /// <remarks>
    /// This is deliberately not an exception. The corpus is intact, the previous route still
    /// retrieves it, and a host that refused to start would take the API and the Worker down over a
    /// configuration edit that a single operator command resolves. What it must not do is look
    /// healthy, so the status carries an error code and the health check reports degraded.
    /// </remarks>
    private async Task RecordRebuildRequiredAsync(
        MemorySeedSyncOutcome outcome,
        CancellationToken cancellationToken)
    {
        syncStatus.RecordRebuildRequired(MemoryCorpusErrorCodes.From(outcome.State), outcome.Generation);
        await statusPersistence.SaveAsync(syncStatus.Snapshot, cancellationToken);
        telemetry?.RecordMemorySync(RuntimeTelemetryOutcome.Failed);
        LogRebuildRequired(logger, outcome.State.ToString(), outcome.Route.RouteId, outcome.ItemCount);
    }

    [LoggerMessage(2301, LogLevel.Warning, "Memory seed runtime synchronization failed with {FailureType}.")]
    private static partial void LogRuntimeSyncFailed(ILogger logger, string failureType);

    [LoggerMessage(
        2303,
        LogLevel.Warning,
        "Memory seed synchronization published nothing because the corpus is {CorpusState} " +
        "relative to embedding route {RouteId}; {ActiveItemCount} previously seeded items remain " +
        "active and retrievable under the route that built them. Run the memory rebuild command.")]
    private static partial void LogRebuildRequired(
        ILogger logger,
        string corpusState,
        string routeId,
        int activeItemCount);

    public void Dispose()
    {
        // Normal shutdown disposes and clears resyncCancellation in StopAsync; this is a
        // defensive fallback in case the host tears this instance down without stopping it.
        resyncCancellation?.Dispose();
    }
}
