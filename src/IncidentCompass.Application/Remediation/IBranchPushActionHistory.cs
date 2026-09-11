namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Reads what earlier branch pushes for the same incident and the same binding already did.
/// </summary>
/// <remarks>
/// It is the adapter-side half of at-most-once. The database claim is the load-bearing half - an
/// approved action is dispatchable exactly once for its lifetime - and this answers the question the
/// claim cannot: whether a previous action row already created the branch, or ended without knowing.
/// The ticket adapter answers the same question by searching the provider for a marker it embedded in
/// a body; a push does not have to, because the branch name is derived from the report and is
/// therefore the marker, and reading one reference is cheaper and more exact than a text search.
/// </remarks>
public interface IBranchPushActionHistory
{
    Task<BranchPushActionHistorySnapshot> ReadPriorAsync(
        Guid actionId,
        CancellationToken cancellationToken);
}
