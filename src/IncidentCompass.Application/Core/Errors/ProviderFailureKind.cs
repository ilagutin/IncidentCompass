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
    InvalidResponse
}
