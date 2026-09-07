namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationRunResult(
    int SchemaVersion,
    string CorpusVersion,
    string EvaluatedRevision,
    string EvaluatedContentIdentity,
    bool EvaluatedContentDirty,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int RequestedAttemptsPerCase,
    EvaluationSettingsResult Settings,
    IReadOnlyList<EvaluationAttemptResult> Attempts,
    IReadOnlyList<EvaluationCaseSummary> Cases,
    EvaluationMetricSummary Metrics,
    bool Passed,
    string Interpretation);
