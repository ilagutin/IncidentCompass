namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationUsageResult(
    string Availability,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    IReadOnlyList<string> UsageSources,
    int MalformedModelCallRows,
    string FailedCallUsageAvailability,
    string AccountingBasis);
