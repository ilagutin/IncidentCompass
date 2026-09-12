namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationModelCallReadResult(
    IReadOnlyList<EvaluationModelCallMetadata> Calls,
    int MalformedRows);
