namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The trigger signal a benchmark query stands for, carried as the description fields an ingested
/// signal has. The query text is what a model would send to <c>memory_search</c>; the signal is the
/// fault the backend itself received, so a judgement made against the signal cannot be raised by a
/// model choosing its query wording. <c>Summary</c> is not carried because the backend synthesizes it
/// from these fields when a signal arrives without one.
/// </summary>
public sealed record MemoryRetrievalBenchmarkSignal(
    string ServiceName,
    string ErrorType,
    string ErrorMessage,
    string? HttpRoute,
    string? OperationName);
