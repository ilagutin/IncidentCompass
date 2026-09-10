namespace IncidentCompass.Worker;

/// <summary>
/// Builds the <see cref="WorkerActionPump"/>'s <see cref="ClaimedTaskSet"/> and owns that pump's
/// two failure log events. The concurrency behaviour is shared; only the events are per pump, so
/// the ids documented in <c>docs/observability.md</c> stay distinct.
/// </summary>
internal static partial class WorkerActionTaskSet
{
    public static ClaimedTaskSet Create(ILogger<WorkerActionPump> logger) =>
        new(logger, LogDispatchFailedAfterClaim, LogDispatchFailedWhileDraining);

    [LoggerMessage(EventId = 1501, Level = LogLevel.Warning, Message = "Approved action dispatch failed after claim.")]
    private static partial void LogDispatchFailedAfterClaim(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1502, Level = LogLevel.Warning, Message = "Approved action dispatch failed while draining.")]
    private static partial void LogDispatchFailedWhileDraining(ILogger logger, Exception exception);
}
