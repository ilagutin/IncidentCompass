using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Core.Health;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Worker;

internal sealed partial class Worker(
    ILogger<Worker> logger,
    IServiceScopeFactory serviceScopeFactory,
    IOptions<WorkerOptions> options,
    WorkerJobPump jobPump)
    : BackgroundService
{
    private readonly string workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await TryLogStartupStatusAsync(stoppingToken);

        var consecutiveErrors = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await jobPump.ObserveCompletedAsync(stoppingToken);
                    await jobPump.FillAvailableSlotsAsync(workerId, options.Value, stoppingToken);
                    consecutiveErrors = 0;
                    var delay = WorkerPollDelay.Calculate(options.Value.PollIntervalSeconds, consecutiveErrors, Random.Shared.NextDouble());
                    await jobPump.WaitForNextWakeAsync(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    consecutiveErrors++;
                    LogWorkerPollFailed(logger, exception);
                    var delay = WorkerPollDelay.Calculate(options.Value.PollIntervalSeconds, consecutiveErrors, Random.Shared.NextDouble());
                    await Task.Delay(delay, stoppingToken);
                }
            }
        }
        finally
        {
            await jobPump.DrainAsync();
        }
    }

    private async Task TryLogStartupStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>();

            var health = await dispatcher.DispatchAsync<GetHealthStatusQuery, HealthStatus>(
                new GetHealthStatusQuery("worker"),
                cancellationToken);

            LogWorkerStarted(
                logger,
                workerId,
                options.Value.MaxConcurrentJobs,
                health.Status,
                health.CheckedAtUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogStartupHealthCheckFailed(logger, exception);
        }
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "IncidentCompass Worker {WorkerId} started with MaxConcurrentJobs {MaxConcurrentJobs}, application status {Status} at {CheckedAtUtc}")]
    private static partial void LogWorkerStarted(
        ILogger logger,
        string workerId,
        int maxConcurrentJobs,
        string status,
        DateTimeOffset checkedAtUtc);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Worker startup health check failed. Polling will continue.")]
    private static partial void LogStartupHealthCheckFailed(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "Worker polling failed. Polling will continue after backoff.")]
    private static partial void LogWorkerPollFailed(
        ILogger logger,
        Exception exception);
}
