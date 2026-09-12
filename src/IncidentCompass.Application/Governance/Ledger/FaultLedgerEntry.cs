using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Governance.Ledger;

/// <summary>
/// One appended ledger row paired with what its payload reference still resolves to. The pairing is
/// deliberate: <see cref="TriageLedgerEntry"/> is the durable row and must keep meaning exactly what
/// was written, while <see cref="PayloadState"/> is answered at read time and can differ between two
/// reads of the same row once retention has run in between.
/// </summary>
public sealed record FaultLedgerEntry(
    TriageLedgerEntry Entry,
    TriageLedgerPayloadState PayloadState);
