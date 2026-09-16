namespace IncidentCompass.Application.Core.Observability;

public enum RuntimeTelemetryOutcome
{
    Claimed,
    Succeeded,
    Failed,
    Cancelled,
    ProviderUnavailable,
    Denied,

    /// <summary>
    /// Allowed by policy but not executed, because an equivalent call already returned the same result
    /// in this attempt.
    /// </summary>
    Refused
}
