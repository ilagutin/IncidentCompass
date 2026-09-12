namespace IncidentCompass.Application.Governance.Ledger;

/// <summary>
/// What a fault-ledger event's <c>payload_ref</c> still resolves to at the moment the timeline is
/// read. It is a derived read-time fact, not a stored column: the ledger is append-only and never
/// learns that a payload it points at was later removed.
/// </summary>
/// <remarks>
/// This exists because <c>payload_ref</c> is plain text with no foreign key. Reconstruction after
/// retention has run therefore does not fail, but without this a reader could not tell a reference
/// whose payload is still stored from one whose payload is gone. Both render as the same string.
/// </remarks>
public enum TriageLedgerPayloadState
{
    /// <summary>The event carries no payload reference at all.</summary>
    None = 0,

    /// <summary>
    /// The event references an attempt artifact and that artifact row is still stored, so the
    /// detailed payload behind this timeline entry can still be read.
    /// </summary>
    Retained = 1,

    /// <summary>
    /// The event references an attempt artifact that is no longer stored, and attempt-artifact
    /// retention is what removed it.
    /// </summary>
    /// <remarks>
    /// The stronger half of that claim - "reaped" rather than merely "absent" - rests on two facts
    /// about the writers rather than on a database constraint. An <c>artifact:</c> reference is only
    /// ever written by <c>PostgresTriageToolResultCommitter</c>, which inserts the artifact row and
    /// the ledger row in one transaction, so such a reference never named a row that did not exist.
    /// And the reap in <c>PostgresAttemptArtifactRetentionRepository</c> is the only statement in the
    /// system that deletes from <c>triage_artifacts</c>. A future second deleter would weaken this
    /// value back to "absent", which is the reason both halves are written down here.
    /// </remarks>
    Reaped = 2,

    /// <summary>
    /// The reference does not name an attempt artifact, so retention cannot remove what it points
    /// at. Reports (<c>report:</c>) and action approvals (<c>action:</c>) are append-only records
    /// whose delete triggers reject removal outright, and a model-call reference is a correlation
    /// key with no stored payload behind it.
    /// </summary>
    NotReapable = 3
}
