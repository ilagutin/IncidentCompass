namespace IncidentCompass.Tester;

internal sealed record TriageJobSummary(
    Guid Id,
    string Status,
    int Attempt,
    string ConfigHash,
    DateTimeOffset CreatedAtUtc);
