namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationModelCallMetadata(
    string RouteId,
    string Model,
    string Provider,
    string UsageSource,
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    long DurationMs);
