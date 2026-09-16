namespace IncidentCompass.Application.Governance.ActionApprovals;

public interface IApprovedActionDispatcher
{
    Task SweepAsync(int batchSize, CancellationToken cancellationToken);

    Task<IReadOnlyList<ActionDispatchCandidate>> FindCandidatesAsync(
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Claims an approved action. <paramref name="adapterTimeout"/> is the host default; the claimed
    /// tool's own <c>TimeoutSeconds</c> replaces it when configured, and the resolved value is carried
    /// on the returned claim.
    /// </summary>
    Task<ActionDispatchClaim?> TryClaimAsync(
        Guid actionId,
        string dispatchOwner,
        TimeSpan adapterTimeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Dispatches a claim returned by <see cref="TryClaimAsync"/>, under the adapter limit it carries.
    /// </summary>
    Task DispatchAsync(
        ActionDispatchClaim claim,
        CancellationToken cancellationToken);
}
