namespace IncidentCompass.Infrastructure.Memory;

internal sealed class MemorySeedSyncStatus : IMemorySeedSyncStatus
{
    private readonly object gate = new();
    private MemorySeedSyncSnapshot snapshot = new(false, false, null, null, null, null);

    public MemorySeedSyncSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot;
            }
        }
    }

    public void Configure(bool enabled, bool runtimeResyncEnabled)
    {
        lock (gate)
        {
            snapshot = snapshot with
            {
                Enabled = enabled,
                RuntimeResyncEnabled = runtimeResyncEnabled
            };
        }
    }

    public void RecordAttempt(DateTimeOffset timestamp)
    {
        lock (gate)
        {
            snapshot = snapshot with { LastAttemptAtUtc = timestamp, LastErrorCode = null };
        }
    }

    public void RecordSuccess(DateTimeOffset timestamp, Guid generation)
    {
        lock (gate)
        {
            snapshot = snapshot with
            {
                LastSuccessAtUtc = timestamp,
                ActiveGeneration = generation,
                LastErrorCode = null
            };
        }
    }

    /// <summary>
    /// Records that the pass declined to publish because the configured embedding route no longer
    /// matches the corpus.
    /// </summary>
    /// <remarks>
    /// The generation reported is the one the database still calls current, read from the corpus
    /// itself rather than from this process, because the host that published it may not be the host
    /// that noticed the route change. The last success timestamp is left as it stands: a host that
    /// has never synchronized successfully has no honest value to put there, and inventing one
    /// would claim a success that did not happen in this process.
    /// </remarks>
    public void RecordRebuildRequired(string errorCode, Guid? activeGeneration)
    {
        lock (gate)
        {
            snapshot = snapshot with
            {
                ActiveGeneration = activeGeneration ?? snapshot.ActiveGeneration,
                LastErrorCode = errorCode
            };
        }
    }

    public void RecordFailure()
    {
        lock (gate)
        {
            snapshot = snapshot with { LastErrorCode = "memory_sync_failed" };
        }
    }
}
