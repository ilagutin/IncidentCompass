namespace IncidentCompass.Application.Core.Configuration;

/// <summary>
/// How long raw payloads stay readable, and how much one retention run may touch. The two windows
/// are separate because they protect different readers: the signal window is about how long anyone
/// still wants the raw ingested body, and the artifact window is about how long an operator
/// investigating an attempt that did not become the current one still needs that attempt's working
/// evidence.
/// </summary>
/// <remarks>
/// This lives in <c>Core</c> rather than beside either operation because both features read it:
/// <c>Intake</c> compacts signal payloads and <c>Investigation</c> reaps attempt artifacts, and an
/// operator setting a retention policy is setting one policy, not two.
/// </remarks>
public sealed class RetentionOptions
{
    public const string SectionName = "IncidentCompass:Retention";

    /// <summary>
    /// Days a signal keeps its raw <c>attributes</c> and <c>body</c>, counted from the time intake
    /// received it. Everything the pipeline derived from the signal - fingerprint, service,
    /// environment, summary, fault link - is unaffected.
    /// </summary>
    public int SignalPayloadRetentionDays { get; init; } = 30;

    /// <summary>
    /// Days an artifact belonging to an attempt that is no longer its job's current attempt is kept
    /// before it becomes reapable. The default is a week rather than zero on purpose: the attempt
    /// stops being current the moment the next one is claimed, and the artifacts of the attempt that
    /// went wrong are exactly what an operator opens when they come to look at why. A week covers
    /// the case where the failure happened on a Friday and is picked up the following Friday.
    /// </summary>
    public int AttemptArtifactRetentionDays { get; init; } = 7;

    /// <summary>
    /// Upper bound on rows one run of either operation may change. It bounds the write, the locks a
    /// run holds and the size of the transaction that rolls back if the run is interrupted.
    /// </summary>
    /// <remarks>
    /// It does not bound the work either operation does to find those rows, and the two differ there.
    /// Compaction is genuinely bounded: its whole predicate is on <c>signals</c>, so the partial
    /// index serves the scan and a run stops at roughly its own budget. The reap is not, because
    /// "stale attempt" is a comparison against <c>triage_jobs.attempt</c> that no index on
    /// <c>triage_artifacts</c> can hold. PostgreSQL either walks artifacts oldest first and checks
    /// each one's job, or scans the table and joins, depending on how many artifacts happen to be
    /// stale, and both read work proportional to the table. A first run over a long-lived database is
    /// therefore a full-table read that deletes at most this many rows. The measured plans are
    /// recorded in <c>infra/postgres/init/028-signal-payload-and-artifact-retention.sql</c>.
    /// </remarks>
    public int MaxRowsPerRun { get; init; } = 500;
}
