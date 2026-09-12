namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationRefusalCriteria(
    bool Required,
    IReadOnlyList<string> AllowedStatuses,
    int MinimumLimitations);
