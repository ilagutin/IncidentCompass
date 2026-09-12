namespace IncidentCompass.Tester.Evaluation;

// Provider and ProviderId are two different facts. Provider is the adapter that answered, which is
// one string for every OpenAI-compatible endpoint a host can reach; ProviderId is the entry in the
// triage configuration's provider table that the called route named, which is what has its own
// endpoint, credential and prices. Cost belongs to ProviderId, so both are recorded and neither is
// derived from the other. ProviderId is null when the ledger row does not state one.
internal sealed record EvaluationModelCallMetadata(
    string RouteId,
    string Model,
    string Provider,
    string? ProviderId,
    string UsageSource,
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    long DurationMs);
