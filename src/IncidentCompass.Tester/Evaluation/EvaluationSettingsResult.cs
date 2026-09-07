namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationSettingsResult(
    IReadOnlyList<EvaluationRouteSettingsResult> Routes,
    EvaluationOrchestratorBudgetResult OrchestratorBudget,
    bool EmptyActionGrantsConfigured,
    bool ExternalActionCredentialsProvided);
