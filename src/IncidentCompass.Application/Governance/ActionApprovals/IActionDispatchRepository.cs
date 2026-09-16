namespace IncidentCompass.Application.Governance.ActionApprovals;

public interface IActionDispatchRepository
{
    Task<IReadOnlyList<ActionDispatchCandidate>> FindCandidatesAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ActionDispatchCandidate>> FindExpiryCandidatesAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ActionDispatchCandidate>> FindSupersededCandidatesAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ActionDispatchCandidate>> FindRecoveryCandidatesAsync(
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Claims an approved action with a deadline chosen for its tool. The resolver receives the
    /// locked row's tool id and must be pure: the configuration it reads from is loaded before the
    /// claim transaction opens, so the transaction does no more I/O than a fixed-deadline claim.
    /// </summary>
    Task<ActionDispatchClaim?> TryClaimAsync(
        Guid actionId,
        string dispatchOwner,
        Func<string, TimeSpan> timeoutWithRecoveryGraceForTool,
        CancellationToken cancellationToken);

    Task<bool> CompleteAsync(
        ActionTerminalRequest request,
        CancellationToken cancellationToken);

    Task<bool> TryExpireAsync(Guid actionId, CancellationToken cancellationToken);

    Task<bool> TryFailSupersededAsync(Guid actionId, CancellationToken cancellationToken);

    Task<bool> TryFailOutcomeUnknownAsync(Guid actionId, CancellationToken cancellationToken);
}
