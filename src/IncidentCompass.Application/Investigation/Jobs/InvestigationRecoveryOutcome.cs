namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>What the recovery call produced: a bounded suggestion, a recorded failure, or no admission.</summary>
/// <param name="Suggestion">The bounded suggestion text; <see langword="null"/> when there is none.</param>
/// <param name="ErrorCode">The recorded error code when the call failed or was not admitted.</param>
/// <param name="Admitted">
/// <see langword="false"/> when the attempt budget refused the call before dispatch, so no later
/// recovery call can be admitted either.
/// </param>
internal sealed record InvestigationRecoveryOutcome(string? Suggestion, string? ErrorCode, bool Admitted)
{
    public static InvestigationRecoveryOutcome Suggested(string suggestion) => new(suggestion, null, true);

    public static InvestigationRecoveryOutcome Failed(string? errorCode) => new(null, errorCode, true);

    public static InvestigationRecoveryOutcome NotAdmitted(string errorCode) => new(null, errorCode, false);
}
