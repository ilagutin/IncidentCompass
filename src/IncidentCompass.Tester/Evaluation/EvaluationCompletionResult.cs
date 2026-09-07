namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationCompletionResult(
    bool Passed,
    string? JobStatus,
    string? ReportStatus,
    bool ReportSchemaValid);
