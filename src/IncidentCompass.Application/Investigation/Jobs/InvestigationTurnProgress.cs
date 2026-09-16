namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// What <see cref="InvestigationProgressTracker.CompleteTurn"/> concluded about one orchestrator turn.
/// </summary>
/// <param name="MadeProgress">The turn added evidence not seen before in the attempt or changed the candidate classification.</param>
/// <param name="TurnsWithoutProgress">Consecutive turns without progress after this turn.</param>
/// <param name="LimitExceeded">
/// <paramref name="TurnsWithoutProgress"/> is past the configured
/// <c>Orchestrator.Budget.MaxTurnsWithoutProgress</c>.
/// </param>
internal readonly record struct InvestigationTurnProgress(
    bool MadeProgress,
    int TurnsWithoutProgress,
    bool LimitExceeded);
