namespace IncidentCompass.Worker;

/// <summary>
/// Builds the <see cref="WorkerJobPump"/>'s <see cref="ClaimedTaskSet"/> and owns that pump's two
/// failure log events. The concurrency behaviour is shared; only the events are per pump, so the
/// ids documented in <c>docs/observability.md</c> stay distinct.
/// </summary>
internal static partial class WorkerJobTaskSet
{
    public static ClaimedTaskSet Create(ILogger<WorkerJobPump> logger) =>
        new(logger, LogJobFailedAfterClaim, LogJobFailedWhileDraining);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Warning, Message = "Claimed triage job processing failed after claim.")]
    private static partial void LogJobFailedAfterClaim(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1402, Level = LogLevel.Warning, Message = "Claimed triage job processing failed while draining the worker.")]
    private static partial void LogJobFailedWhileDraining(ILogger logger, Exception exception);
}
