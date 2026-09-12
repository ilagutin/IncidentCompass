namespace IncidentCompass.Application.Investigation.Reports.Fallback;

/// <summary>
/// One route pairing that actually answered during a job attempt: a call declared on
/// <paramref name="RouteId" /> that its provider failed, answered instead by
/// <paramref name="FallbackRouteId" />.
/// </summary>
/// <param name="RouteId">The route the call was configured on and whose provider failed it.</param>
/// <param name="FallbackRouteId">The route that answered in its place.</param>
public sealed record AttemptModelFallback(string RouteId, string FallbackRouteId);
