namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// A delegate call the backend refused before running a worker because an equivalent delegate had
/// already returned the same worker output <c>Orchestrator.Budget.MaxEquivalentCalls</c> times in a
/// row. The refusal is already recorded when this is thrown; the orchestrator loop turns it into a
/// tool message and carries on. It is not a validation failure, so it charges no reprompt and never
/// fails the attempt.
/// </summary>
internal sealed class RepeatedDelegateRefusedException()
    : Exception(InvestigationNoProgressRecorder.RepeatedCallErrorMessage)
{
}
