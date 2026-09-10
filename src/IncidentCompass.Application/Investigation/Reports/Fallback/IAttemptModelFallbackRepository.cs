namespace IncidentCompass.Application.Investigation.Reports.Fallback;

/// <summary>
/// Reads which of a job attempt's model calls were answered by a route's fallback rather than by the
/// route itself, so publication can state a degraded run the model was never asked about.
/// </summary>
public interface IAttemptModelFallbackRepository
{
    /// <summary>
    /// Returns one entry per distinct route pairing that answered in this attempt, in the order the
    /// pairings first answered, and an empty list when nothing failed over.
    /// </summary>
    /// <remarks>
    /// Only calls that succeeded on the fallback count. A fail-over that failed as well ended the
    /// attempt, so no report is being published from it, and a failed call contributed nothing a
    /// report is built on either way.
    /// </remarks>
    Task<IReadOnlyList<AttemptModelFallback>> ReadCurrentAttemptAsync(
        Guid jobId,
        int attempt,
        CancellationToken cancellationToken);
}
