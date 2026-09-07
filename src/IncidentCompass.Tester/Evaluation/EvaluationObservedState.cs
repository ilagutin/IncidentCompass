namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationObservedState(
    FaultDetailsResponse? Fault,
    FaultLedgerResponse Ledger,
    TriageReportResponse? Report,
    bool LedgerObservationAvailable,
    bool ExpectedJobMatched);
