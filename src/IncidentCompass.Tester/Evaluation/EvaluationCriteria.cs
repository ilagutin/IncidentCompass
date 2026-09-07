namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationCriteria(
    EvaluationDiagnosisCriteria Diagnosis,
    EvaluationEvidenceCriteria Evidence,
    EvaluationRefusalCriteria Refusal,
    EvaluationToleranceCriteria Tolerance);
