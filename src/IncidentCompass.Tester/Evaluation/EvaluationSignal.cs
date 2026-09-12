namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationSignal(
    string ServiceName,
    string Environment,
    string Severity,
    string ErrorType,
    string ErrorMessage,
    string Route,
    string Operation,
    IReadOnlyList<string> AttemptKeys);
