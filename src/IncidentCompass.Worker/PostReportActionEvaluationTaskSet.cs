namespace IncidentCompass.Worker;

/// <summary>
/// Builds the <see cref="PostReportActionEvaluationPump"/>'s <see cref="ClaimedTaskSet"/> and owns
/// that pump's two failure log events. The concurrency behaviour is shared; only the events are per
/// pump, so the ids documented in <c>docs/observability.md</c> stay distinct.
/// </summary>
internal static partial class PostReportActionEvaluationTaskSet
{
    public static ClaimedTaskSet Create(ILogger<PostReportActionEvaluationPump> logger) =>
        new(logger, LogEvaluationFailedAfterClaim, LogEvaluationFailedWhileDraining);

    [LoggerMessage(EventId = 1601, Level = LogLevel.Warning, Message = "Post-report action evaluation failed after claim.")]
    private static partial void LogEvaluationFailedAfterClaim(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1602, Level = LogLevel.Warning, Message = "Post-report action evaluation failed while draining.")]
    private static partial void LogEvaluationFailedWhileDraining(ILogger logger, Exception exception);
}
