using IncidentCompass.Application.Core.ModelClients;

namespace IncidentCompass.Application.Intake.Configuration;

/// <summary>
/// One configured route: which provider answers it, with which model, under which ceilings.
/// </summary>
/// <remarks>
/// <c>FallbackRouteId</c> names another chat route that answers this route's call when the provider
/// fails it in a way a different provider could plausibly answer. Absent means no fail-over, which
/// is what every shipped route does. The declaration is per route; the decision to use it belongs to
/// one model call, so nothing here says a job fails over - see <c>docs/model-gateway.md</c>, "Route
/// Fallback". The named route is not itself followed when it fails: one hop is the whole feature,
/// because a chain would multiply worst-case wall clock against an attempt budget sized for a single
/// provider timeout.
/// </remarks>
public sealed record TriageRouteSettings(
    string Kind,
    string ProviderId,
    string Model,
    double? Temperature,
    int? MaxOutputTokens,
    int? ContextWindowTokens,
    AiReasoningLevel? Reasoning = null,
    string? FallbackRouteId = null);
