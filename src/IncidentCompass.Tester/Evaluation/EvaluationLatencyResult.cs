namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationLatencyResult(
    long EndToEndMilliseconds,
    long? ModelCallTotalMilliseconds,
    IReadOnlyList<long> ModelCallMilliseconds);
