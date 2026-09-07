namespace IncidentCompass.Tester;

internal sealed record FaultDetailsResponse(Guid Id, string Status, TriageJobSummary? Job);
