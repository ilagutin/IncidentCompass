namespace IncidentCompass.Tester.Evaluation;

internal static class EvaluationAttemptFailureFactory
{
    public static EvaluationAttemptResult Create(
        EvaluationCase evaluationCase,
        int attempt,
        DateTimeOffset startedAtUtc,
        long elapsedMilliseconds,
        string phase,
        IngestSignalResponse? ingested,
        string failure,
        EvaluationActionSafetyResult safety) =>
        new(
            evaluationCase.Id,
            evaluationCase.Kind,
            attempt,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            phase,
            ingested?.FaultId,
            ingested?.JobId,
            ingested?.ConfigHash,
            null,
            null,
            [],
            [],
            null,
            new EvaluationCompletionResult(false, null, null, null, null, false),
            new EvaluationCriterionResult(false, "not evaluated: " + failure),
            new EvaluationCriterionResult(false, "not evaluated: " + failure),
            new EvaluationCriterionResult(false, "not evaluated: " + failure),
            safety,
            new EvaluationLatencyResult(elapsedMilliseconds, null, []),
            [],
            new EvaluationUsageResult("unavailable", null, null, null, [], 0, "unavailable", "No readable ModelCall rows; BudgetEvent tokens are never added."),
            EvaluationFailureDetail.Bound(failure));
}
