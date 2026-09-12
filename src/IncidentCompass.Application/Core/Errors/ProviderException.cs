namespace IncidentCompass.Application.Core.Errors;

/// <summary>
/// The base of every provider failure the Application layer declares.
/// </summary>
/// <remarks>
/// Nothing here is transport-shaped. An earlier version carried the provider's
/// <c>HttpStatusCode</c>, which made the base of every Application provider error depend on an HTTP
/// type that a non-HTTP adapter has no honest value for, against the rule in <c>CLAUDE.md</c> that
/// provider HTTP detail must not leak into Application contracts. What a status meant is already
/// carried in transport-neutral form: <see cref="ErrorCode" /> is the normalized closed vocabulary
/// the Application layer decides on, <see cref="FailureKind" /> is the Application enum every
/// retry, fallback and outage decision switches over, and <see cref="ProviderErrorCode" /> is the
/// provider's own string when it gave one. Mapping a status onto those is the adapter's job, and it
/// happens before the exception is constructed.
/// </remarks>
public abstract class ProviderException : IncidentCompass.Application.Core.Exceptions.AppException
{
    protected ProviderException(
        string provider,
        string message,
        string? errorCode = null,
        string? providerErrorCode = null,
        Exception? innerException = null,
        ProviderFailureKind failureKind = ProviderFailureKind.Unknown)
        : base(message, innerException)
    {
        Provider = provider;
        ErrorCode = errorCode;
        ProviderErrorCode = providerErrorCode;
        FailureKind = failureKind;
    }

    public string Provider { get; }

    public string? ErrorCode { get; }

    public string? ProviderErrorCode { get; }

    public ProviderFailureKind FailureKind { get; }
}
