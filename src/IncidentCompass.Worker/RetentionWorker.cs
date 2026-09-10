using Microsoft.Extensions.Options;

namespace IncidentCompass.Worker;

/// <summary>
/// Drives one retention pass per interval. It is the only thing in the system that runs the two
/// retention operations on a schedule.
/// </summary>
/// <remarks>
/// The default interval is fifteen minutes, which is a compromise between the two cases that matter.
/// In the steady state a pass finds almost nothing and costs one artifact-table read, so 96 of them a
/// day is noise next to the investigation traffic on the same database. Over an accumulated backlog
/// the interval is what sets the drain rate: at the default row budget it clears 48,000 rows a day,
/// so even a database carrying the several hundred thousand stale rows measured in
/// <c>infra/postgres/init/028-signal-payload-and-artifact-retention.sql</c> is caught up within a
/// week, without a single unbounded run. An operator who wants the backlog gone sooner raises
/// <c>IncidentCompass:Retention:MaxRowsPerRun</c> rather than shortening the interval, because the
/// budget is the part of the cost that scales with rows and the interval is the part that repeats
/// the table read.
/// </remarks>
internal sealed partial class RetentionWorker(
    ILogger<RetentionWorker> logger,
    IOptions<RetentionScheduleOptions> options,
    RetentionPump retentionPump) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            LogRetentionDisabled(logger, RetentionScheduleOptions.SectionName);
            return;
        }

        var consecutiveErrors = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await retentionPump.RunOnceAsync(stoppingToken);
                consecutiveErrors = 0;
                await Task.Delay(CalculateDelay(options.Value, consecutiveErrors), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                consecutiveErrors++;
                LogRetentionPassFailed(logger, exception);
                await Task.Delay(CalculateDelay(options.Value, consecutiveErrors), stoppingToken);
            }
        }
    }

    private static TimeSpan CalculateDelay(RetentionScheduleOptions options, int consecutiveErrors) =>
        WorkerPollDelay.Calculate(
            options.IntervalMinutes * 60,
            consecutiveErrors,
            Random.Shared.NextDouble());

    [LoggerMessage(
        EventId = 1801,
        Level = LogLevel.Information,
        Message = "Retention is disabled by {SectionName}:Enabled. No signal payload is compacted and no attempt artifact is reaped by this host.")]
    private static partial void LogRetentionDisabled(ILogger logger, string sectionName);

    [LoggerMessage(
        EventId = 1802,
        Level = LogLevel.Warning,
        Message = "Retention pass failed before either operation could run. The next pass follows after backoff.")]
    private static partial void LogRetentionPassFailed(ILogger logger, Exception exception);
}
