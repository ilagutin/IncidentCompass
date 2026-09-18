using IncidentCompass.Application.Core.Errors;

namespace IncidentCompass.Application.Memory;

/// <summary>
/// The bounded failure of <see cref="IMemoryRelevanceJudge" />, carrying the same three things every
/// provider failure in this codebase carries: the normalized error code the Application layer
/// branches on, the adapter's own code, and the failure kind retry, fallback and outage decisions
/// switch over. No message may carry the query, a candidate or a score.
/// </summary>
public sealed class MemoryRelevanceJudgeException : ProviderException
{
    public MemoryRelevanceJudgeException(
        string provider,
        string message,
        string? errorCode = null,
        string? providerErrorCode = null,
        Exception? innerException = null,
        ProviderFailureKind failureKind = ProviderFailureKind.Unknown)
        : base(provider, message, errorCode, providerErrorCode, innerException, failureKind)
    {
    }
}
