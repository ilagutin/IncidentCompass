using IncidentCompass.Application.Intake.Retention;
using IncidentCompass.Application.Investigation.Retention;

namespace IncidentCompass.Worker;

/// <summary>
/// One retention pass: a single bounded run of signal payload compaction followed by a single
/// bounded run of attempt artifact reaping, in one scope.
/// </summary>
/// <remarks>
/// <para>
/// A pass runs each operation exactly once, and neither drains its backlog. The row budget is the
/// only thing bounding a run, and for the reap it bounds what the run deletes, not what it reads:
/// "not the job's current attempt" compares against a column in another table, so every run reads
/// work proportional to the artifact table whatever the backlog is
/// (<c>infra/postgres/init/028-signal-payload-and-artifact-retention.sql</c> records the measured
/// plans). Looping until nothing is left would therefore repeat a table-sized read once per budget
/// worth of rows, on the same database the Worker claims jobs from, for as long as the backlog
/// lasted. One pass per tick costs the same whether the backlog is a day old or a year old, and the
/// backlog drains at a rate the operator can compute: the row budget divided by the interval.
/// </para>
/// <para>
/// The two operations are independent, so one failing does not stop the other: retention is
/// maintenance, and a transient database error on one statement is no reason to skip the other or to
/// stop the loop. Cancellation is the exception - it is rethrown rather than logged as a failure, so
/// a shutdown ends the pass immediately instead of being retried as if it were an error.
/// </para>
/// </remarks>
internal sealed partial class RetentionPump(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<RetentionPump> logger)
{
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        await CompactSignalPayloadsAsync(scope.ServiceProvider, cancellationToken);
        await ReapAttemptArtifactsAsync(scope.ServiceProvider, cancellationToken);
    }

    private async Task CompactSignalPayloadsAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        try
        {
            var compactor = services.GetRequiredService<AgedSignalPayloadCompactor>();
            await compactor.CompactAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogCompactionFailed(logger, exception);
        }
    }

    private async Task ReapAttemptArtifactsAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        try
        {
            var reaper = services.GetRequiredService<StaleAttemptArtifactReaper>();
            await reaper.ReapAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogReapFailed(logger, exception);
        }
    }

    [LoggerMessage(
        EventId = 1901,
        Level = LogLevel.Warning,
        Message = "Signal payload compaction failed. Artifact reaping still runs, and compaction is retried on the next retention pass.")]
    private static partial void LogCompactionFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1902,
        Level = LogLevel.Warning,
        Message = "Attempt artifact reaping failed. It is retried on the next retention pass.")]
    private static partial void LogReapFailed(ILogger logger, Exception exception);
}
