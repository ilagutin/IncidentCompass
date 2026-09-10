namespace IncidentCompass.Application.Intake.Retention;

/// <summary>
/// Empties the raw payload columns of signals intake received before a cutoff, leaving the signal
/// row itself and every reference to it intact.
/// </summary>
/// <remarks>
/// The signal row cannot be deleted: <c>faults.trigger_signal_id</c> has a foreign key to it, so a
/// fault would lose the signal that opened it. Compaction therefore means emptying the two raw
/// payload columns and recording that it happened; the derived fields the pipeline reasons over are
/// untouched.
/// </remarks>
public interface ISignalPayloadCompactionRepository
{
    /// <summary>
    /// Compacts at most <paramref name="maxRows"/> signals received strictly before
    /// <paramref name="receivedBeforeUtc"/> that have not already been compacted, and returns how
    /// many rows this run compacted. Calling it again with the same arguments compacts nothing.
    /// </summary>
    Task<int> CompactAsync(
        DateTimeOffset receivedBeforeUtc,
        int maxRows,
        CancellationToken cancellationToken);
}
