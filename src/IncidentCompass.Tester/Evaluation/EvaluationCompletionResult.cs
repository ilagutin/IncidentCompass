namespace IncidentCompass.Tester.Evaluation;

// JobLastErrorCode and JobNextAttemptAtUtc are projected from the same durable job row as JobStatus
// and are recorded beside it, so an attempt that ended without a report says which bounded outcome
// the backend reached instead of only that it reached one. They are recorded verbatim: this harness
// does not map, group or judge the codes it records.
internal sealed record EvaluationCompletionResult(
    bool Passed,
    string? JobStatus,
    string? JobLastErrorCode,
    DateTimeOffset? JobNextAttemptAtUtc,
    string? ReportStatus,
    bool ReportSchemaValid);
