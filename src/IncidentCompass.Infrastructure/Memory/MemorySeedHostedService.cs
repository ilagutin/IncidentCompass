using IncidentCompass.Application.Core.Observability;
using IncidentCompass.Application.Memory;
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

        // Only a host that seeds needs a chunk budget or a tokenizer; the corpus commands run the
        // synchronizer, which initializes the counter itself, and status reads only its kind.
        MemorySeedOptionsValidator.Validate(settings);
        using (var scope = scopeFactory.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMemoryChunkTokenCounter>()
                .InitializeAsync(settings.Chunking, cancellationToken);
        }

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
            catch (MemorySeedDocumentRefusedException exception)
            {
                LogRuntimeSyncDocumentRefused(logger, exception.SeedSource);
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
    /// Reports a pass that published nothing without failing the host start: a route change, a corpus
    /// holding mixed routes, an installed local model that is not the route's model, or no usable
    /// local model.
    /// </summary>
    /// <remarks>
    /// This is deliberately not an exception. The corpus is intact and still current, and a host that
    /// refused to start would take the Worker down over a state an operator resolves with a command:
    /// a rebuild for a route change or mixed routes, an install or a route correction for the two model
    /// states. What it must not do is look healthy, so the status carries the state's code and the
    /// health check reports degraded.
    /// </remarks>
    private async Task RecordRebuildRequiredAsync(
        MemorySeedSyncOutcome outcome,
        CancellationToken cancellationToken)
    {
        syncStatus.RecordRebuildRequired(MemoryCorpusErrorCodes.From(outcome.State), outcome.Generation);
        await statusPersistence.SaveAsync(syncStatus.Snapshot, cancellationToken);
        telemetry?.RecordMemorySync(RuntimeTelemetryOutcome.Failed);
        if (outcome.State is MemoryCorpusState.EmbeddingModelMismatch or MemoryCorpusState.EmbeddingModelUnavailable)
        {
            LogEmbeddingModelBlocked(
                logger,
                outcome.Route.RouteId,
                outcome.State.ToString(),
                outcome.ModelErrorCode ?? "none",
                outcome.ItemCount);
            return;
        }

        LogRebuildRequired(logger, outcome.State.ToString(), outcome.Route.RouteId, outcome.ItemCount);
    }

    [LoggerMessage(
        2304,
        LogLevel.Warning,
        "Memory seed synchronization published nothing because the local embedding model for route {RouteId} is " +
        "{CorpusState} (model code {ModelErrorCode}); {ActiveItemCount} previously seeded items remain active. " +
        "Install the configured model with the memory model install command or correct the route.")]
    private static partial void LogEmbeddingModelBlocked(
        ILogger logger,
        string routeId,
        string corpusState,
        string modelErrorCode,
        int activeItemCount);

    [LoggerMessage(2301, LogLevel.Warning, "Memory seed runtime synchronization failed with {FailureType}.")]
    private static partial void LogRuntimeSyncFailed(ILogger logger, string failureType);

    [LoggerMessage(
        2305,
        LogLevel.Warning,
        "Memory seed runtime synchronization published nothing because seed {SeedSource} has a heading path, or a " +
        "line together with its heading path, that exceeds the chunk token limit; the previous corpus remains " +
        "current. Such a line is refused, never split or truncated: shorten it, or raise the chunking MaxTokens " +
        "where the embedding window allows and run the memory rebuild command.")]
    private static partial void LogRuntimeSyncDocumentRefused(ILogger logger, string seedSource);

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
