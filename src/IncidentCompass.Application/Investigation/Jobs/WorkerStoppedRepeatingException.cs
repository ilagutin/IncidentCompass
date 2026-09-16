namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Ends one worker run early because its model kept proposing calls the backend refused as repeats
/// without new evidence. The delegate executor turns it into a structured delegate result the
/// orchestrator can act on; it never reaches the job runner, so it neither dead-letters the job nor
/// spends a schema reprompt.
/// </summary>
internal sealed class WorkerStoppedRepeatingException(int consecutiveRefusals)
    : Exception("The worker run was stopped after consecutive refused repeat calls.")
{
    /// <summary>The code the orchestrator receives in the delegate result.</summary>
    internal const string ErrorCode = "worker_stopped_repeating";

    /// <summary>The fixed message the orchestrator receives. It names no argument, task or output.</summary>
    internal const string ErrorMessage =
        "The worker was stopped without an answer: it kept asking for results this attempt already has. " +
        "Use the evidence already returned, delegate a different task, or publish_report.";

    public int ConsecutiveRefusals { get; } = consecutiveRefusals;
}
