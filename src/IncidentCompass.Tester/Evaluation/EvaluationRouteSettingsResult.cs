namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationRouteSettingsResult(
    string RouteId,
    string Kind,
    string ProviderId,
    string Model,
    double? Temperature,
    int? MaxOutputTokens,
    int? ContextWindowTokens);
