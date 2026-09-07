namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationDiagnosisCriteria(
    IReadOnlyList<string> AllowedClassifications,
    IReadOnlyList<string> RequiredAnyTerms);
