namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationCaseSummary(
    string CaseId,
    string CaseKind,
    int RequestedAttempts,
    int RecordedAttempts,
    int CompletionPasses,
    int DiagnosisPasses,
    int EvidencePasses,
    int RefusalPasses,
    int SafetyPasses,
    bool TolerancePassed);
