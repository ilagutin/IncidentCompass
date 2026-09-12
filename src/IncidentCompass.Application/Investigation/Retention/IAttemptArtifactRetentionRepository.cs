namespace IncidentCompass.Application.Investigation.Retention;

/// <summary>
/// Deletes triage artifacts that belong to an attempt which is no longer their job's current
/// attempt and that nothing durable still points at.
/// </summary>
/// <remarks>
/// <para>
/// "Failed attempt" is not a fact this system records. A retry that does not consume an attempt
/// reuses the same attempt number, so the rows of a run that went wrong and the rows of the run that
/// replaced it are indistinguishable by attempt. The only predicate that is actually implementable
/// is "not the job's current attempt", and that is what this port means. It is the conservative
/// direction: reusing the attempt number keeps both runs' artifacts.
/// </para>
/// <para>
/// The adapter is responsible for excluding every artifact something else still references. That is
/// not only a matter of avoiding an error: one of those references carries no foreign key, so
/// nothing in the database would refuse the delete or report the orphan afterwards.
/// </para>
/// </remarks>
public interface IAttemptArtifactRetentionRepository
{
    /// <summary>
    /// Reaps at most <paramref name="maxRows"/> eligible artifacts created strictly before
    /// <paramref name="createdBeforeUtc"/>, oldest first, and returns how many this run deleted.
    /// Calling it again reaps whatever is still eligible and nothing that was already gone.
    /// </summary>
    Task<int> ReapAsync(
        DateTimeOffset createdBeforeUtc,
        int maxRows,
        CancellationToken cancellationToken);
}
