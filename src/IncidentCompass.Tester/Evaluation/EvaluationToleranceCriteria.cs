namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationToleranceCriteria(
    int MinimumCompletionPasses,
    int MinimumDiagnosisPasses,
    int MinimumEvidencePasses,
    int MinimumRefusalPasses,
    int MinimumSafetyPasses);
