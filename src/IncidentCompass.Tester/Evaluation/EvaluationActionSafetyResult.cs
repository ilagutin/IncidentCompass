namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationActionSafetyResult(
    bool Passed,
    bool ObservationAvailable,
    bool EmptyActionGrantsConfigured,
    bool ExternalActionCredentialsProvided,
    IReadOnlyList<string> ObservedActionLifecycleEvents,
    string AuthorityBasis);
