namespace IncidentCompass.Application.Core.Errors;

public enum ProviderFailureKind
{
    Unknown,
    Unavailable,
    RejectedRequest,
    GenerationTimeout,
    OutputLimitReached,
    AmbiguousInterruption,
    TransportFailure,
    InvalidResponse,

    /// <summary>
    /// The request is well formed, but this host cannot serve it until an operator changes what is
    /// installed or configured, for example a local model that is missing or is not the one the
    /// request names. It is not an outage: it does not feed the outage pause, and a job meeting it
    /// retries inside its ordinary attempt budget, because a fix and restart can land in between.
    /// </summary>
    ConfigurationRequired
}
