namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationCase(
    string Id,
    string Kind,
    EvaluationSignal Input,
    EvaluationCriteria Criteria)
{
    internal IncidentEnvelope CreateEnvelope(string runId, int attempt)
    {
        var unique = runId + "-" + Id + "-" + attempt;
        var attemptKey = Input.AttemptKeys[attempt - 1];
        return new IncidentEnvelope(
            "tester",
            Expand(Input.ServiceName, runId, attemptKey),
            Input.Environment,
            Input.Severity,
            DateTimeOffset.UtcNow,
            new IncidentCorrelation("trace-" + unique, "span-" + unique, "evaluation-" + unique),
            new IncidentAttributes(
                Input.ErrorType,
                Expand(Input.ErrorMessage, runId, attemptKey),
                Expand(Input.Route, runId, attemptKey),
                Expand(Input.Operation, runId, attemptKey),
                500),
            new Dictionary<string, object?>
            {
                ["evaluationCorpus"] = "triage-evaluation-corpus-v1",
                ["evaluationCase"] = Id,
                ["evaluationAttempt"] = attempt
            });
    }

    private static string Expand(string value, string runId, string attemptKey) =>
        value.Replace("{runId}", runId, StringComparison.Ordinal)
            .Replace("{attemptKey}", attemptKey, StringComparison.Ordinal);
}
