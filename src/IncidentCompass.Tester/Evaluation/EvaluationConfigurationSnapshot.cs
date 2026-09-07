namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationConfigurationSnapshot(
    IReadOnlyList<EvaluationRouteSettingsResult> Routes,
    EvaluationOrchestratorBudgetResult OrchestratorBudget);
