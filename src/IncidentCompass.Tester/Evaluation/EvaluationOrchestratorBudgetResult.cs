namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationOrchestratorBudgetResult(
    int MaxWorkers,
    int MaxTokens,
    int MaxWallClockSeconds,
    int? MaxReprompts);
