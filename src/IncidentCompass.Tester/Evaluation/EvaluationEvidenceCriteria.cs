namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationEvidenceCriteria(
    int MinimumCitations,
    IReadOnlyList<string> RequiredAnyArtifactKinds,
    bool RequireCurrentAttempt,
    bool RequireStaleDocumentation);
