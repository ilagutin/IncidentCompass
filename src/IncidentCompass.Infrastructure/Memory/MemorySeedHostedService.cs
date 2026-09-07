using System.Security.Cryptography;
using System.Text;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Observability;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Memory;

internal sealed partial class MemorySeedHostedService(
    IOptions<MemorySeedOptions> options,
    IHostEnvironment environment,
    IServiceScopeFactory scopeFactory,
    MemorySeedSyncStatus syncStatus,
    MemorySeedSyncStatusPersistence statusPersistence,
    TimeProvider timeProvider,
    ILogger<MemorySeedHostedService> logger,
    IRuntimeTelemetry? telemetry = null) : IHostedService, IDisposable
{
    private const string MemorySearchToolName = "memory_search";
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
            var configurationRepository = scope.ServiceProvider.GetRequiredService<ITriageConfigurationRepository>();
            var embeddingClient = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();
            var memoryRepository = scope.ServiceProvider.GetRequiredService<IMemoryRepository>();
            var configuration = await configurationRepository.GetCurrentAsync(cancellationToken);
            var route = ResolveEmbeddingRoute(configuration);
            var scan = MemorySeedFileLoader.LoadScan(ResolveRootDirectory());
            var entries = new List<MemorySeedEntry>(scan.Files.Count);
            foreach (var file in scan.Files)
            {
                entries.Add(await PrepareSeedAsync(file, route, embeddingClient, memoryRepository, cancellationToken));
            }

            var corpus = new MemorySeedCorpus(
                options.Value.TenantId, options.Value.Owner, Guid.NewGuid(), scan.PresentDirectories, entries);
            await memoryRepository.ReconcileSeedCorpusAsync(corpus, cancellationToken);
            syncStatus.RecordSuccess(timeProvider.GetUtcNow(), corpus.Generation);
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

    private static TriageRouteSettings ResolveEmbeddingRoute(TriageConfiguration configuration)
    {
        if (!configuration.Tools.TryGetValue(MemorySearchToolName, out var tool) ||
            string.IsNullOrWhiteSpace(tool.EmbeddingRouteId))
        {
            throw new InvalidOperationException("memory_search must declare an EmbeddingRouteId before memory seeding can run.");
        }

        if (!configuration.Routes.TryGetValue(tool.EmbeddingRouteId, out var route))
        {
            throw new InvalidOperationException(
                "Memory seed embedding route '" + tool.EmbeddingRouteId +
                "' is not configured; fix Tools.memory_search.EmbeddingRouteId.");
        }

        return route;
    }

    private async Task<MemorySeedEntry> PrepareSeedAsync(
        MemorySeedFile file,
        TriageRouteSettings route,
        IEmbeddingClient embeddingClient,
        IMemoryRepository memoryRepository,
        CancellationToken cancellationToken)
    {
        var contentHash = ComputeSha256Hex(file.Content);
        var item = CreateItem(file, contentHash);
        if (await memoryRepository.SeedItemExistsAsync(options.Value.Owner, item, cancellationToken))
        {
            return new MemorySeedEntry(item, []);
        }

        var embedding = await embeddingClient.CreateEmbeddingAsync(
            new EmbeddingRequest(file.Content, route.Model, "memory-seed:" + file.Source), cancellationToken);
        var chunk = new MemorySeedChunk(
            MemorySeedFileLoader.DeterministicId(item.Id + ":0:" + embedding.Provider + ":" + embedding.Model + ":" + embedding.Vector.Count),
            Position: 0, file.Content, ComputeSha256Hex(file.Content), embedding.Provider, embedding.Model,
            embedding.Vector.Count, embedding.Vector);
        return new MemorySeedEntry(item, [chunk]);
    }

    private MemorySeedItem CreateItem(MemorySeedFile file, string contentHash) =>
        new(MemorySeedFileLoader.DeterministicId(
                options.Value.TenantId + ":" + options.Value.Owner + ":" + file.Source + ":seed-v3"),
            options.Value.TenantId, file.Kind, file.Source, file.Title, file.Content, contentHash,
            Version: 1, file.Tags, file.ServiceName, file.Component, file.ReleaseName);

    private string ResolveRootDirectory()
    {
        var sourceDirectory = options.Value.SourceDirectory;
        return Path.IsPathRooted(sourceDirectory)
            ? sourceDirectory
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, sourceDirectory));
    }

    private static string ComputeSha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    [LoggerMessage(2301, LogLevel.Warning, "Memory seed runtime synchronization failed with {FailureType}.")]
    private static partial void LogRuntimeSyncFailed(ILogger logger, string failureType);

    public void Dispose()
    {
        // Normal shutdown disposes and clears resyncCancellation in StopAsync; this is a
        // defensive fallback in case the host tears this instance down without stopping it.
        resyncCancellation?.Dispose();
    }
}
