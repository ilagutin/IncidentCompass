namespace IncidentCompass.Application.Governance.Ledger.GetFaultLedger;

public sealed record FaultLedgerEventResponse(
    long Id,
    Guid JobId,
    int Attempt,
    string EventType,
    string? Role,
    string? ToolName,
    string? Decision,
    string? Rationale,
    string? DecisionReason,
    string? ToolStatus,
    int? TokensDelta,
    int? WorkersDelta,
    string? PayloadRef,
    string PayloadState,
    string ConfigHash,
    DateTimeOffset CreatedAtUtc);
