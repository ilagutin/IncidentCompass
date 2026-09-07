namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationReportSnapshot(
    Guid ReportId,
    string Status,
    string Summary,
    string RecommendedNextAction,
    string Classification,
    string DocumentationFit,
    string ConfigHash,
    IReadOnlyList<string> Limitations,
    int OmittedLimitationCount,
    IReadOnlyList<EvaluationEvidenceSnapshot> Evidence,
    int OmittedEvidenceCount);
