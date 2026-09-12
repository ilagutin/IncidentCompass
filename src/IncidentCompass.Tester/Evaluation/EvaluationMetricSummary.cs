namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationMetricSummary(
    int RequestedAttempts,
    int RecordedAttempts,
    int FailedAttempts,
    int AttemptsWithModelUsage,
    int AttemptsWithoutCompleteModelUsage,
    EvaluationRange? EndToEndMilliseconds,
    EvaluationRange? ModelCallMilliseconds,
    EvaluationRange? TotalTokens);
