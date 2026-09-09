namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Classifies attempt failures that are permanent for the job rather than transient. Budget
/// exhaustion, governance denial and exhausted worker-output corrections cannot succeed on a later
/// attempt with the same configuration, so the runner dead-letters them with their own error code.
/// The inner-exception chain is walked because bounded paths can wrap the original failure.
/// </summary>
internal static class TriageNonRetryableFailureClassifier
{
    public static string? TryGetErrorCode(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case TriageBudgetExhaustedException budgetExhausted:
                    return budgetExhausted.ErrorCode;
                case TriageGovernanceDeniedException governanceDenied:
                    return governanceDenied.ErrorCode;
                case WorkerOutputInvalidException:
                    return WorkerOutputInvalidException.ErrorCode;
                default:
                    continue;
            }
        }

        return null;
    }
}
