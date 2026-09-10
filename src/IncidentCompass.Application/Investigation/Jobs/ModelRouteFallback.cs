using IncidentCompass.Application.Intake.Configuration;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// The route one failed model call may be retried on: the configured id and the settings behind it,
/// resolved together so the caller never has to look the id up a second time.
/// </summary>
/// <param name="RouteId">The configured id of the fallback route, as it is written in the ledger.</param>
/// <param name="Route">The fallback route's own model, ceilings, provider and reasoning preference.</param>
internal sealed record ModelRouteFallback(string RouteId, TriageRouteSettings Route);
